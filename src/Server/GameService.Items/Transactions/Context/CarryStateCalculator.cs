using Dapper;
using Mmorpg.WorldData.Items;
using Npgsql;
using static Mmorpg.GameService.Items.ItemTransactionRejection;

namespace Mmorpg.GameService.Items.Transactions;

// Recomputes a character's carried weight and carry capacity from its carried
// containers and equipped Bag, rejecting arithmetic overflow.
internal sealed class CarryStateCalculator(ItemTransactionSession session)
{
    private NpgsqlConnection Connection => session.Connection;

    private NpgsqlTransaction Transaction => session.Transaction;

    public async Task<CalculatedCarryState> CalculateCarryStateAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        var baseCapacity = await Connection.QuerySingleAsync<long>(new CommandDefinition(
            """
            select base_carry_capacity
            from character_item_states
            where character_id = @CharacterId;
            """,
            new { CharacterId = characterId },
            Transaction,
            cancellationToken: cancellationToken));
        var bagBonus = await Connection.QuerySingleAsync<long>(new CommandDefinition(
            """
            select coalesce(max(bag.carry_capacity_bonus), 0)
            from item_instances item
            join bag_definitions bag on bag.definition_id = item.definition_id
            where item.equipped_character_id = @CharacterId
              and item.equipment_slot_id = 'bag';
            """,
            new { CharacterId = characterId },
            Transaction,
            cancellationToken: cancellationToken));
        var weights = await Connection.QueryAsync<CarriedItemWeightRow>(new CommandDefinition(
            """
            -- Each branch is an indexed lookup; an OR across joined tables scans every item.
            with carried_containers as (
                select state.permanent_inventory_container_id as container_id
                from character_item_states state
                where state.character_id = @CharacterId
                union
                select state.secure_container_id
                from character_item_states state
                where state.character_id = @CharacterId
                union
                select container.id
                from item_instances bag
                join item_containers container
                  on container.bound_bag_item_instance_id = bag.id
                where bag.equipped_character_id = @CharacterId
                  and bag.equipment_slot_id = 'bag'
                  and container.container_type = 'bag_contents'
                  and container.lifecycle = 'active'
            )
            select
                definition.unit_weight as "UnitWeight",
                item.quantity as "Quantity"
            from carried_containers carried
            join item_instances item on item.container_id = carried.container_id
            join item_definitions definition on definition.id = item.definition_id;
            """,
            new { CharacterId = characterId },
            Transaction,
            cancellationToken: cancellationToken));

        long carriedWeight;
        long carryCapacity;
        try
        {
            carriedWeight = ItemWeightRules.SumWeight(weights.Select(row =>
                ItemWeightRules.CalculateStackWeight(row.UnitWeight, row.Quantity)));
            carryCapacity = checked(baseCapacity + bagBonus);
        }
        catch (OverflowException)
        {
            Reject(
                ItemTransactionErrorCodes.CarryWeightLimitExceeded,
                "The operation would overflow authoritative carry-weight arithmetic.");
            throw;
        }

        return new CalculatedCarryState(carriedWeight, carryCapacity);
    }

    private sealed class CarriedItemWeightRow
    {
        public long UnitWeight { get; set; }

        public int Quantity { get; set; }
    }
}

internal sealed record CalculatedCarryState(
    long CarriedWeight,
    long CarryCapacity);
