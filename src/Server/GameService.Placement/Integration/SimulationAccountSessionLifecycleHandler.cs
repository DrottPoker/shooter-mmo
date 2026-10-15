using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Placement.Integration;

// Ends simulation access in the same transaction that ends the account session, so a
// replaced or revoked session can never keep a simulation lease or a join ticket.
public sealed class SimulationAccountSessionLifecycleHandler : IAccountSessionLifecycleHandler
{
    public async Task OnSessionsReplacedAsync(
        PostgresTransactionScope scope,
        AccountSessionsReplaced sessionsReplaced,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sessionsReplaced);

        const string releaseSimulationSessionsSql = """
            update character_simulation_sessions
            set released_at = coalesce(released_at, now())
            where account_id = @AccountId
              and released_at is null;
            """;

        await scope.Connection.ExecuteAsync(new CommandDefinition(
            releaseSimulationSessionsSql,
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

        const string releaseSimulationSessionsSql = """
            update character_simulation_sessions
            set released_at = coalesce(released_at, now())
            where account_session_id = @SessionId and released_at is null;
            """;

        await scope.Connection.ExecuteAsync(new CommandDefinition(
            releaseSimulationSessionsSql,
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
