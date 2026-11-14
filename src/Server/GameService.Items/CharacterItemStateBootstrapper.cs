using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Persistence;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Items;

internal sealed class CharacterItemStateBootstrapper(NpgsqlDataSource dataSource)
{
    private const int RecomputeBatchSize = 500;

    // Recomputes the carry weight and capacity of every active character when the item catalog
    // revision differs from the one the last completed recompute ran for, because the catalog
    // decides item weights and Bag capacities. Returns how many states changed, or null when
    // the recorded revision is current and nothing was read. Each batch is a short
    // transaction, so no transaction holds locks on every character.
    public async Task<int?> RecomputeCarryStateAsync(
        string catalogRevision,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogRevision);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var recorded = await ItemDataSteps.ReadAsync(
            connection,
            ItemDataSteps.CarryStateRecompute,
            cancellationToken);
        if (string.Equals(recorded, catalogRevision, StringComparison.Ordinal))
        {
            return null;
        }

        var changed = 0;
        var afterCharacterId = Guid.Empty;
        while (true)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            // Locking the rows first makes a concurrent item transaction wait, so the
            // recompute can never overwrite a newer weight with a stale one.
            var batch = (await connection.QueryAsync<Guid>(new CommandDefinition(
                $"""
                select state.character_id
                from character_item_states state
                join {CharactersSql.ActiveCharacters} character on character.id = state.character_id
                where state.character_id > @AfterCharacterId
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
                break;
            }

            changed += await RecomputeCarryStateAsync(connection, transaction, batch, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            afterCharacterId = batch[^1];
        }

        await ItemDataSteps.RecordAsync(
            connection,
            ItemDataSteps.CarryStateRecompute,
            catalogRevision,
            cancellationToken);
        return changed;
    }

    public Task BootstrapAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return scope.Connection.ExecuteAsync(new CommandDefinition(
            "select bootstrap_character_item_state(@CharacterId);",
            new { CharacterId = characterId },
            scope.Transaction,
            cancellationToken: cancellationToken));
    }

    // Uses the same carry rule as item transactions and writes only changed states.
    private static async Task<int> RecomputeCarryStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] characterIds,
        CancellationToken cancellationToken)
    {
        var states = await CarryStateCalculator.CalculateAsync(
            connection,
            transaction,
            characterIds,
            cancellationToken);
        var ids = states.Keys.ToArray();
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            update character_item_states state
            set carried_weight = recalculated.carried_weight,
                carry_capacity = recalculated.carry_capacity,
                revision = state.revision + 1,
                updated_at = now()
            from unnest(@CharacterIds, @CarriedWeights, @CarryCapacities)
                as recalculated(character_id, carried_weight, carry_capacity)
            where state.character_id = recalculated.character_id
              and (
                  state.carried_weight is distinct from recalculated.carried_weight
                  or state.carry_capacity is distinct from recalculated.carry_capacity);
            """,
            new
            {
                CharacterIds = ids,
                CarriedWeights = ids.Select(id => states[id].CarriedWeight).ToArray(),
                CarryCapacities = ids.Select(id => states[id].CarryCapacity).ToArray()
            },
            transaction,
            cancellationToken: cancellationToken));
    }
}
