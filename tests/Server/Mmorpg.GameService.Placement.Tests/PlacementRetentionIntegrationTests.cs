using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Placement.Tests;

// The retention tasks of session history in Placement: join tickets, simulation sessions, and
// SimulationAssignments, each counted from the moment it ended. Rows are made due by moving their
// end back past a one-day window.
public sealed class PlacementRetentionIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    [PostgresIntegrationFact]
    public async Task ALiveSessionAndAPendingTicketAreNeverDueAndAReleasedSessionCountsFromItsRelease()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var waiting = await context.RegisterPlayerAsync("waiting@example.com", "waiting_player", "Waiting Hero");
        var session = await JoinAsync(context, player.Account, player.Character.Id);
        var pending = await TicketAsync(context, waiting.Account, waiting.Character.Id);
        Assert.True(pending.Succeeded, pending.Error?.Message);
        await using var connection = await context.DataSource.OpenConnectionAsync();

        // A zero window makes every ended row due: the consumed ticket of the join goes, while the
        // live session and the pending ticket stay.
        var live = await RetentionPasses.RunAsync(context, Day, TimeSpan.Zero);
        Assert.Equal(1, live.Task("join_tickets").Deleted);
        Assert.Equal(0, live.Task("simulation_sessions").Deleted);
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from character_simulation_sessions where id = @SessionId;",
                new { SessionId = session.SimulationSessionId }));
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from simulation_join_tickets where consumed_at is null and account_id = @AccountId;",
                new { waiting.Account.AccountId }));

        var released = await context.SimulationSessionService.ReleaseAsync(
            session.SimulationSessionId,
            WorkerId,
            new SimulationSessionReleaseRequest(RuntimeId, session.SimulationSessionToken),
            CancellationToken.None);
        Assert.True(released.Succeeded, released.Error?.Message);
        await connection.ExecuteAsync(
            """
            update character_simulation_sessions
            set created_at = now() - interval '3 days',
                last_heartbeat_at = now() - interval '3 days',
                released_at = now() - interval '2 days'
            where id = @SessionId;
            """,
            new { SessionId = session.SimulationSessionId });

        var ended = await RetentionPasses.RunAsync(context, Day, Day);

        Assert.Equal(1, ended.Task("simulation_sessions").Deleted);
        Assert.InRange(ended.Task("simulation_sessions").Lag!.Value, TimeSpan.FromMinutes(1439), TimeSpan.FromMinutes(1441));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from character_simulation_sessions where id = @SessionId;",
                new { SessionId = session.SimulationSessionId }));
    }

    // A session whose lease expired unreleased ended at its expiry. One that a join ticket still
    // names waits for the ticket, whose key would refuse the delete.
    [PostgresIntegrationFact]
    public async Task AnExpiredSessionEndedAtItsExpiryAndWaitsForTheTicketThatNamesIt()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var session = await JoinAsync(context, player.Account, player.Character.Id);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            update character_simulation_sessions
            set created_at = now() - interval '3 days',
                last_heartbeat_at = now() - interval '3 days',
                expires_at = now() - interval '2 days'
            where id = @SessionId;
            """,
            new { SessionId = session.SimulationSessionId });

        // The consumed ticket ended today, so it is not due and holds the session.
        var held = await RetentionPasses.RunAsync(context, Day, Day);
        Assert.Equal(0, held.Task("simulation_sessions").Deleted);

        await connection.ExecuteAsync(
            """
            update simulation_join_tickets
            set created_at = now() - interval '3 days',
                expires_at = now() - interval '3 days' + interval '30 seconds',
                consumed_at = now() - interval '3 days' + interval '1 second'
            where simulation_session_id = @SessionId;
            """,
            new { SessionId = session.SimulationSessionId });
        var due = await RetentionPasses.RunAsync(context, Day, Day);

        Assert.Equal(1, due.Task("join_tickets").Deleted);
        Assert.Equal(1, due.Task("simulation_sessions").Deleted);
    }

    [PostgresIntegrationFact]
    public async Task AReleasedAssignmentGoesAndTheActiveOneStays()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var active = await connection.QuerySingleAsync<string>(
            "select id from simulation_assignments where released_at is null and shard_id = @ShardId;",
            new { ShardId });
        await connection.ExecuteAsync(
            """
            insert into simulation_assignments (id, worker_id, shard_id, assigned_at, released_at)
            values ('assignment-retention-old', @WorkerId, @ShardId, now() - interval '3 days', now() - interval '2 days');
            """,
            new { WorkerId, ShardId });

        var pass = await RetentionPasses.RunAsync(context, Day, TimeSpan.Zero);

        Assert.Equal(1, pass.Task("simulation_assignments").Deleted);
        Assert.Equal(
            new[] { active },
            (await connection.QueryAsync<string>("select id from simulation_assignments;")).ToArray());
    }

    private static Task<ServiceResult<JoinShardResponse>> TicketAsync(
        PostgresIntegrationTestContext context,
        AuthenticatedAccount account,
        Guid characterId)
    {
        return context.ShardService.CreateJoinTicketAsync(
            account,
            ShardId,
            new JoinShardRequest(characterId),
            CancellationToken.None);
    }

    private static async Task<ConsumedSimulationJoinTicketResponse> JoinAsync(
        PostgresIntegrationTestContext context,
        AuthenticatedAccount account,
        Guid characterId)
    {
        var join = await TicketAsync(context, account, characterId);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await context.ShardService.ConsumeJoinTicketAsync(
            new ConsumeSimulationJoinTicketRequest(join.Value!.JoinTicket, WorkerId, RuntimeId, ShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        return consumed.Value!;
    }
}
