using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Placement.Development;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Placement.Tests;

// One session heartbeat renews the leases of all the sessions of a worker runtime that it names,
// with the same statements for any number of sessions. Every session is answered on its own, in
// the request's order, so a refused session never fails the others, and the sessions are locked in
// ascending id order, as every statement that locks several sessions locks them.
public sealed class SimulationSessionHeartbeatIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";

    private static readonly TimeSpan MustFinishWithin = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MustStillWaitAfter = TimeSpan.FromSeconds(1.5);

    [PostgresIntegrationFact]
    public async Task AHeartbeatRenewsEveryValidSessionAndRefusesEachInvalidOneOnItsOwn()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var valid = await JoinAsync(context, "valid");
        var released = await JoinAsync(context, "released");
        var expired = await JoinAsync(context, "expired");
        var stolen = await JoinAsync(context, "stolen");
        var release = await context.SimulationSessionService.ReleaseAsync(
            released.SimulationSessionId,
            WorkerId,
            new SimulationSessionReleaseRequest(RuntimeId, released.SimulationSessionToken),
            CancellationToken.None);
        Assert.True(release.Succeeded, release.Error?.Message);
        await context.ExecuteAsync(
            $"update character_simulation_sessions set expires_at = created_at + interval '1 millisecond' where id = '{expired.SimulationSessionId}';");
        var expiresBefore = await ExpiriesAsync(context);
        var unknown = Guid.NewGuid();

        var heartbeat = await context.SimulationSessionService.HeartbeatAsync(
            WorkerId,
            new SimulationSessionHeartbeatRequest(
                RuntimeId,
                [
                    Entry(stolen) with { SessionToken = "a-token-that-proves-nothing" },
                    Entry(released),
                    new SimulationSessionHeartbeatEntry(unknown, "unknown-token"),
                    Entry(valid),
                    Entry(expired)
                ]),
            CancellationToken.None);

        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.Equal(
            [stolen.SimulationSessionId, released.SimulationSessionId, unknown, valid.SimulationSessionId, expired.SimulationSessionId],
            heartbeat.Value!.Sessions.Select(result => result.SimulationSessionId));
        var renewed = heartbeat.Value.Sessions[3].Lease;
        Assert.NotNull(renewed);
        Assert.Null(heartbeat.Value.Sessions[3].Error);
        Assert.Equal(valid.SimulationSessionId, renewed.SimulationSessionId);
        Assert.Equal(valid.CharacterId, renewed.CharacterId);
        Assert.Equal((ShardId, WorkerId, RuntimeId), (renewed.ShardId, renewed.WorkerId, renewed.WorkerRuntimeId));
        Assert.False(renewed.Released);
        Assert.Equal(60_000, renewed.LeaseDurationMilliseconds);
        Assert.Null(renewed.AccountSessionEnded);
        Assert.Equal(
            (valid.ItemStateRevision, valid.CarriedWeight, valid.CarryCapacity),
            (renewed.ItemStateRevision, renewed.CarriedWeight, renewed.CarryCapacity));
        foreach (var refused in heartbeat.Value.Sessions.Where((_, index) => index != 3))
        {
            Assert.Null(refused.Lease);
            Assert.Equal("invalid_simulation_session", refused.Error!.Code);
        }

        var expiresAfter = await ExpiriesAsync(context);
        Assert.True(expiresAfter[valid.SimulationSessionId] > expiresBefore[valid.SimulationSessionId]);
        Assert.Equal(renewed.ExpiresAt, expiresAfter[valid.SimulationSessionId]);
        foreach (var session in new[] { released, expired, stolen })
        {
            Assert.Equal(expiresBefore[session.SimulationSessionId], expiresAfter[session.SimulationSessionId]);
        }

        // The other runtime of the same worker renews none of them.
        var otherRuntime = await context.HeartbeatSessionAsync(
            valid.SimulationSessionId,
            valid.SimulationSessionToken,
            WorkerId,
            "another-runtime");
        Assert.Equal("invalid_simulation_session", otherRuntime.Error!.Code);
    }

    // The whole batch costs two statements and no transaction: the update of every lease, which
    // commits on its own, and then the read of every carry state.
    // The whole batch costs the same statements as one session: a short transaction that locks the
    // sessions and renews them, then the extension of their account sessions and the read of their
    // carry states, which both run after the renewal committed.
    [PostgresIntegrationFact]
    public async Task AHeartbeatOfManySessionsRunsFourStatements()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var sessions = new List<ConsumedSimulationJoinTicketResponse>();
        for (var index = 0; index < 5; index++)
        {
            sessions.Add(await JoinAsync(context, $"traced{index}"));
        }

        var recorder = new SqlTraceRecorder();
        using var loggerFactory = SqlTraceRecorder.CreateLoggerFactory(recorder);
        await using var tracedDataSource = new NpgsqlDataSourceBuilder(context.ConnectionString)
            .UseLoggerFactory(loggerFactory)
            .Build();
        var service = ActivatorUtilities.CreateInstance<SimulationSessionService>(context.Services, tracedDataSource);
        await using (await tracedDataSource.OpenConnectionAsync())
        {
            // The first connection loads the database types; the trace holds only the heartbeat.
        }

        recorder.Start();
        var heartbeat = await service.HeartbeatAsync(
            WorkerId,
            new SimulationSessionHeartbeatRequest(RuntimeId, sessions.Select(Entry).ToArray()),
            CancellationToken.None);
        var trace = recorder.Stop();

        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.All(heartbeat.Value!.Sessions, result => Assert.NotNull(result.Lease));
        string[] expected =
        [
            "c1 begin",
            "order by simulation_session.id for no key update of simulation_session",
            "update character_simulation_sessions set last_heartbeat_at",
            "c1 commit",
            "update account_sessions account_session set expires_at",
            "from character_item_states state where state.character_id = any("
        ];
        Assert.True(
            trace.Count == expected.Length
                && trace.Zip(expected).All(pair => pair.First.Contains(pair.Second, StringComparison.Ordinal)),
            string.Join(Environment.NewLine, trace));
    }

    // The batch takes its session locks in ascending id order: while it waits for the highest
    // session, it already holds the lower ones.
    [PostgresIntegrationFact]
    public async Task AHeartbeatLocksItsSessionsInAscendingIdOrder()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var sessions = new[]
        {
            await JoinAsync(context, "first"),
            await JoinAsync(context, "second"),
            await JoinAsync(context, "third")
        }.OrderBy(session => session.SimulationSessionId).ToArray();
        Task<ServiceResult<SimulationSessionHeartbeatResponse>> heartbeat;
        var releaseInFlight = await HeldSessionLock.TakeAsync(context, sessions[^1].SimulationSessionId);
        await using (releaseInFlight)
        {
            heartbeat = context.SimulationSessionService.HeartbeatAsync(
                WorkerId,
                new SimulationSessionHeartbeatRequest(
                    RuntimeId,
                    sessions.OrderByDescending(session => session.SimulationSessionId).Select(Entry).ToArray()),
                CancellationToken.None);
            var first = await Task.WhenAny(heartbeat, Task.Delay(MustStillWaitAfter));
            Assert.NotSame(heartbeat, first);

            Assert.True(await IsLockedAsync(context, sessions[0].SimulationSessionId));
            Assert.True(await IsLockedAsync(context, sessions[1].SimulationSessionId));
            await releaseInFlight.CommitAsync();
        }

        var renewed = await heartbeat.WaitAsync(MustFinishWithin);
        Assert.True(renewed.Succeeded, renewed.Error?.Message);
        Assert.All(renewed.Value!.Sessions, result => Assert.NotNull(result.Lease));
    }

    // A worker that goes offline releases its sessions in the same order, so it never deadlocks
    // with a heartbeat of its own sessions that is still on its way.
    [PostgresIntegrationFact]
    public async Task GoingOfflineLocksTheSessionsInAscendingIdOrder()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var sessions = new[]
        {
            await JoinAsync(context, "first"),
            await JoinAsync(context, "second")
        }.OrderBy(session => session.SimulationSessionId).ToArray();
        Task<ServiceResult<SimulationWorkerOfflineResponse>> offline;
        var heartbeatInFlight = await HeldSessionLock.TakeAsync(context, sessions[^1].SimulationSessionId);
        await using (heartbeatInFlight)
        {
            offline = context.SimulationWorkerRegistryService.MarkOfflineAsync(
                WorkerId,
                new SimulationWorkerOfflineRequest(RuntimeId),
                CancellationToken.None);
            var first = await Task.WhenAny(offline, Task.Delay(MustStillWaitAfter));
            Assert.NotSame(offline, first);

            Assert.True(await IsLockedAsync(context, sessions[0].SimulationSessionId));
            await heartbeatInFlight.CommitAsync();
        }

        var result = await offline.WaitAsync(MustFinishWithin);
        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(0, await context.ExecuteScalarIntAsync(
            "select count(*) from character_simulation_sessions where released_at is null;"));
    }

    // The sessions of development bots live in memory and share the heartbeat with the stored
    // sessions; every answer keeps its place in the request.
    [PostgresIntegrationFact]
    public async Task AHeartbeatRenewsDevelopmentBotSessionsBesideTheStoredOnes()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await JoinAsync(context, "player");
        var bots = new DevelopmentSimulationBotAuthority(
            new DevelopmentSimulationBotOptions(true, new string('s', 32), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), "Active Bot", 0),
            TimeProvider.System);
        var ticket = bots.IssueTicket(
            new DevelopmentSimulationBotTicketRequest(Guid.NewGuid(), 1, ShardId),
            new DevelopmentSimulationBotPlacement(ShardId, "development-world-1", WorkerId, RuntimeId, "127.0.0.1", 27015, 100, 0, 0, 0, 1, "simulation-revision", "collision-revision"));
        Assert.True(ticket.Succeeded, ticket.Error?.Message);
        Assert.True(bots.TryConsumeTicket(
            JoinTicketProofs.Consume(ticket.Value!, WorkerId, RuntimeId, ShardId),
            out var bot));
        var service = ActivatorUtilities.CreateInstance<SimulationSessionService>(context.Services, bots);

        var heartbeat = await service.HeartbeatAsync(
            WorkerId,
            new SimulationSessionHeartbeatRequest(RuntimeId, [Entry(bot.Value!), Entry(player)]),
            CancellationToken.None);

        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.Equal(
            [bot.Value!.SimulationSessionId, player.SimulationSessionId],
            heartbeat.Value!.Sessions.Select(result => result.Lease!.SimulationSessionId));
        Assert.Equal(30_000, heartbeat.Value.Sessions[0].Lease!.LeaseDurationMilliseconds);
        Assert.Equal(60_000, heartbeat.Value.Sessions[1].Lease!.LeaseDurationMilliseconds);
    }

    [PostgresIntegrationFact]
    public async Task AMalformedHeartbeatIsRefusedAsAWhole()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var session = await JoinAsync(context, "malformed");
        var entry = Entry(session);
        var tooMany = Enumerable.Range(0, SimulationSessionService.MaximumHeartbeatSessions + 1)
            .Select(_ => new SimulationSessionHeartbeatEntry(Guid.NewGuid(), "token"))
            .ToArray();

        (SimulationSessionHeartbeatRequest Request, string Code)[] cases =
        [
            (new SimulationSessionHeartbeatRequest(null, [entry]), "invalid_simulation_worker_identity"),
            (new SimulationSessionHeartbeatRequest("bad runtime", [entry]), "invalid_simulation_worker_identity"),
            (new SimulationSessionHeartbeatRequest(RuntimeId, null), SimulationSessionService.InvalidHeartbeatCode),
            (new SimulationSessionHeartbeatRequest(RuntimeId, []), SimulationSessionService.InvalidHeartbeatCode),
            (new SimulationSessionHeartbeatRequest(RuntimeId, tooMany), SimulationSessionService.InvalidHeartbeatCode),
            (new SimulationSessionHeartbeatRequest(RuntimeId, [entry, entry]), SimulationSessionService.InvalidHeartbeatCode),
            (new SimulationSessionHeartbeatRequest(RuntimeId, [entry with { SimulationSessionId = Guid.Empty }]), "invalid_simulation_session_id"),
            (new SimulationSessionHeartbeatRequest(RuntimeId, [entry, entry with { SimulationSessionId = Guid.NewGuid(), SessionToken = " " }]), "invalid_simulation_session_token")
        ];

        var expiresBefore = await ExpiriesAsync(context);
        foreach (var (request, code) in cases)
        {
            var refused = await context.SimulationSessionService.HeartbeatAsync(WorkerId, request, CancellationToken.None);

            Assert.Equal(400, refused.StatusCode);
            Assert.Equal(code, refused.Error!.Code);
        }

        Assert.Equal(expiresBefore, await ExpiriesAsync(context));
    }

    private static SimulationSessionHeartbeatEntry Entry(ConsumedSimulationJoinTicketResponse session)
    {
        return new SimulationSessionHeartbeatEntry(session.SimulationSessionId, session.SimulationSessionToken);
    }

    // A player with a character of the given name, joined on the worker runtime.
    private static async Task<ConsumedSimulationJoinTicketResponse> JoinAsync(
        PostgresIntegrationTestContext context,
        string name)
    {
        var player = await context.RegisterPlayerAsync($"{name}@example.com", $"{name}_player", $"{char.ToUpperInvariant(name[0])}{name[1..]} Hero");
        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await context.ShardService.ConsumeJoinTicketAsync(
            JoinTicketProofs.Consume(join.Value!, WorkerId, RuntimeId, ShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        return consumed.Value!;
    }

    private static async Task<Dictionary<Guid, DateTime>> ExpiriesAsync(PostgresIntegrationTestContext context)
    {
        var expiries = new Dictionary<Guid, DateTime>();
        await using var command = context.DataSource.CreateCommand(
            "select id, expires_at from character_simulation_sessions;");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            expiries.Add(reader.GetGuid(0), reader.GetDateTime(1));
        }

        return expiries;
    }

    // Whether another transaction holds a lock on the session that a key update would need.
    private static async Task<bool> IsLockedAsync(PostgresIntegrationTestContext context, Guid simulationSessionId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "select 1 from character_simulation_sessions where id = @Id for no key update nowait;",
            connection);
        command.Parameters.AddWithValue("Id", simulationSessionId);
        try
        {
            await command.ExecuteScalarAsync();
            return false;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return true;
        }
    }

    // An open transaction that holds one session's row lock, as a release in flight does.
    private sealed class HeldSessionLock : IAsyncDisposable
    {
        private readonly NpgsqlConnection connection;
        private readonly NpgsqlTransaction transaction;

        private HeldSessionLock(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            this.connection = connection;
            this.transaction = transaction;
        }

        public static async Task<HeldSessionLock> TakeAsync(PostgresIntegrationTestContext context, Guid simulationSessionId)
        {
            var connection = await context.DataSource.OpenConnectionAsync();
            var transaction = await connection.BeginTransactionAsync();
            var held = new HeldSessionLock(connection, transaction);
            await using var command = new NpgsqlCommand(
                "select 1 from character_simulation_sessions where id = @Id for no key update;",
                connection,
                transaction);
            command.Parameters.AddWithValue("Id", simulationSessionId);
            Assert.Equal(1, await command.ExecuteScalarAsync());
            return held;
        }

        public Task CommitAsync()
        {
            return transaction.CommitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
