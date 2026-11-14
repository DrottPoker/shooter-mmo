using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Placement.Tests;

// The row locks of a worker heartbeat and of a join ticket, as the lock order in
// docs/GAME_SERVICE_MODULES.md describes them. Every test holds, in a transaction of its own,
// exactly the lock that another kind of work holds while it is in flight, and checks who has
// to wait for it. Before the renewal path existed, a heartbeat held the Shard row FOR UPDATE and
// then waited for the assignment FOR UPDATE, while a player death held the assignment FOR SHARE
// and then waited for the foreign key lock on the Shard row: a deadlock.
public sealed class ShardRowLockIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string ShardId = "local-shard-1";

    // Long enough for a slow test machine, far below the wait of a lock that is really held.
    private static readonly TimeSpan MustFinishWithin = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MustStillWaitAfter = TimeSpan.FromSeconds(1.5);

    [PostgresIntegrationFact]
    public async Task ARenewalAndAnItemOperationNeverWaitForEachOther()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        await using var itemOperation = await HeldLock.TakeAsync(
            context,
            "select id from simulation_assignments where shard_id = @ShardId and released_at is null for share;");

        var renewed = await HeartbeatAsync(context).WaitAsync(MustFinishWithin);

        Assert.True(renewed.Succeeded, renewed.Error?.Message);
        // The foreign key lock that the corpse of a player death takes on the Shard row.
        await itemOperation.ExecuteAsync(
            "set local lock_timeout = '10s'; select 1 from shards where id = @ShardId for key share;");
        await itemOperation.CommitAsync();
    }

    [PostgresIntegrationFact]
    public async Task ARenewalDoesNotWaitForARowOfTheShardInFlight()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        await using var foreignKeyInsert = await HeldLock.TakeAsync(
            context,
            "select 1 from shards where id = @ShardId for key share;");

        var renewed = await HeartbeatAsync(context).WaitAsync(MustFinishWithin);

        Assert.True(renewed.Succeeded, renewed.Error?.Message);
    }

    [PostgresIntegrationFact]
    public async Task AJoinTicketDoesNotWaitForARowOfTheShardInFlight()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        await using var foreignKeyInsert = await HeldLock.TakeAsync(
            context,
            "select 1 from shards where id = @ShardId for key share;");

        var join = await context.ShardService.CreateJoinTicketAsync(
                player.Account,
                ShardId,
                new JoinShardRequest(player.Character.Id),
                CancellationToken.None)
            .WaitAsync(MustFinishWithin);

        Assert.True(join.Succeeded, join.Error?.Message);
    }

    [PostgresIntegrationFact]
    public async Task AJoinTicketDoesNotWaitForASessionOfTheWorkerInFlight()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        // The foreign key lock that the session of a ticket redeem takes on the worker row.
        await using var foreignKeyInsert = await HeldLock.TakeAsync(
            context,
            "select 1 from simulation_workers where id = 'local-simulation-worker-1' for key share;");

        var join = await context.ShardService.CreateJoinTicketAsync(
                player.Account,
                ShardId,
                new JoinShardRequest(player.Character.Id),
                CancellationToken.None)
            .WaitAsync(MustFinishWithin);

        Assert.True(join.Succeeded, join.Error?.Message);
    }

    [PostgresIntegrationFact]
    public async Task TwoJoinsThatWaitForTheWorkerNeverTakeTheSameLastPlace()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var first = await context.RegisterPlayerAsync("first@example.com", "first_joiner", "First Joiner");
        var second = await context.RegisterPlayerAsync("second@example.com", "second_joiner", "Second Joiner");
        await context.ExecuteScalarIntAsync(
            "update simulation_workers set max_connections = 1 returning 1;");
        var joinInFlight = await HeldLock.TakeAsync(
            context,
            "select 1 from simulation_workers where id = 'local-simulation-worker-1' for no key update;");
        Task<ServiceResult<JoinShardResponse>>[] joins;
        await using (joinInFlight)
        {
            joins =
            [
                JoinAsync(context, first),
                JoinAsync(context, second)
            ];

            // Both wait for the worker row before either has counted the capacity.
            var early = await Task.WhenAny(Task.WhenAny(joins), Task.Delay(MustStillWaitAfter));
            Assert.False(joins[0].IsCompleted || joins[1].IsCompleted, early.ToString());
            await joinInFlight.CommitAsync();
        }

        var results = await Task.WhenAll(joins).WaitAsync(MustFinishWithin);

        Assert.Single(results, result => result.Succeeded);
        Assert.Equal("shard_unavailable", Assert.Single(results, result => !result.Succeeded).Error!.Code);
        Assert.Equal(1, await context.ExecuteScalarIntAsync(
            "select count(*) from simulation_join_tickets where consumed_at is null;"));
    }

    [PostgresIntegrationFact]
    public async Task ATakeoverNeverMissesTheRenewalItWaitedFor()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        // The worker's lease has run out, and its renewal is in flight: it has written the
        // worker row and holds the assignment, as TryRenewAsync does, but has not committed.
        await context.ExecuteScalarIntAsync(
            "update simulation_workers set last_heartbeat_at = now() - interval '10 minutes' returning 1;");
        var renewalInFlight = await HeldLock.TakeAsync(
            context,
            "update simulation_workers set last_heartbeat_at = now() where id = 'local-simulation-worker-1' returning 1;");
        Task<ServiceResult<SimulationWorkerHeartbeatResponse>> takeover;
        await using (renewalInFlight)
        {
            await renewalInFlight.ExecuteAsync(
                "select id from simulation_assignments where shard_id = @ShardId and released_at is null for share;");
            takeover = context.SimulationWorkerRegistryService.HeartbeatAsync(
                "another-simulation-worker",
                PostgresIntegrationTestContext.CreateHeartbeatRequest("another-worker-runtime"),
                CancellationToken.None);

            var first = await Task.WhenAny(takeover, Task.Delay(MustStillWaitAfter));
            Assert.NotSame(takeover, first);
            await renewalInFlight.CommitAsync();
        }

        var refused = await takeover.WaitAsync(MustFinishWithin);

        Assert.False(refused.Succeeded);
        Assert.Equal("shard_assignment_conflict", refused.Error!.Code);
        Assert.Equal(1, await context.ExecuteScalarIntAsync(
            "select count(*) from simulation_workers where id = 'local-simulation-worker-1' and is_online;"));
        Assert.Equal(1, await context.ExecuteScalarIntAsync(
            "select count(*) from simulation_assignments where released_at is null and worker_id = 'local-simulation-worker-1';"));
    }

    [PostgresIntegrationFact]
    public async Task ARenewalAnswersLikeTheRegistrationAndMovesTheLease()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var registered = await HeartbeatAsync(context, "another-runtime");
        Assert.True(registered.Succeeded, registered.Error?.Message);
        Assert.Equal(1, await CountSuccessorsAsync(context));

        var renewed = await HeartbeatAsync(context, "another-runtime", activeConnections: 7);

        Assert.True(renewed.Succeeded, renewed.Error?.Message);
        Assert.Equal(
            registered.Value! with { },
            renewed.Value! with
            {
                ActiveConnections = registered.Value.ActiveConnections,
                LastHeartbeatAt = registered.Value.LastHeartbeatAt,
                OnlineUntil = registered.Value.OnlineUntil
            });
        Assert.Equal(7, renewed.Value.ActiveConnections);
        Assert.True(renewed.Value.LastHeartbeatAt > registered.Value.LastHeartbeatAt);
        Assert.True(renewed.Value.OnlineUntil > registered.Value.OnlineUntil);
        // A renewal changes no assignment: the registration made one, and it is still the one.
        Assert.Equal(1, await CountSuccessorsAsync(context));
    }

    [PostgresIntegrationFact]
    public async Task AHeartbeatAfterGoingOfflineRegistersAgain()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var offline = await context.SimulationWorkerRegistryService.MarkOfflineAsync(
            WorkerId,
            new SimulationWorkerOfflineRequest("integration-worker-runtime"),
            CancellationToken.None);
        Assert.True(offline.Succeeded, offline.Error?.Message);
        Assert.Equal(0, await context.ExecuteScalarIntAsync(
            "select count(*) from simulation_assignments where released_at is null;"));

        var heartbeat = await HeartbeatAsync(context);

        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.Equal(1, await context.ExecuteScalarIntAsync(
            "select count(*) from simulation_assignments where released_at is null and worker_id = 'local-simulation-worker-1';"));
        Assert.Equal(1, await context.ExecuteScalarIntAsync(
            "select count(*) from simulation_workers where id = 'local-simulation-worker-1' and is_online;"));
    }

    [PostgresIntegrationFact]
    public async Task ANewRuntimeIsNeverTakenForARenewal()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        await context.ExecuteScalarIntAsync(
            "update simulation_workers set last_heartbeat_at = now() - interval '10 minutes' returning 1;");

        // The same worker with a new runtime and the old assignment still in place: only the
        // registration ends what the old runtime handed out.
        var restarted = await HeartbeatAsync(context, "restarted-runtime");

        Assert.True(restarted.Succeeded, restarted.Error?.Message);
        Assert.Equal("restarted-runtime", restarted.Value!.RuntimeId);
        Assert.Equal(0, await context.ExecuteScalarIntAsync(
            "select count(*) from simulation_join_tickets where consumed_at is null;"));
    }

    [PostgresIntegrationFact]
    public async Task AWorldRebindWaitsForARowOfTheShardInFlight()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var offline = await context.SimulationWorkerRegistryService.MarkOfflineAsync(
            WorkerId,
            new SimulationWorkerOfflineRequest("integration-worker-runtime"),
            CancellationToken.None);
        Assert.True(offline.Succeeded, offline.Error?.Message);
        var foreignKeyInsert = await HeldLock.TakeAsync(
            context,
            "select 1 from shards where id = @ShardId for key share;");
        await using (foreignKeyInsert)
        {
            var rebind = context.SimulationWorkerRegistryService.HeartbeatAsync(
                WorkerId,
                PostgresIntegrationTestContext.CreateHeartbeatRequest("rebind-runtime") with
                {
                    WorldId = "development-world-2"
                },
                CancellationToken.None);

            // The counts of what blocks a rebind are exact only while no row of the Shard can
            // appear, so the rebind waits for the one in flight.
            var first = await Task.WhenAny(rebind, Task.Delay(MustStillWaitAfter));
            Assert.NotSame(rebind, first);

            await foreignKeyInsert.CommitAsync();
            var rebound = await rebind.WaitAsync(MustFinishWithin);
            Assert.True(rebound.Succeeded, rebound.Error?.Message);
            Assert.Equal("development-world-2", rebound.Value!.WorldId);
        }
    }

    private static Task<ServiceResult<JoinShardResponse>> JoinAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        return context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
    }

    private static Task<ServiceResult<SimulationWorkerHeartbeatResponse>> HeartbeatAsync(
        PostgresIntegrationTestContext context,
        string runtimeId = "integration-worker-runtime",
        int activeConnections = 0)
    {
        return context.SimulationWorkerRegistryService.HeartbeatAsync(
            WorkerId,
            PostgresIntegrationTestContext.CreateHeartbeatRequest(runtimeId) with
            {
                ActiveConnections = activeConnections
            },
            CancellationToken.None);
    }

    // Every assignment the worker ever had on the Shard; a registration that replaces the
    // runtime keeps the open one, so this counts 1 until an assignment is released and made anew.
    private static Task<int> CountSuccessorsAsync(PostgresIntegrationTestContext context)
    {
        return context.ExecuteScalarIntAsync(
            "select count(*) from simulation_assignments where worker_id = 'local-simulation-worker-1';");
    }

    // An open transaction that holds one row lock, as work in flight does.
    private sealed class HeldLock : IAsyncDisposable
    {
        private readonly NpgsqlConnection connection;
        private readonly NpgsqlTransaction transaction;

        private HeldLock(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            this.connection = connection;
            this.transaction = transaction;
        }

        public static async Task<HeldLock> TakeAsync(PostgresIntegrationTestContext context, string sql)
        {
            var connection = await context.DataSource.OpenConnectionAsync();
            var transaction = await connection.BeginTransactionAsync();
            var held = new HeldLock(connection, transaction);
            Assert.Equal(1, await held.ExecuteAsync(sql));
            return held;
        }

        // Returns the number of rows the last statement of the command read.
        public async Task<int> ExecuteAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            if (sql.Contains("@ShardId", StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue("ShardId", ShardId);
            }

            var rows = 0;
            await using var reader = await command.ExecuteReaderAsync();
            do
            {
                rows = 0;
                while (await reader.ReadAsync())
                {
                    rows++;
                }
            }
            while (await reader.NextResultAsync());

            return rows;
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
