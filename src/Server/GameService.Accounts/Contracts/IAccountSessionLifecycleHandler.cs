using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Accounts.Contracts;

// A login replaced every active session of the account.
public sealed record AccountSessionsReplaced(Guid AccountId);

// One account session was revoked by logout or a manual revoke.
public sealed record AccountSessionRevoked(Guid AccountId, Guid AccountSessionId, string Reason);

// Lets a higher module react to account session changes inside the session
// transaction, so access granted to an old session ends atomically with it. Exactly one
// implementation is registered.
public interface IAccountSessionLifecycleHandler
{
    Task OnSessionsReplacedAsync(
        PostgresTransactionScope scope,
        AccountSessionsReplaced sessionsReplaced,
        CancellationToken cancellationToken);

    Task OnSessionRevokedAsync(
        PostgresTransactionScope scope,
        AccountSessionRevoked sessionRevoked,
        CancellationToken cancellationToken);
}
