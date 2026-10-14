using Dapper;
using Npgsql;

namespace AuthService.Items;

public sealed class CharacterItemStateBootstrapper(NpgsqlDataSource dataSource)
{
    private const int RecomputeBatchSize = 500;

    // Startup work runs in short batches. One transaction over every account took one
    // advisory lock per account and failed with "out of shared memory" at scale.
    public async Task BackfillActiveCharactersAsync(CancellationToken cancellationToken)
    {
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            var missingCharacterIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select character.id
                from characters character
                left join character_item_states state on state.character_id = character.id
                where character.deleted_at is null
                  and state.character_id is null
                order by character.id;
                """,
                cancellationToken: cancellationToken))).ToArray();
            foreach (var characterId in missingCharacterIds)
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await BootstrapAsync(connection, transaction, characterId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }

        var afterCharacterId = Guid.Empty;
        while (true)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            // Locking the rows first makes a concurrent item transaction wait, so the
            // recompute can never overwrite a newer weight with a stale one.
            var batch = (await connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select state.character_id
                from character_item_states state
                join characters character on character.id = state.character_id
                where character.deleted_at is null
                  and state.character_id > @AfterCharacterId
                order by state.character_id
                limit @BatchSize
                for update of state;
                """,
                new { AfterCharacterId = afterCharacterId, BatchSize = RecomputeBatchSize },
                transaction,
                cancellationToken: cancellationToken))).ToArray();
            if (batch.Length == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            await RecomputeCarryStateAsync(connection, transaction, batch, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            afterCharacterId = batch[^1];
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

    private static Task RecomputeCarryStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] characterIds,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(new CommandDefinition(
            """
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
                  and bag.equipment_slot_id = 'bag'
                  and container.container_type = 'bag_contents'
                  and container.lifecycle = 'active'
            ),
            recalculated as (
                select
                    state.character_id,
                    coalesce((
                        select sum(definition.unit_weight * item.quantity)
                        from carried_containers carried
                        join item_instances item on item.container_id = carried.container_id
                        join item_definitions definition on definition.id = item.definition_id
                        where carried.character_id = state.character_id
                    ), 0) as carried_weight,
                    state.base_carry_capacity + coalesce((
                        select max(bag.carry_capacity_bonus)
                        from item_instances item
                        join bag_definitions bag on bag.definition_id = item.definition_id
                        where item.equipped_character_id = state.character_id
                          and item.equipment_slot_id = 'bag'
                    ), 0) as carry_capacity
                from character_item_states state
                where state.character_id = any(@CharacterIds)
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
            new { CharacterIds = characterIds },
            transaction,
            cancellationToken: cancellationToken));
    }
}
