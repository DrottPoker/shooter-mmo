using Dapper;
using Npgsql;

namespace AuthService.Items;

public sealed class CharacterItemStateBootstrapper(NpgsqlDataSource dataSource)
{
    private const long BootstrapLockId = 7_104_202_607_151_245;

    public async Task BackfillActiveCharactersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "select pg_advisory_xact_lock(@BootstrapLockId);",
                new { BootstrapLockId },
                transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                select pg_advisory_xact_lock(
                    hashtextextended(
                        'item-secure-entitlement:' || cast(account_id as text),
                        0))
                from (
                    select distinct account_id
                    from characters
                    where deleted_at is null
                    order by account_id
                ) active_accounts;

                select bootstrap_character_item_state(id)
                from characters
                where deleted_at is null
                order by id;

                with recalculated as (
                    select
                        state.character_id,
                        coalesce((
                            select sum(definition.unit_weight * item.quantity)
                            from item_instances item
                            join item_definitions definition
                              on definition.id = item.definition_id
                            left join item_containers container
                              on container.id = item.container_id
                            left join item_instances bound_bag
                              on bound_bag.id = container.bound_bag_item_instance_id
                            where item.container_id in (
                                    state.permanent_inventory_container_id,
                                    state.secure_container_id)
                               or (
                                    container.container_type = 'bag_contents'
                                    and container.lifecycle = 'active'
                                    and bound_bag.equipped_character_id = state.character_id
                                    and bound_bag.equipment_slot_id = 'bag')
                        ), 0) as carried_weight,
                        state.base_carry_capacity + coalesce((
                            select max(bag.carry_capacity_bonus)
                            from item_instances item
                            join bag_definitions bag
                              on bag.definition_id = item.definition_id
                            where item.equipped_character_id = state.character_id
                              and item.equipment_slot_id = 'bag'
                        ), 0) as carry_capacity
                    from character_item_states state
                    join characters character on character.id = state.character_id
                    where character.deleted_at is null
                )
                update character_item_states state
                set carried_weight = recalculated.carried_weight,
                    carry_capacity = recalculated.carry_capacity,
                    revision = state.revision + 1,
                    updated_at = now()
                from recalculated
                where state.character_id = recalculated.character_id
                  and (
                      state.carried_weight is distinct from recalculated.carried_weight
                      or state.carry_capacity is distinct from recalculated.carry_capacity);
                """,
                transaction: transaction,
                cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public Task BootstrapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(new CommandDefinition(
            """
            select pg_advisory_xact_lock(
                hashtextextended(
                    'item-secure-entitlement:' || cast(account_id as text),
                    0))
            from characters
            where id = @CharacterId;

            select bootstrap_character_item_state(@CharacterId);
            """,
            new { CharacterId = characterId },
            transaction,
            cancellationToken: cancellationToken));
    }
}
