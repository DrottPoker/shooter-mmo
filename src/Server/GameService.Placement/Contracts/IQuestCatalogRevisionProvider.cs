using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Placement.Contracts;

// Lets Placement compare a worker's quest catalog with the one GameService mirrors, without
// depending on the module that owns it. Exactly one implementation is registered.
public interface IQuestCatalogRevisionProvider
{
    // The current mirrored quest catalog revision, or null before the catalog was seeded.
    Task<string?> GetCurrentRevisionAsync(
        PostgresTransactionScope scope,
        CancellationToken cancellationToken);
}
