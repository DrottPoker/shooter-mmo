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
