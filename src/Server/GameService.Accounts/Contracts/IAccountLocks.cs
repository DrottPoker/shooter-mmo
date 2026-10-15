using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Accounts.Contracts;

// Row locks on accounts for other modules. Every cross-module transaction locks the
// account first, so all of them serialize per account in one order.
public interface IAccountLocks
{
    // Locks the account row FOR UPDATE in the caller's transaction. Returns false when
    // the account does not exist.
    Task<bool> LockForUpdateAsync(
        PostgresTransactionScope scope,
        Guid accountId,
        CancellationToken cancellationToken);
}
