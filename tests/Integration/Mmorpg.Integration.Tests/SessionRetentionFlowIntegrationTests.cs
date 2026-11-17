using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.Integration.Tests;

// The retention of account sessions in Accounts never deletes one that Placement's simulation
// sessions or join tickets still name: their foreign keys would set the name to null, and a
// session without an account session counts as unbound, not ended. The account session goes in a
// pass after Placement's retention deleted what names it.
public sealed class SessionRetentionFlowIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    [PostgresIntegrationFact]
    public async Task AnAccountSessionThatASimulationSessionNamesStaysAndItsEndStaysKnown()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        var session = await context.ShardService.ConsumeJoinTicketAsync(
            JoinTicketProofs.Consume(join.Value!, WorkerId, RuntimeId, ShardId),
            CancellationToken.None);
        Assert.True(session.Succeeded, session.Error?.Message);
        var simulationSessionId = session.Value!.SimulationSessionId;
        var logout = await context.SessionService.RevokeAsync(
            player.Registration.AccountId,
            player.Registration.SessionId,
            AccountSessionRevocationReason.Logout,
            CancellationToken.None);
        Assert.True(logout.Succeeded, logout.Error?.Message);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await ExpireAccountSessionAsync(connection, player.Registration.SessionId);

        // The body's session still names the ended account session, so it stays, and the
        // session authority still says how the account session ended.
        var held = await RetentionPasses.RunAsync(context, Day, Day);
        Assert.Equal(0, held.Task("account_sessions").Deleted);
        Assert.Equal(SimulationAccountSessionEnd.Ended, await AccountSessionEndAsync(context, simulationSessionId));

        var released = await context.SimulationSessionService.ReleaseAsync(
            simulationSessionId,
            WorkerId,
            new SimulationSessionReleaseRequest(RuntimeId, session.Value.SimulationSessionToken),
            CancellationToken.None);
        Assert.True(released.Succeeded, released.Error?.Message);
        await connection.ExecuteAsync(
            """
            update simulation_join_tickets
            set created_at = now() - interval '3 days',
                expires_at = now() - interval '3 days' + interval '30 seconds',
                consumed_at = now() - interval '3 days' + interval '1 second'
            where account_id = @AccountId;

            update character_simulation_sessions
            set created_at = now() - interval '3 days',
                last_heartbeat_at = now() - interval '3 days',
                account_session_ended_at = now() - interval '3 days',
                released_at = now() - interval '2 days'
            where id = @SimulationSessionId;
            """,
            new { player.Registration.AccountId, SimulationSessionId = simulationSessionId });

        // Accounts' task runs before Placement's in a pass, so the account session goes in the
        // pass after the rows that named it.
        var placement = await RetentionPasses.RunAsync(context, Day, Day);
        Assert.Equal(1, placement.Task("simulation_sessions").Deleted);
        Assert.Equal(0, placement.Task("account_sessions").Deleted);
        var accounts = await RetentionPasses.RunAsync(context, Day, Day);
        Assert.Equal(1, accounts.Task("account_sessions").Deleted);
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from account_sessions where id = @SessionId;",
                new { player.Registration.SessionId }));
    }

    private static async Task ExpireAccountSessionAsync(Npgsql.NpgsqlConnection connection, Guid accountSessionId)
    {
        await connection.ExecuteAsync(
            """
            update account_sessions
            set created_at = now() - interval '4 days',
                revoked_at = coalesce(revoked_at, now() - interval '3 days'),
                expires_at = now() - interval '2 days'
            where id = @SessionId;
            """,
            new { SessionId = accountSessionId });
    }

    private static async Task<SimulationAccountSessionEnd?> AccountSessionEndAsync(
        PostgresIntegrationTestContext context,
        Guid simulationSessionId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var authority = await context.Services.GetRequiredService<ISimulationSessionAuthority>()
            .GetReadAuthorityAsync(
                new PostgresTransactionScope(connection, transaction),
                simulationSessionId,
                CancellationToken.None);
        await transaction.RollbackAsync();
        return Assert.IsType<SimulationSessionReadAuthority>(authority).AccountSessionEnd;
    }
}
