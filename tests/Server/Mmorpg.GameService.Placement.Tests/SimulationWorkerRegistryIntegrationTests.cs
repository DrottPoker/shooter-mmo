using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Placement.Tests;

// A worker runtime that GameService did not hear from for longer than the worker heartbeat
// timeout, while no other runtime took the worker id over, renews its registration with its
// next heartbeat: the same runtime keeps the Shard and its sessions, as a worker that comes back
// within its outage tolerance needs.
public sealed class SimulationWorkerRegistryIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";

    [PostgresIntegrationFact]
    public async Task AStaleRegistrationOfTheSameRuntimeRenewsAndKeepsItsSessions()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var session = await JoinAsync(context);
        await context.ExecuteAsync(
            "update simulation_workers set last_heartbeat_at = now() - interval '2 minutes';");
        var stale = await context.ShardService.ListShardsAsync(CancellationToken.None);
        Assert.False(Assert.Single(stale.Value!).IsOnline);

        var heartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
            WorkerId,
            PostgresIntegrationTestContext.CreateHeartbeatRequest(RuntimeId),
            CancellationToken.None);

        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.Equal((RuntimeId, ShardId), (heartbeat.Value!.RuntimeId, heartbeat.Value.ShardId));
        Assert.True(heartbeat.Value.OnlineUntil > DateTime.UtcNow);
        var renewed = await context.ShardService.ListShardsAsync(CancellationToken.None);
        Assert.True(Assert.Single(renewed.Value!).IsOnline);
        Assert.Equal(
            1,
            await context.ExecuteScalarIntAsync(
                "select count(*) from simulation_assignments where released_at is null;"));
        Assert.Equal(
            0,
            await context.ExecuteScalarIntAsync(
                $"select count(*) from character_simulation_sessions where id = '{session.SimulationSessionId}' and released_at is not null;"));
        var sessionHeartbeat = await context.SimulationSessionService.HeartbeatAsync(
            WorkerId,
            new SimulationSessionHeartbeatRequest(
                RuntimeId,
                [new SimulationSessionHeartbeatEntry(session.SimulationSessionId, session.SimulationSessionToken)]),
            CancellationToken.None);
        Assert.True(sessionHeartbeat.Succeeded, sessionHeartbeat.Error?.Message);
        Assert.NotNull(Assert.Single(sessionHeartbeat.Value!.Sessions).Lease);
    }

    private static async Task<ConsumedSimulationJoinTicketResponse> JoinAsync(PostgresIntegrationTestContext context)
    {
        var player = await context.RegisterPlayerAsync("returning@example.com", "returning_player", "Returning Hero");
        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            PostgresIntegrationTestContext.CreateJoinRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await context.ShardService.ConsumeJoinTicketAsync(
            JoinTicketProofs.Consume(join.Value!, WorkerId, RuntimeId, ShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        return consumed.Value!;
    }
}
