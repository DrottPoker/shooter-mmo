using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Placement.Integration;

// Ends simulation access in the same transaction that ends the account session. A pending join
// ticket of the ended session is consumed. A simulation session is not released but marked: the
// character's body stays in the world its full time on the worker, which renews the lease and
// releases it when the body goes, while everything a client asks for on the session is refused
// and the worker ends the connection at the next session heartbeat, or at the first refusal.
internal sealed class SimulationAccountSessionLifecycleHandler : IAccountSessionLifecycleHandler
{
    public async Task OnSessionsReplacedAsync(
        PostgresTransactionScope scope,
        AccountSessionsReplaced sessionsReplaced,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sessionsReplaced);

        const string markSimulationSessionsSql = """
            update character_simulation_sessions
            set account_session_ended_at = coalesce(account_session_ended_at, now())
            where account_id = @AccountId
              and released_at is null;
            """;

        await scope.Connection.ExecuteAsync(new CommandDefinition(
            markSimulationSessionsSql,
            new { sessionsReplaced.AccountId },
            scope.Transaction,
            cancellationToken: cancellationToken));

        const string revokeTicketsSql = """
            update simulation_join_tickets
            set consumed_at = now()
            where account_id = @AccountId
              and consumed_at is null;
            """;

        await scope.Connection.ExecuteAsync(new CommandDefinition(
            revokeTicketsSql,
            new { sessionsReplaced.AccountId },
            scope.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task OnSessionRevokedAsync(
        PostgresTransactionScope scope,
        AccountSessionRevoked sessionRevoked,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sessionRevoked);

        const string markSimulationSessionsSql = """
            update character_simulation_sessions
            set account_session_ended_at = coalesce(account_session_ended_at, now())
            where account_session_id = @SessionId and released_at is null;
            """;

        await scope.Connection.ExecuteAsync(new CommandDefinition(
            markSimulationSessionsSql,
            new { SessionId = sessionRevoked.AccountSessionId },
            scope.Transaction,
            cancellationToken: cancellationToken));

        const string revokeTicketsSql = """
            update simulation_join_tickets
            set consumed_at = now()
            where account_session_id = @SessionId and consumed_at is null;
            """;

        await scope.Connection.ExecuteAsync(new CommandDefinition(
            revokeTicketsSql,
            new { SessionId = sessionRevoked.AccountSessionId },
            scope.Transaction,
            cancellationToken: cancellationToken));
    }
}
