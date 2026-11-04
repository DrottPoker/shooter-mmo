using Dapper;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Quests.Contracts;

namespace Mmorpg.GameService.Quests;

// Stores quest log changes in the caller's transaction. Every write first raises the log
// revision, which also creates the log for a character's first quest.
internal sealed class CharacterQuestLog : ICharacterQuestLog
{
    public async Task<CharacterQuestLogState> ReadAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        using var reader = await scope.Connection.QueryMultipleAsync(new CommandDefinition(
            """
            select revision
            from character_quest_logs
            where character_id = @CharacterId;

            select quest_id as "QuestId",
                   step_id as "StepId",
                   accepted_at as "AcceptedAt"
            from character_active_quests
            where character_id = @CharacterId
            order by quest_id;

            select quest_id as "QuestId",
                   objective_id as "ObjectiveId",
                   progress as "Progress"
            from character_quest_progress
            where character_id = @CharacterId
            order by quest_id, objective_id;

            select quest_id as "QuestId",
                   completion_count as "CompletionCount",
                   last_completed_at as "LastCompletedAt"
            from character_completed_quests
            where character_id = @CharacterId
            order by quest_id;
            """,
            new { CharacterId = characterId },
            scope.Transaction,
            cancellationToken: cancellationToken));
        var revision = await reader.ReadSingleOrDefaultAsync<long?>() ?? 0;
        var active = (await reader.ReadAsync<ActiveQuestRow>()).ToArray();
        var progress = (await reader.ReadAsync<ProgressRow>())
            .ToLookup(row => row.QuestId, StringComparer.Ordinal);
        var completed = (await reader.ReadAsync<CompletedQuestState>()).ToArray();
        return new CharacterQuestLogState(
            revision,
            active
                .Select(row => new ActiveQuestState(
                    row.QuestId,
                    row.StepId,
                    row.AcceptedAt,
                    progress[row.QuestId].ToDictionary(
                        entry => entry.ObjectiveId,
                        entry => entry.Progress,
                        StringComparer.Ordinal)))
                .ToArray(),
            completed);
    }

    public async Task<long> AcceptAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string questId,
        string stepId,
        IReadOnlyList<string> objectiveIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireStep(questId, stepId, objectiveIds);
        var revision = await RaiseRevisionAsync(scope, characterId, cancellationToken);
        await scope.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into character_active_quests (character_id, quest_id, step_id)
            values (@CharacterId, @QuestId, @StepId);

            insert into character_quest_progress (character_id, quest_id, objective_id, progress)
            select @CharacterId, @QuestId, objective_id, 0
            from unnest(@ObjectiveIds) as objective_id;
            """,
            new
            {
                CharacterId = characterId,
                QuestId = questId,
                StepId = stepId,
                ObjectiveIds = objectiveIds.ToArray()
            },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return revision;
    }

    public async Task<long> SetProgressAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string questId,
        IReadOnlyDictionary<string, int> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentException.ThrowIfNullOrWhiteSpace(questId);
        var changes = progress.ToArray();
        RequireObjectiveIds(changes.Select(change => change.Key).ToArray());
        if (changes.Length == 0)
        {
            throw new ArgumentException("A progress change names at least one objective.", nameof(progress));
        }

        var revision = await RaiseRevisionAsync(scope, characterId, cancellationToken);
        var changed = await scope.Connection.ExecuteAsync(new CommandDefinition(
            """
            update character_quest_progress stored
            set progress = changes.progress
            from unnest(@ObjectiveIds, @Progress) as changes(objective_id, progress)
            where stored.character_id = @CharacterId
              and stored.quest_id = @QuestId
              and stored.objective_id = changes.objective_id;
            """,
            new
            {
                CharacterId = characterId,
                QuestId = questId,
                ObjectiveIds = changes.Select(change => change.Key).ToArray(),
                Progress = changes.Select(change => change.Value).ToArray()
            },
            scope.Transaction,
            cancellationToken: cancellationToken));
        if (changed != changes.Length)
        {
            throw new InvalidOperationException(
                $"Quest '{questId}' is not active with every changed objective in its current step.");
        }

        return revision;
    }

    public async Task<long> AdvanceAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string questId,
        string stepId,
        IReadOnlyList<string> objectiveIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireStep(questId, stepId, objectiveIds);
        var revision = await RaiseRevisionAsync(scope, characterId, cancellationToken);
        var changed = await scope.Connection.ExecuteAsync(new CommandDefinition(
            """
            update character_active_quests
            set step_id = @StepId
            where character_id = @CharacterId and quest_id = @QuestId;
            """,
            new { CharacterId = characterId, QuestId = questId, StepId = stepId },
            scope.Transaction,
            cancellationToken: cancellationToken));
        RequireActive(changed, questId);
        await scope.Connection.ExecuteAsync(new CommandDefinition(
            """
            delete from character_quest_progress
            where character_id = @CharacterId and quest_id = @QuestId;

            insert into character_quest_progress (character_id, quest_id, objective_id, progress)
            select @CharacterId, @QuestId, objective_id, 0
            from unnest(@ObjectiveIds) as objective_id;
            """,
            new
            {
                CharacterId = characterId,
                QuestId = questId,
                ObjectiveIds = objectiveIds.ToArray()
            },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return revision;
    }

    public async Task<long> CompleteAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string questId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(questId);
        var revision = await RaiseRevisionAsync(scope, characterId, cancellationToken);
        RequireActive(await DeleteActiveAsync(scope, characterId, questId, cancellationToken), questId);
        await scope.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into character_completed_quests (
                character_id,
                quest_id,
                completion_count,
                last_completed_at)
            values (@CharacterId, @QuestId, 1, now())
            on conflict (character_id, quest_id) do update
            set completion_count = character_completed_quests.completion_count + 1,
                last_completed_at = excluded.last_completed_at;
            """,
            new { CharacterId = characterId, QuestId = questId },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return revision;
    }

    public async Task<long> AbandonAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string questId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(questId);
        var revision = await RaiseRevisionAsync(scope, characterId, cancellationToken);
        RequireActive(await DeleteActiveAsync(scope, characterId, questId, cancellationToken), questId);
        return revision;
    }

    private static Task<long> RaiseRevisionAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        return scope.Connection.QuerySingleAsync<long>(new CommandDefinition(
            """
            insert into character_quest_logs (character_id, revision)
            values (@CharacterId, 1)
            on conflict (character_id) do update
            set revision = character_quest_logs.revision + 1
            returning revision;
            """,
            new { CharacterId = characterId },
            scope.Transaction,
            cancellationToken: cancellationToken));
    }

    // The progress rows go with the active quest.
    private static Task<int> DeleteActiveAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string questId,
        CancellationToken cancellationToken)
    {
        return scope.Connection.ExecuteAsync(new CommandDefinition(
            """
            delete from character_active_quests
            where character_id = @CharacterId and quest_id = @QuestId;
            """,
            new { CharacterId = characterId, QuestId = questId },
            scope.Transaction,
            cancellationToken: cancellationToken));
    }

    private static void RequireActive(int changedRows, string questId)
    {
        if (changedRows != 1)
        {
            throw new InvalidOperationException($"Quest '{questId}' is not active.");
        }
    }

    private static void RequireStep(string questId, string stepId, IReadOnlyCollection<string> objectiveIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        RequireObjectiveIds(objectiveIds);
    }

    private static void RequireObjectiveIds(IReadOnlyCollection<string> objectiveIds)
    {
        ArgumentNullException.ThrowIfNull(objectiveIds);
        if (objectiveIds.Any(string.IsNullOrWhiteSpace)
            || objectiveIds.Distinct(StringComparer.Ordinal).Count() != objectiveIds.Count)
        {
            throw new ArgumentException("Objective ids must be unique and not empty.", nameof(objectiveIds));
        }
    }

    private sealed record ActiveQuestRow(string QuestId, string StepId, DateTime AcceptedAt);

    private sealed record ProgressRow(string QuestId, string ObjectiveId, int Progress);
}
