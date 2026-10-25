using Dapper;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.WorldData.Characters;
using Npgsql;

namespace Mmorpg.GameService.Characters;

// Refuses to start when the appearance content no longer offers what a stored look names, such
// as a removed hairstyle, as the item catalog refuses a change that would invalidate items: such
// a change needs a data migration first.
internal sealed class CharacterAppearanceVerifier(
    NpgsqlDataSource dataSource,
    CharacterAppearanceCatalog catalog) : IDatabaseSeeder
{
    private const int ReportedCharacters = 5;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<(Guid CharacterId, string BodyModel, string Choices)>(new CommandDefinition(
            "select character_id, body_model, choices::text from character_appearances;",
            cancellationToken: cancellationToken));
        var broken = rows
            .Select(row => (row.CharacterId, Error: catalog.StorageError(
                row.BodyModel,
                CharacterAppearances.DeserializeChoices(row.Choices))))
            .Where(row => row.Error is not null)
            .ToArray();
        if (broken.Length > 0)
        {
            throw new InvalidOperationException(
                $"The character appearance content does not offer the stored looks of {broken.Length} characters, such as "
                + string.Join("; ", broken.Take(ReportedCharacters).Select(row => $"{row.CharacterId}: {row.Error}"))
                + ". Migrate the stored looks before removing what they name.");
        }
    }
}
