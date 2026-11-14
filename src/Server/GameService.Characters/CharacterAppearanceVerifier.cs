using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.WorldData.Characters;
using Npgsql;

namespace Mmorpg.GameService.Characters;

// Refuses to start when the appearance content no longer offers what a stored look names, such
// as a removed hairstyle, as the item catalog refuses a change that would invalidate items: such
// a change needs a data migration first. A look is checked when it is stored, so only a change
// of what the storage rule reads can break a stored one. The check therefore runs only when that
// revision differs from the one the last completed check ran for, and reads the looks in batches
// by character id, never all at once.
internal sealed class CharacterAppearanceVerifier : IDatabaseSeeder
{
    public const string StepId = "appearance_verification";

    // Raise when CharacterAppearanceCatalog.StorageError changes, so every look is checked again.
    private const int StorageRuleVersion = 1;
    private const int BatchSize = 1_000;
    private const int ReportedCharacters = 5;

    private readonly NpgsqlDataSource dataSource;
    private readonly CharacterAppearanceCatalog catalog;

    public CharacterAppearanceVerifier(NpgsqlDataSource dataSource, CharacterAppearanceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(catalog);
        this.dataSource = dataSource;
        this.catalog = catalog;
        StorageRevision = ComputeStorageRevision(catalog);
    }

    // A hash of everything CharacterAppearanceCatalog.StorageError reads: the body models, the
    // kinds, and every option that some body model offers for a kind and slot.
    public string StorageRevision { get; }

    public async Task<IReadOnlyList<string>> PlanAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var verified = await PostgresCatalog.TableExistsAsync(connection, "character_data_steps", cancellationToken)
            ? await ReadVerifiedRevisionAsync(connection, cancellationToken)
            : null;
        return string.Equals(verified, StorageRevision, StringComparison.Ordinal)
            ? []
            : [$"Character looks: check every stored look against appearance content {StorageRevision}; last checked against {verified ?? "none"}."];
    }

    public async Task<IReadOnlyList<string>> SeedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (string.Equals(
                await ReadVerifiedRevisionAsync(connection, cancellationToken),
                StorageRevision,
                StringComparison.Ordinal))
        {
            return [];
        }

        var checkedLooks = 0;
        var brokenCount = 0;
        var broken = new List<string>();
        var afterCharacterId = Guid.Empty;
        while (true)
        {
            var rows = (await connection.QueryAsync<(Guid CharacterId, string BodyModel, string Choices)>(new CommandDefinition(
                """
                select character_id, body_model, choices::text
                from character_appearances
                where character_id > @AfterCharacterId
                order by character_id
                limit @BatchSize;
                """,
                new { AfterCharacterId = afterCharacterId, BatchSize },
                cancellationToken: cancellationToken))).ToArray();
            if (rows.Length == 0)
            {
                break;
            }

            foreach (var row in rows)
            {
                var error = catalog.StorageError(row.BodyModel, CharacterAppearances.DeserializeChoices(row.Choices));
                if (error is null)
                {
                    continue;
                }

                brokenCount++;
                if (broken.Count < ReportedCharacters)
                {
                    broken.Add($"{row.CharacterId}: {error}");
                }
            }

            checkedLooks += rows.Length;
            afterCharacterId = rows[^1].CharacterId;
        }

        if (brokenCount > 0)
        {
            throw new InvalidOperationException(
                $"The character appearance content does not offer the stored looks of {brokenCount} characters, such as "
                + string.Join("; ", broken)
                + ". Migrate the stored looks before removing what they name.");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into character_data_steps (id, revision, completed_at)
            values (@StepId, @Revision, now())
            on conflict (id) do update
            set revision = excluded.revision,
                completed_at = excluded.completed_at;
            """,
            new { StepId, Revision = StorageRevision },
            cancellationToken: cancellationToken));
        return [$"Character looks: checked {checkedLooks} stored looks against appearance content {StorageRevision}."];
    }

    private static Task<string?> ReadVerifiedRevisionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        return connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "select revision from character_data_steps where id = @StepId;",
            new { StepId },
            cancellationToken: cancellationToken));
    }

    private static string ComputeStorageRevision(CharacterAppearanceCatalog content)
    {
        var lines = new List<string>
        {
            "appearance-storage-rule " + StorageRuleVersion.ToString(CultureInfo.InvariantCulture),
            "maximum-choices " + CharacterAppearanceFormat.MaximumOffers.ToString(CultureInfo.InvariantCulture)
        };
        lines.AddRange(content.BodyModels.Order(StringComparer.Ordinal).Select(bodyModel => "body " + bodyModel));
        lines.AddRange(content.Kinds.Select(kind => "kind " + kind.Id).Order(StringComparer.Ordinal));
        lines.AddRange(content.BodyModels
            .SelectMany(bodyModel => content.OffersFor(bodyModel))
            .SelectMany(offer => offer.Options.Select(option => $"offer {offer.Kind}|{offer.Slot}|{option}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }
}
