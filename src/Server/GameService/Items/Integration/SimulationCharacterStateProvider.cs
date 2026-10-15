using Dapper;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Items.Integration;

public sealed class SimulationCharacterStateProvider : ISimulationCharacterStateProvider
{
    public Task<SimulationCharacterItemState> LockForSessionStartAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return scope.Connection.QuerySingleAsync<SimulationCharacterItemState>(new CommandDefinition(
            """
            select revision as "ItemStateRevision",
                   carried_weight as "CarriedWeight",
                   carry_capacity as "CarryCapacity"
            from character_item_states
            where character_id = @CharacterId
            for update;
            """,
            new { CharacterId = characterId },
            scope.Transaction,
            cancellationToken: cancellationToken));
    }

    public Task<SimulationCharacterItemState?> GetAsync(
        NpgsqlConnection connection,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<SimulationCharacterItemState>(new CommandDefinition(
            """
            select revision as "ItemStateRevision",
                   carried_weight as "CarriedWeight",
                   carry_capacity as "CarryCapacity"
            from character_item_states
            where character_id = @CharacterId;
            """,
            new { CharacterId = characterId },
            cancellationToken: cancellationToken));
    }
}
