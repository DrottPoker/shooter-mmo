using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Placement.Contracts;

// The item state a SimulationWorker needs for a character's session: the carry state and
// what is equipped, read together at one item-state revision.
public sealed record SimulationCharacterItemState(
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity,
    IReadOnlyList<SimulationEquippedItem> Equipment);

// One occupied equipment slot, such as the equipped weapon or the arrow stack.
public sealed record SimulationEquippedItem(
    string EquipmentSlotId,
    Guid ItemInstanceId,
    string DefinitionId,
    int Quantity,
    long Revision);

// Lets Placement read the item-owned state without depending on the module that owns
// it. Exactly one implementation is registered.
public interface ISimulationCharacterStateProvider
{
    // Locks the character's item state FOR UPDATE in the session start transaction,
    // so the session starts from the latest committed item revision. The state must
    // exist.
    Task<SimulationCharacterItemState> LockForSessionStartAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken);

    // Reads the current item state in one statement outside a transaction, or null when
    // none exists.
    Task<SimulationCharacterItemState?> GetAsync(
        NpgsqlConnection connection,
        Guid characterId,
        CancellationToken cancellationToken);
}
