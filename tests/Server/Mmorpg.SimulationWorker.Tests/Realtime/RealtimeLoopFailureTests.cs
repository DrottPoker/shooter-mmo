using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.WorldCollision;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A failure that concerns one player or one peer takes only that one out of play, and the test
// harness fails a test whose loop failed without the test expecting it.
public sealed class RealtimeLoopFailureTests
{
    // The movement step of one player throws: its session ends with internal_error, so the peer
    // supervisor disconnects its peer and removes its entity, and the session is then released
    // once without a final state, while the other players moved in the same tick, keep their
    // sessions, and the movement phase itself does not count as failed.
    [Fact]
    public async Task APlayerWhoseMovementThrowsLeavesAloneAndTheOthersMove()
    {
        using var fixture = new RealtimeLoopFixture();
        var broken = new ProbeCollisionWorld(fixture.CollisionWorld);
        var firstWalker = new ProbeCollisionWorld(fixture.CollisionWorld);
        var secondWalker = new ProbeCollisionWorld(fixture.CollisionWorld);
        var walkerBefore = fixture.ConnectPlayer(1, "Walker Before", firstWalker);
        var failing = fixture.ConnectPlayer(2, "Broken Walker", broken);
        var walkerAfter = fixture.ConnectPlayer(3, "Walker After", secondWalker);

        // A halted character steps its physics every tick.
        foreach (var player in new[] { walkerBefore, failing, walkerAfter })
        {
            player.Entity.Movement.Halt();
        }

        var phases = new SimulationTickPhases();
        new PlayerMovementTickPhase(fixture.CarrySync, fixture.PerformanceMetrics, fixture.FailureIsolation)
            .AddTickPhase(phases);
        var loop = new SimulationTickLoop(
            fixture.Clock,
            fixture.Entities,
            phases,
            fixture.PerformanceMetrics,
            fixture.LoopGuard,
            fixture.Logger);
        broken.Fail = true;
        var nextTick = TimeSpan.Zero;

        loop.ProcessDueTicks(nextTick, TimeSpan.FromMilliseconds(1000d / 30d), ref nextTick);

        Assert.True(firstWalker.Queries > 0);
        Assert.True(secondWalker.Queries > 0);
        Assert.Equal(1, fixture.LoopGuard.TotalFailures);
        var interval = fixture.PerformanceMetrics.CaptureAndReset();
        Assert.Equal(0, interval.LoopStepFailures);
        Assert.Equal(1, interval.LoopIsolatedFailures);
        var error = Assert.Single(fixture.Logger.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.Contains(
            $"movement of player entity {failing.Entity.NetworkEntityId}",
            error.Message,
            StringComparison.Ordinal);
        Assert.False(fixture.SessionStore.IsCurrent(failing.Session, DateTime.UtcNow));
        Assert.True(fixture.SessionStore.IsCurrent(walkerBefore.Session, DateTime.UtcNow));

        var supervision = SupervisorService.Create(fixture);
        supervision.Supervisor.EnforcePeerState();

        var disconnect = Assert.Single(failing.Peer.Sent, sent => sent.Type == RealtimeMessageType.ServerDisconnect);
        Assert.True(RealtimeProtocol.TryDecodeServerDisconnect(disconnect.Data, out var reason, out _));
        Assert.Equal("internal_error", reason.Code);
        Assert.NotNull(failing.Context.PendingDisconnect);
        Assert.Null(failing.Context.Session);
        Assert.False(fixture.Entities.TryGetPlayer(failing.Entity.NetworkEntityId, out _));
        await fixture.OperationTracker.WaitForAllAsync();
        var release = Assert.Single(fixture.GameService.Releases);
        Assert.Equal(failing.Session.SimulationSessionId, release.SimulationSessionId);
        Assert.Null(release.Request.FinalState);

        // A second supervision pass releases nothing more.
        supervision.Supervisor.EnforcePeerState();
        await fixture.OperationTracker.WaitForAllAsync();
        Assert.Single(fixture.GameService.Releases);
        foreach (var other in new[] { walkerBefore, walkerAfter })
        {
            Assert.Null(other.Context.PendingDisconnect);
            Assert.DoesNotContain(other.Peer.Sent, sent => sent.Type == RealtimeMessageType.ServerDisconnect);
            Assert.True(fixture.Entities.TryGetPlayer(other.Entity.NetworkEntityId, out _));
            Assert.True(fixture.SessionStore.IsCurrent(other.Session, DateTime.UtcNow));
        }
    }

    // The despawn of a removed player cannot be sent to one of the peers that see it: that
    // peer is disconnected alone, the other peers still get the despawn, and the removal hooks
    // run, so the session of the removed player is released.
    [Fact]
    public async Task ADespawnThatFailsForOnePeerStillFinishesTheRemoval()
    {
        using var fixture = new RealtimeLoopFixture();
        var blocked = fixture.ConnectPlayer(1, "Blocked Viewer");
        var failing = fixture.ConnectPlayer(2, "Removed Player");
        var viewer = fixture.ConnectPlayer(3, "Other Viewer");
        var mobs = new MobSystem(
            fixture.Config,
            fixture.Clock,
            fixture.CollisionWorld,
            new CombatantRegistry(),
            new CombatLineOfSight(fixture.CollisionWorld),
            fixture.Presence,
            null,
            null);
        var replicator = new SnapshotReplicator(
            fixture.Config,
            mobs,
            fixture.Entities,
            fixture.Bindings,
            fixture.Registry,
            fixture.Sender,
            fixture.Presence,
            fixture.Metrics,
            fixture.PerformanceMetrics,
            fixture.Clock,
            fixture.FailureIsolation);
        var interest = new InterestRefresher(fixture.Entities, fixture.Presence, fixture.PerformanceMetrics);
        // Every peer sees the others once each connection had its visibility turn.
        for (var broadcast = 0; broadcast < fixture.Config.InterestManagement.RefreshIntervalSnapshots + 1; broadcast++)
        {
            interest.Refresh();
            replicator.Broadcast();
        }

        fixture.FailureIsolation.RemovePlayer(
            PlayerMovementTickPhase.Name,
            failing.Entity,
            new InvalidOperationException("The step broke."));
        blocked.Peer.FailReliableSends = true;
        var supervision = SupervisorService.Create(fixture);

        var failuresBefore = fixture.LoopGuard.TotalFailures;
        supervision.Supervisor.EnforcePeerState();

        Assert.False(fixture.Entities.TryGetPlayer(failing.Entity.NetworkEntityId, out _));
        Assert.Contains(viewer.Peer.Sent, sent => sent.Type == RealtimeMessageType.EntityDespawn);
        Assert.NotNull(blocked.Context.PendingDisconnect);
        Assert.Null(viewer.Context.PendingDisconnect);
        Assert.Equal(failuresBefore + 1, fixture.LoopGuard.TotalFailures);
        Assert.Contains(
            fixture.Logger.Entries,
            entry => entry.Level == LogLevel.Error
                && entry.Message.Contains("send failed for UDP peer 1", StringComparison.Ordinal));
        await fixture.OperationTracker.WaitForAllAsync();
        var release = Assert.Single(fixture.GameService.Releases);
        Assert.Equal(failing.Session.SimulationSessionId, release.SimulationSessionId);
        Assert.Null(release.Request.FinalState);
    }

    // A removal hook that throws does not skip the hooks after it, and its exception still
    // leaves the call.
    [Fact]
    public void ARemovalHookThatThrowsDoesNotSkipTheHooksAfterIt()
    {
        var hooks = new RealtimeLifecycleHooks();
        var ran = new List<string>();
        hooks.AddPlayerRemoved((_, _, _) => ran.Add("registered first"));
        hooks.AddPlayerRemoved((_, _, _) => throw new InvalidOperationException("The hook broke."));
        hooks.AddPlayerRemoved((_, _, _) => ran.Add("registered last"));

        var failure = Assert.Throws<InvalidOperationException>(() => hooks.RaisePlayerRemoved(
            RealtimePeerContext.System,
            RealtimeTestSessions.Create("Hook Player"),
            7));

        Assert.Equal("The hook broke.", failure.Message);
        Assert.Equal(["registered last", "registered first"], ran);
    }

    // Sending to one recipient throws: only that peer is disconnected with internal_error, and
    // the recipients before and after it still get their snapshots.
    [Fact]
    public void ARecipientWhoseSnapshotFailsIsDisconnectedAloneAndTheOthersGetTheirs()
    {
        using var fixture = new RealtimeLoopFixture();
        var first = fixture.ConnectPlayer(1, "First Viewer");
        var failing = fixture.ConnectPlayer(2, "Failing Viewer");
        var last = fixture.ConnectPlayer(3, "Last Viewer");
        failing.Peer.FailUnreliableSends = true;
        var mobs = new MobSystem(
            fixture.Config,
            fixture.Clock,
            fixture.CollisionWorld,
            new CombatantRegistry(),
            new CombatLineOfSight(fixture.CollisionWorld),
            fixture.Presence,
            null,
            null);
        var replicator = new SnapshotReplicator(
            fixture.Config,
            mobs,
            fixture.Entities,
            fixture.Bindings,
            fixture.Registry,
            fixture.Sender,
            fixture.Presence,
            fixture.Metrics,
            fixture.PerformanceMetrics,
            fixture.Clock,
            fixture.FailureIsolation);

        // Visibility refreshes one connection per snapshot in turn, so remote snapshots follow
        // within one refresh interval.
        var interest = new InterestRefresher(fixture.Entities, fixture.Presence, fixture.PerformanceMetrics);
        for (var broadcast = 0; broadcast < fixture.Config.InterestManagement.RefreshIntervalSnapshots + 1; broadcast++)
        {
            interest.Refresh();
            replicator.Broadcast();
        }

        foreach (var other in new[] { first, last })
        {
            Assert.Contains(other.Peer.Sent, sent => sent.Type == RealtimeMessageType.OwnerSimulationSnapshot);
            Assert.Contains(other.Peer.Sent, sent => sent.Type == RealtimeMessageType.SimulationSnapshot);
            Assert.Null(other.Context.PendingDisconnect);
        }

        var disconnect = Assert.Single(failing.Peer.Sent, sent => sent.Type == RealtimeMessageType.ServerDisconnect);
        Assert.True(RealtimeProtocol.TryDecodeServerDisconnect(disconnect.Data, out var reason, out _));
        Assert.Equal("internal_error", reason.Code);
        Assert.NotNull(failing.Context.PendingDisconnect);
        Assert.DoesNotContain(failing.Peer.Sent, sent => sent.Delivery == DeliveryMethod.Unreliable);
        // The first failed send takes the peer out; the sends that follow fail quietly.
        var error = Assert.Single(fixture.Logger.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.Contains("send failed for UDP peer 2", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.LoopGuard.TotalFailures);
    }

    // The notice of a changed carry state cannot be sent to its player: the player's state is
    // sound, so the session stays and only the peer is disconnected, which leaves the
    // character in the world as a body. The movement phase goes on with the other players.
    [Fact]
    public void ACarryNoticeThatCannotBeSentCostsThePeerNotThePlayer()
    {
        using var fixture = new RealtimeLoopFixture();
        var burdened = fixture.ConnectPlayer(1, "Burdened Player");
        var other = fixture.ConnectPlayer(2, "Other Player");
        var carry = burdened.Session.CarryState;
        Assert.Equal(ItemStateApplyResult.Applied, fixture.CharacterItemStateStore.ApplyCommitted(
            burdened.Session.CharacterId,
            burdened.Session.SimulationSessionId,
            CharacterStateTestData.ItemState(
                new PlayerCarryState(carry.ItemStateRevision + 1, carry.CarriedWeight + 1, carry.CarryCapacity)),
            out _));
        burdened.Peer.FailReliableSends = true;
        var phases = new SimulationTickPhases();
        new PlayerMovementTickPhase(fixture.CarrySync, fixture.PerformanceMetrics, fixture.FailureIsolation)
            .AddTickPhase(phases);
        var loop = new SimulationTickLoop(
            fixture.Clock,
            fixture.Entities,
            phases,
            fixture.PerformanceMetrics,
            fixture.LoopGuard,
            fixture.Logger);
        var nextTick = TimeSpan.Zero;

        loop.ProcessDueTicks(nextTick, TimeSpan.FromMilliseconds(1000d / 30d), ref nextTick);

        Assert.True(fixture.SessionStore.IsCurrent(burdened.Session, DateTime.UtcNow));
        Assert.True(fixture.Entities.TryGetPlayer(burdened.Entity.NetworkEntityId, out _));
        Assert.Equal(carry.ItemStateRevision + 1, burdened.Entity.Movement.CarryState.ItemStateRevision);
        Assert.NotNull(burdened.Context.PendingDisconnect);
        Assert.Null(other.Context.PendingDisconnect);
        var error = Assert.Single(fixture.Logger.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.Contains("send failed for UDP peer 1", error.Message, StringComparison.Ordinal);
        var interval = fixture.PerformanceMetrics.CaptureAndReset();
        Assert.Equal(0, interval.LoopStepFailures);
        Assert.Equal(1, interval.LoopIsolatedFailures);
    }

    // A connect callback that throws costs only its own connection, and a disconnect callback
    // that throws does not leave PollEvents: later connections still reach the lifecycle.
    [Fact]
    public async Task AFailingConnectOrDisconnectCallbackCostsOnlyItsPeer()
    {
        using var fixture = new RealtimeLoopFixture();
        var config = RealtimeServerHarness.CreateConfig(FindAvailableUdpPort(), new RealtimeServerHarnessOptions());
        var lifecycle = new FailingPeerLifecycle { FailConnect = true };
        var transport = new LiteNetLibRealtimeTransport(
            config,
            new RealtimeConnectionAdmission(config, null, new PeerAdmissionGuard(config.Admission), fixture.Metrics),
            fixture.LoopGuard,
            fixture.Logger);
        transport.Start(
            lifecycle,
            new RealtimeMessageDispatcher(fixture.Registry, fixture.Sender, fixture.Metrics, NullLogger.Instance, new RealtimeMessageRoutes()));
        using var refused = new RealtimeRecordingClient("Refused", null);
        using var accepted = new RealtimeRecordingClient("Accepted", null);
        try
        {
            refused.Connect(config.UdpPort);
            await PollUntilAsync(transport, [refused], () => refused.IsDisconnected, "the refused client to be disconnected");
            Assert.Equal(1, fixture.LoopGuard.TotalFailures);

            // Only the second peer's disconnect callback fails: the disconnect of the refused
            // peer may reach the lifecycle at any time from here on and must not count.
            lifecycle.FailConnect = false;
            accepted.Connect(config.UdpPort);
            await PollUntilAsync(transport, [accepted], () => accepted.IsConnected && lifecycle.Connected.Count == 1, "the second client to connect");
            var acceptedPeerId = lifecycle.Connected[0];
            lifecycle.FailDisconnectOf = acceptedPeerId;

            accepted.Stop();
            await PollUntilAsync(transport, [], () => lifecycle.Disconnected.Contains(acceptedPeerId), "the disconnect callback");

            Assert.Equal(2, fixture.LoopGuard.TotalFailures);
            var errors = fixture.Logger.Entries.Where(entry => entry.Level >= LogLevel.Error).Select(entry => entry.Message).ToArray();
            Assert.Equal(2, errors.Length);
            Assert.Contains("peer connect failed", errors[0], StringComparison.Ordinal);
            Assert.Contains($"peer disconnect failed for UDP peer {acceptedPeerId}", errors[1], StringComparison.Ordinal);
        }
        finally
        {
            transport.Stop();
        }
    }

    // A tick phase that keeps failing while nothing expects it fails the test when the harness is
    // disposed, with the logged failure in the message.
    [Fact]
    public async Task TheHarnessFailsATestWhoseLoopFailedUnexpectedly()
    {
        var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            CollisionStreamingStore = BrokenStreamingStore(chunkSize: 32f, chunkX: 0, chunkZ: 0),
            TickPhaseFailureLimit = 100_000
        });
        await harness.StartAsync();
        await harness.PollUntilAsync(() => harness.LoopFailures > 0, "a failed collision streaming phase");

        var failure = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() => harness.DisposeAsync().AsTask());

        Assert.Contains("unexpected failures", failure.Message, StringComparison.Ordinal);
        Assert.Contains("tick phase collision streaming failed", failure.Message, StringComparison.Ordinal);
        Assert.Contains("FileNotFoundException", failure.Message, StringComparison.Ordinal);
    }

    // The same failure passes when the test declares that it expects loop failures.
    [Fact]
    public async Task TheHarnessAcceptsLoopFailuresThatTheTestExpects()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            CollisionStreamingStore = BrokenStreamingStore(chunkSize: 32f, chunkX: 0, chunkZ: 0),
            TickPhaseFailureLimit = 100_000,
            ExpectLoopFailures = true
        });
        await harness.StartAsync();
        await harness.PollUntilAsync(() => harness.LoopFailures > 0, "a failed collision streaming phase");
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Error
                && entry.Message.Contains("tick phase collision streaming failed", StringComparison.Ordinal));
    }

    // A collision streaming store whose one chunk file is missing, so every tick that needs the
    // chunk fails the collision streaming phase, as a damaged World directory would.
    internal static WorldCollisionStreamingStore BrokenStreamingStore(float chunkSize, int chunkX, int chunkZ)
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.chunk");
        return new WorldCollisionStreamingStore(
            RealtimeServerHarness.WorldId,
            "broken-test-revision",
            chunkSize,
            [new WorldCollisionChunkSource(chunkX, chunkZ, "missing.chunk", missing, "00")],
            CollisionStreamingConfig.Default);
    }

    private static async Task PollUntilAsync(
        LiteNetLibRealtimeTransport transport,
        IReadOnlyList<RealtimeRecordingClient> clients,
        Func<bool> condition,
        string description)
    {
        using var timeout = RealtimeServerHarness.CreateTimeout();
        while (!condition())
        {
            transport.PollEvents();
            transport.FlushSends();
            foreach (var client in clients)
            {
                client.PollEvents();
            }

            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {description}.");
            }
        }
    }

    private static int FindAvailableUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    // Records the peers it hears of and throws when told to, after recording.
    private sealed class FailingPeerLifecycle : IRealtimePeerLifecycle
    {
        public bool FailConnect { get; set; }

        // The peer whose disconnect callback throws, or null for none.
        public int? FailDisconnectOf { get; set; }

        public List<int> Connected { get; } = [];

        public List<int> Disconnected { get; } = [];

        public void OnPeerConnected(int peerId, IPAddress address)
        {
            if (FailConnect)
            {
                throw new InvalidOperationException("The connect callback broke.");
            }

            Connected.Add(peerId);
        }

        public void OnPeerDisconnected(int peerId, DisconnectReason reason)
        {
            Disconnected.Add(peerId);
            if (FailDisconnectOf == peerId)
            {
                throw new InvalidOperationException("The disconnect callback broke.");
            }
        }
    }

    // The peer supervisor of a loop fixture, with the fixture's session operations.
    private sealed class SupervisorService
    {
        private SupervisorService(RealtimeLoopFixture fixture)
        {
            Supervisor = new RealtimePeerSupervisor(
                fixture.Config,
                fixture.Registry,
                fixture.Directory,
                fixture.Sender,
                fixture.SessionStore,
                fixture.Teardown,
                fixture.Bodies,
                fixture.SessionOperations,
                fixture.Metrics,
                NullLogger.Instance);
        }

        public RealtimePeerSupervisor Supervisor { get; }

        public static SupervisorService Create(RealtimeLoopFixture fixture)
        {
            return new SupervisorService(fixture);
        }
    }
}

// Passes collision queries on and counts them; throws instead while Fail is set.
internal sealed class ProbeCollisionWorld(ICollisionWorld inner) : ICollisionWorld
{
    public bool Fail { get; set; }

    public int Queries { get; private set; }

    public void QueryShapes(CollisionAabb bounds, uint layerMask, CollisionQueryBuffer buffer)
    {
        if (Fail)
        {
            throw new InvalidOperationException("The collision probe broke.");
        }

        Queries++;
        inner.QueryShapes(bounds, layerMask, buffer);
    }
}
