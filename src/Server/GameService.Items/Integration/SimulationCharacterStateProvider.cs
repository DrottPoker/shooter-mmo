using System.Text.Json;
using Dapper;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Items.Integration;

// Reads a character's carry state and equipment in one statement, so both belong to the
// same item-state revision. Placement uses it for session start and heartbeats, and the
// simulation item endpoints return it after every operation.
internal sealed class SimulationCharacterStateProvider : ISimulationCharacterStateProvider
{
    private const string StateSql = """
        select
            state.revision as "ItemStateRevision",
            state.carried_weight as "CarriedWeight",
            state.carry_capacity as "CarryCapacity",
            coalesce(
                (
                    select json_agg(
                        json_build_object(
                            'equipmentSlotId', item.equipment_slot_id,
                            'itemInstanceId', item.id,
                            'definitionId', item.definition_id,
                            'quantity', item.quantity,
                            'revision', item.revision)
                        order by item.equipment_slot_id)
                    from item_instances item
                    where item.equipped_character_id = state.character_id
                ),
                '[]'::json)::text as "EquipmentJson"
        from character_item_states state
        where state.character_id = @CharacterId;
        """;

    private static readonly JsonSerializerOptions EquipmentJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SimulationCharacterItemState> LockForSessionStartAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await scope.Connection.ExecuteAsync(new CommandDefinition(
            "select 1 from character_item_states where character_id = @CharacterId for update;",
            new { CharacterId = characterId },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return await ReadAsync(scope.Connection, scope.Transaction, characterId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Character '{characterId}' has no item state.");
    }

    public Task<SimulationCharacterItemState?> GetAsync(
        NpgsqlConnection connection,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return ReadAsync(connection, null, characterId, cancellationToken);
    }

    private static async Task<SimulationCharacterItemState?> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition(
            StateSql,
            new { CharacterId = characterId },
            transaction,
            cancellationToken: cancellationToken));
        return row is null
            ? null
            : new SimulationCharacterItemState(
                row.ItemStateRevision,
                row.CarriedWeight,
                row.CarryCapacity,
                JsonSerializer.Deserialize<SimulationEquippedItem[]>(
                    row.EquipmentJson,
                    EquipmentJsonOptions) ?? []);
    }

    private sealed class StateRow
    {
        public long ItemStateRevision { get; set; }

        public long CarriedWeight { get; set; }

        public long CarryCapacity { get; set; }

        public string EquipmentJson { get; set; } = "[]";
    }
}
