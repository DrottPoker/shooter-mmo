using Dapper;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Items.Integration;

internal sealed class ItemCatalogRevisionProvider : IItemCatalogRevisionProvider
{
    public Task<string?> GetCurrentRevisionAsync(
        PostgresTransactionScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return scope.Connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "select revision from item_catalog_revisions where is_current;",
            transaction: scope.Transaction,
            cancellationToken: cancellationToken));
    }
}
