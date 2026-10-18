using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Placement.Contracts;

// Lets Placement compare a worker's item catalog with the catalog GameService mirrors,
// without depending on the module that owns it. Exactly one implementation is registered.
public interface IItemCatalogRevisionProvider
{
    // The current mirrored catalog revision, or null before the catalog was seeded.
    Task<string?> GetCurrentRevisionAsync(
        PostgresTransactionScope scope,
        CancellationToken cancellationToken);
}
