using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Placement.Contracts;

// The carry state a SimulationWorker needs for a character's session.
public sealed record SimulationCharacterItemState(
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity);

// Lets Placement read the item-owned carry state without depending on the module that
// owns it. Exactly one implementation is registered.
public interface ISimulationCharacterStateProvider
{
    // Locks the character's carry state FOR UPDATE in the session start transaction,
    // so the session starts from the latest committed item revision. The state must
    // exist.
    Task<SimulationCharacterItemState> LockForSessionStartAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken);

    // Reads the current carry state outside a transaction, or null when none exists.
    Task<SimulationCharacterItemState?> GetAsync(
        NpgsqlConnection connection,
        Guid characterId,
        CancellationToken cancellationToken);
}
