using Dapper;
using Mmorpg.WorldData.Items;
using Npgsql;
using static Mmorpg.GameService.Items.ItemTransactionRejection;

namespace Mmorpg.GameService.Items.Transactions;

// Recomputes carried weight and carry capacity from the carried containers and the equipped
// Bag. Item transactions and the startup recompute share this one rule.
internal sealed class CarryStateCalculator(ItemTransactionSession session)
{
    public async Task<CalculatedCarryState> CalculateCarryStateAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        try
        {
            var states = await CalculateAsync(
                session.Connection,
                session.Transaction,
                [characterId],
                cancellationToken);
            return states[characterId];
        }
        catch (OverflowException)
        {
            Reject(
                ItemTransactionErrorCodes.CarryWeightLimitExceeded,
                "The operation would overflow authoritative carry-weight arithmetic.");
            throw;
        }
    }

    // Returns the carry state of every listed character that has item state. Arithmetic
    // overflow throws OverflowException, so callers decide whether it is a rejection.
    public static async Task<IReadOnlyDictionary<Guid, CalculatedCarryState>> CalculateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] characterIds,
        CancellationToken cancellationToken)
    {
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            """
            -- Each branch is an indexed lookup; an OR across joined tables scans every item.
            with carried_containers as (
                select state.character_id, state.permanent_inventory_container_id as container_id
                from character_item_states state
                where state.character_id = any(@CharacterIds)
                union
                select state.character_id, state.secure_container_id
                from character_item_states state
                where state.character_id = any(@CharacterIds)
                union
                select bag.equipped_character_id, container.id
                from item_instances bag
                join item_containers container
                  on container.bound_bag_item_instance_id = bag.id
                where bag.equipped_character_id = any(@CharacterIds)
                  and bag.equipment_slot_id = @BagSlotId
                  and container.container_type = @BagContentsType
                  and container.lifecycle = 'active'
            )
            select
                carried.character_id as "CharacterId",
                definition.unit_weight as "UnitWeight",
                item.quantity as "Quantity"
            from carried_containers carried
            join item_instances item on item.container_id = carried.container_id
            join item_definitions definition on definition.id = item.definition_id;

            select
                state.character_id as "CharacterId",
                state.base_carry_capacity as "BaseCarryCapacity",
                coalesce((
                    select max(bag.carry_capacity_bonus)
                    from item_instances item
                    join bag_definitions bag on bag.definition_id = item.definition_id
                    where item.equipped_character_id = state.character_id
                      and item.equipment_slot_id = @BagSlotId
                ), 0) as "BagCarryCapacityBonus"
            from character_item_states state
            where state.character_id = any(@CharacterIds);
            """,
            new
            {
                CharacterIds = characterIds,
                BagSlotId = ItemEquipmentSlotIds.Bag,
                BagContentsType = ItemContainerTypeIds.BagContents
            },
            transaction,
            cancellationToken: cancellationToken));
        var weightsByCharacter = (await grid.ReadAsync<CarriedItemWeightRow>())
            .ToLookup(row => row.CharacterId);
        var capacities = await grid.ReadAsync<CarryCapacityRow>();
        return capacities.ToDictionary(
            row => row.CharacterId,
            row => new CalculatedCarryState(
                ItemWeightRules.SumWeight(weightsByCharacter[row.CharacterId].Select(weight =>
                    ItemWeightRules.CalculateStackWeight(weight.UnitWeight, weight.Quantity))),
                checked(row.BaseCarryCapacity + row.BagCarryCapacityBonus)));
    }

    private sealed class CarriedItemWeightRow
    {
        public Guid CharacterId { get; set; }

        public long UnitWeight { get; set; }

        public int Quantity { get; set; }
    }

    private sealed class CarryCapacityRow
    {
        public Guid CharacterId { get; set; }

        public long BaseCarryCapacity { get; set; }

        public long BagCarryCapacityBonus { get; set; }
    }
}

internal sealed record CalculatedCarryState(
    long CarriedWeight,
    long CarryCapacity);
