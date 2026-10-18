using Mmorpg.SimulationWorker.GameServiceApi;

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
}
