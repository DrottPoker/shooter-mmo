using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameProtocol;
using Mmorpg.GameService.Placement.Development;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Testing;
using Mmorpg.GameSimulation;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Placement.Tests;

// GameService decides the version question of a join before it issues a ticket: a client plays only
// on a worker with exactly its realtime protocol version and simulation revision, and exactly its
// World's collision revision when it names one. A refusal is a 409 with the code that the client
// shows a text for, issues and supersedes nothing, and is counted by Shard and reason.
public sealed class JoinVersionIntegrationTests
{
    private const string ShardId = "local-shard-1";
    private const string WorkerId = "local-simulation-worker-1";
    private const string ShardWorldId = "development-world-1";
    private const string WorkerCollisionRevision = "integration-collision-revision";

    [PostgresIntegrationFact]
    public async Task AClientOfTheWorkersVersionsGetsATicketAndIsNotCounted()
    {
        using var refusals = new RefusalCounter();
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(
                player.Character.Id,
                RealtimeProtocol.Version,
                GameSimulationCompatibility.Revision,
                ShardWorldId,
                WorkerCollisionRevision),
            CancellationToken.None);

        Assert.True(join.Succeeded, join.Error?.Message);
        Assert.Empty(refusals.Taken);
    }

    // A client from before the versions travelled in the join request sends none.
    [PostgresIntegrationFact]
    public async Task ARequestWithoutVersionsIsRefusedAsIncompatibleProtocolAndSupersedesNothing()
    {
        using var refusals = new RefusalCounter();
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var first = await JoinAsync(context, player);
        Assert.True(first.Succeeded, first.Error?.Message);

        var refused = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);

        AssertRefused(refused, "incompatible_protocol");
        Assert.Equal(
            1,
            await context.ExecuteScalarIntAsync("select count(*) from simulation_join_tickets;"));
        Assert.Equal(
            1,
            await context.ExecuteScalarIntAsync(
                $"select count(*) from simulation_join_tickets where id = '{first.Value!.JoinTicketId}' and consumed_at is null and transport_key is not null;"));
        Assert.Equal([(ShardId, "incompatible_protocol")], refusals.Taken);

        var again = await JoinAsync(context, player);
        Assert.True(again.Succeeded, again.Error?.Message);
    }

    [PostgresIntegrationFact]
    public async Task AWorkerOfOtherVersionsRefusesWithTheCodeThatNamesTheWayOut()
    {
        using var refusals = new RefusalCounter();
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var cases = new (string Case, Func<SimulationWorkerHeartbeatRequest, SimulationWorkerHeartbeatRequest> Worker, string Code)[]
        {
            ("a worker ahead of the client", worker => worker with { ProtocolVersion = RealtimeProtocol.Version + 1 }, "incompatible_protocol"),
            ("a worker behind the client", worker => worker with { ProtocolVersion = RealtimeProtocol.Version - 1 }, "shard_not_updated"),
            ("another simulation revision", worker => worker with { SimulationRevision = "another-simulation" }, "incompatible_simulation")
        };

        foreach (var (label, worker, code) in cases)
        {
            await HeartbeatAsync(context, worker);

            var refused = await JoinAsync(context, player);

            AssertRefused(refused, code, label);
        }

        Assert.Equal(
            cases.Select(entry => (ShardId, entry.Code)).ToArray(),
            refusals.Taken);
        Assert.Equal(
            0,
            await context.ExecuteScalarIntAsync("select count(*) from simulation_join_tickets;"));

        await HeartbeatAsync(context, worker => worker);
        var fits = await JoinAsync(context, player);
        Assert.True(fits.Succeeded, fits.Error?.Message);
    }

    [PostgresIntegrationFact]
    public async Task OtherCollisionDataForTheShardsWorldIsRefusedAndDataForAnotherWorldIsNot()
    {
        using var refusals = new RefusalCounter();
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var refused = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(
                player.Character.Id,
                RealtimeProtocol.Version,
                GameSimulationCompatibility.Revision,
                ShardWorldId,
                "another-collision-revision"),
            CancellationToken.None);
        AssertRefused(refused, "incompatible_world_data");
        Assert.Equal([(ShardId, "incompatible_world_data")], refusals.Taken);

        // A client whose Shard list is stale names a World that the Shard no longer runs; the
        // placement tells it so, not this check.
        var staleList = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(
                player.Character.Id,
                RealtimeProtocol.Version,
                GameSimulationCompatibility.Revision,
                "development-world-2",
                "another-collision-revision"),
            CancellationToken.None);
        Assert.True(staleList.Succeeded, staleList.Error?.Message);
        Assert.Single(refusals.Taken);
    }

    // The worker that is full still decides: a client that must update is told so, which a
    // matching client could not be told, because it only finds no room.
    [PostgresIntegrationFact]
    public async Task AFullWorkerStillRefusesAClientOfOtherVersionsWithItsVersionCode()
    {
        using var refusals = new RefusalCounter();
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        await HeartbeatAsync(context, worker => worker with { MaxConnections = 1 });
        var holder = await context.RegisterPlayerAsync();
        var holding = await JoinAsync(context, holder);
        Assert.True(holding.Succeeded, holding.Error?.Message);
        var other = await context.RegisterPlayerAsync("full@example.com", "full_player", "Full Hero");

        var matching = await JoinAsync(context, other);
        var outdated = await context.ShardService.CreateJoinTicketAsync(
            other.Account,
            ShardId,
            new JoinShardRequest(other.Character.Id, RealtimeProtocol.Version - 1, GameSimulationCompatibility.Revision),
            CancellationToken.None);

        AssertRefused(matching, "shard_unavailable");
        AssertRefused(outdated, "incompatible_protocol");
        Assert.Equal([(ShardId, "incompatible_protocol")], refusals.Taken);
    }

    // The worker that owns a character's session was replaced by a newer version: the client that
    // was not updated is told to update, and an updated one reconnects.
    [PostgresIntegrationFact]
    public async Task AReconnectIsJudgedByTheWorkersVersionsLikeAFirstJoin()
    {
        using var refusals = new RefusalCounter();
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var join = await JoinAsync(context, player);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await context.ShardService.ConsumeJoinTicketAsync(
            JoinTicketProofs.Consume(join.Value!, WorkerId, "integration-worker-runtime", ShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        await HeartbeatAsync(context, worker => worker with { ProtocolVersion = RealtimeProtocol.Version + 1 });

        var notUpdated = await JoinAsync(context, player);
        var updated = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(
                player.Character.Id,
                RealtimeProtocol.Version + 1,
                GameSimulationCompatibility.Revision),
            CancellationToken.None);

        AssertRefused(notUpdated, "incompatible_protocol");
        Assert.True(updated.Succeeded, updated.Error?.Message);
        Assert.True(updated.Value!.IsReconnect);
        Assert.Equal([(ShardId, "incompatible_protocol")], refusals.Taken);
    }

    // A Shard without a fresh worker has nothing to compare with; the placement says so.
    [PostgresIntegrationFact]
    public async Task AShardWithoutAFreshWorkerIsUnavailableWhateverTheRequestSays()
    {
        using var refusals = new RefusalCounter();
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        await context.ExecuteAsync("update simulation_workers set last_heartbeat_at = now() - interval '2 minutes';");

        var refused = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);

        AssertRefused(refused, "shard_unavailable");
        Assert.Empty(refusals.Taken);
    }

    [PostgresIntegrationFact]
    public async Task ADevelopmentBotIsHeldToTheWorkersVersionsLikeAPlayer()
    {
        using var refusals = new RefusalCounter();
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var service = new DevelopmentSimulationBotTicketService(
            context.Services.GetRequiredService<DevelopmentSimulationBotPlacementService>(),
            new DevelopmentSimulationBotAuthority(
                new DevelopmentSimulationBotOptions(
                    true,
                    new string('s', 32),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(30),
                    "Active Bot",
                    1),
                TimeProvider.System),
            context.Services.GetRequiredService<PlacementMetrics>());

        var withoutVersions = await service.CreateTicketAsync(
            new DevelopmentSimulationBotTicketRequest(Guid.NewGuid(), 1, ShardId),
            CancellationToken.None);
        var behind = await service.CreateTicketAsync(
            new DevelopmentSimulationBotTicketRequest(
                Guid.NewGuid(),
                2,
                ShardId,
                RealtimeProtocol.Version + 1,
                GameSimulationCompatibility.Revision),
            CancellationToken.None);
        var otherSimulation = await service.CreateTicketAsync(
            new DevelopmentSimulationBotTicketRequest(
                Guid.NewGuid(),
                3,
                ShardId,
                RealtimeProtocol.Version,
                "another-simulation"),
            CancellationToken.None);
        var fits = await service.CreateTicketAsync(
            new DevelopmentSimulationBotTicketRequest(
                Guid.NewGuid(),
                4,
                ShardId,
                RealtimeProtocol.Version,
                GameSimulationCompatibility.Revision),
            CancellationToken.None);

        AssertRefused(withoutVersions, "incompatible_protocol");
        AssertRefused(behind, "shard_not_updated");
        AssertRefused(otherSimulation, "incompatible_simulation");
        Assert.True(fits.Succeeded, fits.Error?.Message);
        Assert.Equal(
            [(ShardId, "incompatible_protocol"), (ShardId, "shard_not_updated"), (ShardId, "incompatible_simulation")],
            refusals.Taken);
    }

    private static Task<ServiceResult<JoinShardResponse>> JoinAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        return context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            PostgresIntegrationTestContext.CreateJoinRequest(player.Character.Id),
            CancellationToken.None);
    }

    private static async Task HeartbeatAsync(
        PostgresIntegrationTestContext context,
        Func<SimulationWorkerHeartbeatRequest, SimulationWorkerHeartbeatRequest> change)
    {
        var heartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
            WorkerId,
            change(PostgresIntegrationTestContext.CreateHeartbeatRequest()),
            CancellationToken.None);
        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
    }

    private static void AssertRefused<T>(ServiceResult<T> result, string code, string? label = null)
    {
        Assert.False(result.Succeeded, label);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Equal(code, result.Error!.Code);
    }

    // The refusals that the counter saw for the Shards of this test, in order.
    private sealed class RefusalCounter : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly List<(string ShardId, string Reason)> taken = [];

        public RefusalCounter()
        {
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == PlacementMetrics.MeterName
                    && instrument.Name == "game_service.placement.join.refusals")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                string? shard = null;
                string? reason = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == PlacementMetrics.ShardIdTag)
                    {
                        shard = tag.Value as string;
                    }
                    else if (tag.Key == PlacementMetrics.ReasonTag)
                    {
                        reason = tag.Value as string;
                    }
                }

                Assert.Equal(1, value);
                lock (taken)
                {
                    taken.Add((shard!, reason!));
                }
            });
            listener.Start();
        }

        public (string ShardId, string Reason)[] Taken
        {
            get
            {
                lock (taken)
                {
                    return taken.ToArray();
                }
            }
        }

        public void Dispose()
        {
            listener.Dispose();
        }
    }
}
