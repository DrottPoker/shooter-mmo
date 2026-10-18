using Mmorpg.GameService.Placement.Contracts;
using Npgsql;

namespace Mmorpg.GameService.Items;

// Reads the acting character's state after a committed simulation operation, so every
// worker-facing response tells the worker what the character carries and has equipped.
internal sealed class SimulationCharacterStates(
    NpgsqlDataSource dataSource,
    ISimulationCharacterStateProvider stateProvider)
{
    public async Task<SimulationCharacterStateResponse?> ReadAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var state = await stateProvider.GetAsync(connection, characterId, cancellationToken);
        return state is null
            ? null
            : new SimulationCharacterStateResponse(
                characterId,
                state.ItemStateRevision,
                state.CarriedWeight,
                state.CarryCapacity,
                state.Equipment);
    }
}
