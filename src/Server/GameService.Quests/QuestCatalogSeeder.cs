using Dapper;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.WorldData.Quests;
using Npgsql;

namespace Mmorpg.GameService.Quests;

// Mirrors the quest catalog's revision and quest identities into PostgreSQL before quest
// traffic is accepted. Quest ids are never removed: an id that leaves the catalog must be
// listed as retired, and a retired id never returns. A structural change, or a retirement,
// of a quest that characters have active stops startup until an explicit data migration
// makes the stored progress fit, because the progress names steps and objectives. A changed
// catalog may also lower a count, which moves the quests whose step it completes on.
internal sealed class QuestCatalogSeeder(
    NpgsqlDataSource dataSource,
    QuestDefinitions catalog,
    ICharacterQuestLog questLog,
    ILogger<QuestCatalogSeeder> logger) : IDatabaseSeeder
{
    private const long CatalogLockId = 7_104_202_611_042_200;
    private const string MigrationAdvice =
        "Apply an explicit data migration, or reset a local development database.";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? previousRevision;
        var advanced = 0;
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "select pg_advisory_xact_lock(@CatalogLockId);",
                new { CatalogLockId },
                transaction,
                cancellationToken: cancellationToken));
            var mirrored = (await connection.QueryAsync<MirroredQuestRow>(new CommandDefinition(
                    """
                    select id as "Id",
                           structural_fingerprint as "StructuralFingerprint",
                           retired_at is not null as "Retired",
                           exists (
                               select 1 from character_active_quests active
                               where active.quest_id = quest_definitions.id) as "Active"
                    from quest_definitions;
                    """,
                    transaction: transaction,
                    cancellationToken: cancellationToken)))
                .ToArray();
            EnsureCompatible(mirrored);
            previousRevision = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "select revision from quest_catalogs where catalog_id = @CatalogId;",
                new { catalog.CatalogId },
                transaction,
                cancellationToken: cancellationToken));
            await WriteAsync(connection, transaction, cancellationToken);
            if (!string.Equals(previousRevision, catalog.Revision, StringComparison.Ordinal))
            {
                advanced = await AdvanceCompletedStepsAsync(
                    new PostgresTransactionScope(connection, transaction),
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        if (!string.Equals(previousRevision, catalog.Revision, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Mirrored quest catalog {CatalogId} revision {CatalogRevision} with {QuestCount} quests and moved {AdvancedQuestCount} active quests past a step that it completes.",
                catalog.CatalogId,
                catalog.Revision,
                catalog.Quests.Count,
                advanced);
        }
    }

    // Only new progress moves a quest to its next step, so a step that a lowered count completes
    // would wait forever. Such a quest moves on here, as the progress that completed the step
    // would have moved it. A step before the last holds counted objectives only, so the stored
    // progress decides, and the next step starts without progress, so one move is enough.
    private async Task<int> AdvanceCompletedStepsAsync(
        PostgresTransactionScope scope,
        CancellationToken cancellationToken)
    {
        var questsWithSteps = catalog.Quests.Where(quest => quest.Steps.Length > 1).ToArray();
        if (questsWithSteps.Length == 0)
        {
            return 0;
        }

        var rows = await scope.Connection.QueryAsync<ActiveStepProgressRow>(new CommandDefinition(
            """
            select active.character_id as "CharacterId",
                   active.quest_id as "QuestId",
                   active.step_id as "StepId",
                   progress.objective_id as "ObjectiveId",
                   coalesce(progress.progress, 0) as "Progress"
            from character_active_quests active
            join unnest(@QuestIds, @LastStepIds) as quests(quest_id, last_step_id)
              on quests.quest_id = active.quest_id
            left join character_quest_progress progress
              on progress.character_id = active.character_id
             and progress.quest_id = active.quest_id
            where active.step_id <> quests.last_step_id
            order by active.character_id, active.quest_id, progress.objective_id;
            """,
            new
            {
                QuestIds = questsWithSteps.Select(quest => quest.Id).ToArray(),
                LastStepIds = questsWithSteps.Select(quest => quest.Steps[^1].Id).ToArray()
            },
            scope.Transaction,
            cancellationToken: cancellationToken));
        var advanced = 0;
        foreach (var active in rows.GroupBy(row => (row.CharacterId, row.QuestId, row.StepId)))
        {
            var quest = catalog.Find(active.Key.QuestId);
            var stepIndex = quest is null
                ? -1
                : Array.FindIndex(quest.Steps, step => string.Equals(step.Id, active.Key.StepId, StringComparison.Ordinal));
            if (quest is null || stepIndex < 0 || stepIndex == quest.Steps.Length - 1)
            {
                continue;
            }

            var progress = active
                .Where(row => row.ObjectiveId is not null)
                .ToDictionary(row => row.ObjectiveId!, row => row.Progress, StringComparer.Ordinal);
            if (!QuestRules.IsStepComplete(quest.Steps[stepIndex], objectiveId => progress.GetValueOrDefault(objectiveId)))
            {
                continue;
            }

            var next = quest.Steps[stepIndex + 1];
            await questLog.AdvanceAsync(
                scope,
                active.Key.CharacterId,
                quest.Id,
                next.Id,
                next.Objectives.Where(QuestRules.StoresProgress).Select(objective => objective.Id).ToArray(),
                cancellationToken);
            advanced++;
        }

        return advanced;
    }

    private void EnsureCompatible(IReadOnlyList<MirroredQuestRow> mirrored)
    {
        var retired = new HashSet<string>(catalog.RetiredQuestIds, StringComparer.Ordinal);
        foreach (var row in mirrored.OrderBy(row => row.Id, StringComparer.Ordinal))
        {
            var quest = catalog.Find(row.Id);
            if (quest is not null)
            {
                if (row.Retired)
                {
                    throw new QuestCatalogCompatibilityException(
                        $"Retired quest id '{row.Id}' cannot be reused.");
                }

                if (row.Active
                    && !string.Equals(row.StructuralFingerprint, quest.StructuralFingerprint, StringComparison.Ordinal))
                {
                    throw new QuestCatalogCompatibilityException(
                        $"Quest '{row.Id}' changed its steps or objectives while characters have it active. "
                        + MigrationAdvice);
                }

                continue;
            }

            if (!retired.Contains(row.Id))
            {
                throw new QuestCatalogCompatibilityException(
                    $"Existing quest id '{row.Id}' is missing from the quest catalog. "
                    + "List it as retired instead of removing it.");
            }

            if (row.Active)
            {
                throw new QuestCatalogCompatibilityException(
                    $"Quest '{row.Id}' is retired while characters have it active. " + MigrationAdvice);
            }
        }
    }

    private async Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into quest_catalogs (catalog_id, revision)
            values (@CatalogId, @Revision)
            on conflict (catalog_id) do update
            set revision = excluded.revision,
                mirrored_at = now()
            where quest_catalogs.revision <> excluded.revision;

            insert into quest_definitions (id, structural_fingerprint)
            select id, fingerprint
            from unnest(@QuestIds, @Fingerprints) as quests(id, fingerprint)
            on conflict (id) do update
            set structural_fingerprint = excluded.structural_fingerprint,
                mirrored_at = now()
            where quest_definitions.structural_fingerprint is distinct from excluded.structural_fingerprint;

            -- A retired id that this database never mirrored becomes a tombstone.
            insert into quest_definitions (id, structural_fingerprint, retired_at)
            select id, null, now()
            from unnest(@RetiredQuestIds) as retired(id)
            on conflict (id) do update
            set retired_at = now()
            where quest_definitions.retired_at is null;
            """,
            new
            {
                catalog.CatalogId,
                catalog.Revision,
                QuestIds = catalog.Quests.Select(quest => quest.Id).ToArray(),
                Fingerprints = catalog.Quests.Select(quest => quest.StructuralFingerprint).ToArray(),
                RetiredQuestIds = catalog.RetiredQuestIds.ToArray()
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private sealed record MirroredQuestRow(string Id, string? StructuralFingerprint, bool Retired, bool Active);

    private sealed record ActiveStepProgressRow(
        Guid CharacterId,
        string QuestId,
        string StepId,
        string? ObjectiveId,
        int Progress);
}
