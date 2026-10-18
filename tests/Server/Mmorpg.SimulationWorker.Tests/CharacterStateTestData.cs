using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;

namespace Mmorpg.SimulationWorker.Tests;

// The post-commit character state that every GameService mutation response carries.
internal static class CharacterStateTestData
{
    public static SimulationCharacterState Create(
        Guid characterId,
        long itemStateRevision,
        long carriedWeight = 0,
        long carryCapacity = 200,
        IReadOnlyList<SimulationEquippedItem>? equipment = null)
    {
        return new SimulationCharacterState(
            characterId,
            itemStateRevision,
            carriedWeight,
            carryCapacity,
            equipment ?? []);
    }

    // The worker-side item state for a carry state and the given equipment.
    public static CharacterItemState ItemState(PlayerCarryState carry, params SimulationEquippedItem[] equipment)
    {
        return CharacterItemState.Create(carry, equipment);
    }
}
