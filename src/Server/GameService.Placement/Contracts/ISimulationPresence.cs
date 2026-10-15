using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Placement.Contracts;

// Whether characters are currently in the world, read in the caller's transaction.
public interface ISimulationPresence
{
    // Returns the lowest character id among the ids that has an unreleased, unexpired
    // simulation session, or null when none is in the world.
    Task<Guid?> FindActiveCharacterAsync(
        PostgresTransactionScope scope,
        IReadOnlyCollection<Guid> characterIds,
        CancellationToken cancellationToken);
}
