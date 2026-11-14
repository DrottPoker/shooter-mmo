using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Accounts.Persistence;

// Migrations owned by Accounts. Its tables were created by the host's frozen baseline; the host
// merges these with every other module's by id.
internal static class AccountsSchemaMigrations
{
    public static IReadOnlyList<DatabaseMigration> All { get; } =
    [
        new(AccountSessionRetentionIndexMigration.Id, AccountSessionRetentionIndexMigration.Sql)
    ];
}
