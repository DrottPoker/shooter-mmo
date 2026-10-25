using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Characters.Persistence;

// Migrations owned by Characters. The host merges them with every other module's by id.
internal static class CharactersSchemaMigrations
{
    public static IReadOnlyList<DatabaseMigration> All { get; } =
    [
        new(CharacterLevelMigration.Id, CharacterLevelMigration.Sql),
        new(CharacterCurrencyLedgerMigration.Id, CharacterCurrencyLedgerMigration.Sql),
        new(CharacterAppearanceMigration.Id, CharacterAppearanceMigration.Sql)
    ];
}
