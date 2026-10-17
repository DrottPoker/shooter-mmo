using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Items;

internal sealed class CharacterItemStateBootstrapper(NpgsqlDataSource dataSource)
{
    private const int RecomputeBatchSize = 500;

    // Startup work runs in short batches. One transaction over every account took one
    // advisory lock per account and failed with "out of shared memory" at scale.
    public async Task BackfillActiveCharactersAsync(CancellationToken cancellationToken)
    {
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            var missingCharacters = (await connection.QueryAsync<MissingCharacterRow>(
                new CommandDefinition(
                    $"""
                    select
                        character.id as "CharacterId",
                        character.account_id as "AccountId"
                    from {CharactersSql.ActiveCharacters} character
                    left join character_item_states state on state.character_id = character.id
                    where state.character_id is null
                    order by character.id;
                    """,
                    cancellationToken: cancellationToken))).ToArray();
            foreach (var missing in missingCharacters)
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await BootstrapAsync(
                    new PostgresTransactionScope(connection, transaction),
                    missing.CharacterId,
                    missing.AccountId,
                    cancellationToken);
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
                return;
            }

            await RecomputeCarryStateAsync(connection, transaction, batch, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            afterCharacterId = batch[^1];
        }
    }

    // The account's Secure Container entitlement lock is taken first, in the same order
    // as every other entitlement change. The caller supplies the character's account.
    public Task BootstrapAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return scope.Connection.ExecuteAsync(new CommandDefinition(
            """
            select pg_advisory_xact_lock(
                hashtextextended(
                    'item-secure-entitlement:' || cast(@AccountId as text),
                    0));

            select bootstrap_character_item_state(@CharacterId);
            """,
            new { CharacterId = characterId, AccountId = accountId },
            scope.Transaction,
            cancellationToken: cancellationToken));
    }

    // Uses the same carry rule as item transactions and writes only changed states.
    private static async Task RecomputeCarryStateAsync(
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
        await connection.ExecuteAsync(new CommandDefinition(
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

    private sealed record MissingCharacterRow(Guid CharacterId, Guid AccountId);
}
