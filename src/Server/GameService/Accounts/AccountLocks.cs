using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Accounts;

public sealed class AccountLocks : IAccountLocks
{
    public async Task<bool> LockForUpdateAsync(
        PostgresTransactionScope scope,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var lockedAccountId = await scope.Connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                "select id from accounts where id = @AccountId for update;",
                new { AccountId = accountId },
                scope.Transaction,
                cancellationToken: cancellationToken));
        return lockedAccountId is not null;
    }
}
