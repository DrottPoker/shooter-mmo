using LiteNetLib;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

public sealed class SimulationTickLoopTests
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(1000d / 30d);

    // The loop sorts the phases by their order, not by when they were registered, and hands
    // each the same tick: the new server tick and the players in entity id order.
    [Fact]
    public void ATickRunsThePhasesInTheirOrderWithThePlayersOfTheTick()
    {
        using var fixture = new RealtimeLoopFixture();
        var players = new[] { fixture.ConnectPlayer(1, "First"), fixture.ConnectPlayer(2, "Second") };
        using var metrics = new RealtimePerformanceMetrics();
        var phases = new SimulationTickPhases();
        var calls = new List<string>();
        phases.Add(SimulationTickOrder.Snapshots, "late", (in SimulationTick tick) =>
            calls.Add($"late {tick.ServerTick}"));
        phases.Add(SimulationTickOrder.Movement, "early", (in SimulationTick tick) =>
        {
            foreach (var player in tick.Players)
            {
                calls.Add($"early {tick.ServerTick} {player.NetworkEntityId}");
            }
        });
        var loop = new SimulationTickLoop(fixture.Clock, fixture.Entities, phases, metrics, NullLogger.Instance);
        var nextTick = TimeSpan.Zero;

        loop.ProcessDueTicks(nextTick, TickInterval, ref nextTick);

        Assert.Equal(
            [
                $"early 1 {players[0].Entity.NetworkEntityId}",
                $"early 1 {players[1].Entity.NetworkEntityId}",
                "late 1"
            ],
            calls);
        Assert.Equal(["early", "late"], loop.Phases.Select(phase => phase.Name));
    }

    // The loop runs at the tick rate for every player, so beyond its phases a tick must not
    // allocate: filling the players, building the tick, and calling the phases.
    [Fact]
    public void ATickAllocatesNothingBeyondItsPhases()
    {
        using var fixture = new RealtimeLoopFixture();
        for (var peerId = 1; peerId <= 50; peerId++)
        {
            fixture.ConnectPlayer(peerId, $"Player {peerId}");
        }

        using var metrics = new RealtimePerformanceMetrics();
        var phases = new SimulationTickPhases();
        var visited = 0L;
        phases.Add(SimulationTickOrder.Movement, "players", (in SimulationTick tick) =>
        {
            foreach (var player in tick.Players)
            {
                visited += (long)player.NetworkEntityId;
            }

            for (var index = 0; index < tick.Players.Count; index++)
            {
                visited += (long)tick.Players[index].NetworkEntityId;
            }
        });
        phases.Add(
            SimulationTickOrder.PlayerGrid,
            "player grid",
            static (in SimulationTick tick) => tick.PlayerGrid.Rebuild(tick.Players.AsReadOnlyList()));
        var loop = new SimulationTickLoop(fixture.Clock, fixture.Entities, phases, metrics, NullLogger.Instance);
        var nextTick = TimeSpan.Zero;
        RunTicks(loop, ref nextTick, 100);

        // A meter listener that another test left subscribed creates its metric points on
        // this thread once, so one quiet pass proves a steady tick.
        var allocated = new List<long>();
        for (var pass = 0; pass < 3 && !allocated.Contains(0); pass++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            RunTicks(loop, ref nextTick, 1_000);
            allocated.Add(GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Contains(0L, allocated);
        Assert.True(visited > 0);
    }

    // The composed character checkpoint runs every tick but does nothing until its interval is
    // due, so a tick with tracked players allocates nothing. The due tick then sends them all.
    [Fact]
    public async Task TheCharacterCheckpointAllocatesNothingOnATickWhenItIsNotDue()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            CombatRules = RealtimeCombatTests.TestRules(),
            CharacterCheckpointInterval = TimeSpan.FromSeconds(200)
        });
        var config = harness.Config;
        var initialState = PlayerMovementSimulation.CreateInitialState(
            config.MovementSimulation,
            harness.CollisionWorld,
            config.MovementSpawn.X,
            config.MovementSpawn.Y,
            config.MovementSpawn.Z,
            config.MovementSpawn.YawDegrees);
        // The harness is not started, so this thread is the loop: the joined hooks make every
        // player a combatant and track it for persistence.
        for (var peerId = 1; peerId <= 50; peerId++)
        {
            var session = RealtimeTestSessions.Create($"Checkpoint Player {peerId}");
            var entity = harness.EntityRegistry.RegisterPlayer(
                    session,
                    () => new AuthoritativePlayerMovement(
                        initialState,
                        config.MovementSimulation,
                        session.CarryState,
                        harness.CollisionWorld))
                .Entity;
            harness.Service.Server.Registries.Hooks.RaisePlayerJoined(
                new RealtimePeerContext(peerId, DateTime.UtcNow, config.UdpQuotas) { Session = session },
                entity);
        }

        var checkpoint = harness.Service.Server.TickPhases.Single(phase => phase.Name == "character checkpoint");
        var phases = new SimulationTickPhases();
        phases.Add(checkpoint.Order, checkpoint.Name, checkpoint.Run);
        using var metrics = new RealtimePerformanceMetrics();
        var loop = new SimulationTickLoop(
            new RealtimeSimulationClock(),
            harness.EntityRegistry,
            phases,
            metrics,
            NullLogger.Instance);
        var nextTick = TimeSpan.Zero;
        RunTicks(loop, ref nextTick, 100);

        var allocated = new List<long>();
        for (var pass = 0; pass < 3 && !allocated.Contains(0); pass++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            RunTicks(loop, ref nextTick, 1_000);
            allocated.Add(GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Contains(0L, allocated);
        Assert.Empty(harness.GameService.Checkpoints);

        // The interval is 6,000 ticks, counted from the first tick.
        RunTicks(loop, ref nextTick, 6_000 - 100 - (allocated.Count * 1_000) + 1);
        await harness.PollUntilAsync(() => harness.GameService.Checkpoints.Count == 1, "the due checkpoint");
        Assert.Equal(50, Assert.Single(harness.GameService.Checkpoints).States.Count);
    }

    // 500 lingering bodies, made through the composed leave route, add nothing to a tick that ends
    // none of them: not to the body phase, nor to movement, which steps them, combat, which still
    // holds them, or the character checkpoint, which saves them at its interval.
    [Fact]
    public async Task FiveHundredLingeringBodiesAllocateNothingOnATickThatEndsNone()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            CombatRules = RealtimeCombatTests.TestRules(),
            CharacterCheckpointInterval = TimeSpan.FromSeconds(3_600),
            LingeringBodySeconds = 3_600
        });
        var config = harness.Config;
        var server = harness.Service.Server;
        var leave = server.Registries.Routes.Authenticated.Single(route => route.Type == RealtimeMessageType.LeaveRequest);
        var initialState = PlayerMovementSimulation.CreateInitialState(
            config.MovementSimulation,
            harness.CollisionWorld,
            config.MovementSpawn.X,
            config.MovementSpawn.Y,
            config.MovementSpawn.Z,
            config.MovementSpawn.YawDegrees);

        // The harness is not started, so this thread is the loop: each player joins through the
        // joined hooks and leaves through the leave route, which leaves its body.
        for (var peerId = 1; peerId <= 500; peerId++)
        {
            var session = RealtimeTestSessions.Create($"Lingering Player {peerId}");
            Assert.Equal(ActiveSimulationSessionRegistration.Joined, harness.SessionStore.Register(session));
            harness.CharacterItemStateStore.Register(
                session.CharacterId,
                session.SimulationSessionId,
                CharacterStateTestData.ItemState(session.CarryState),
                out _);
            var entity = harness.EntityRegistry.RegisterPlayer(
                    session,
                    () => new AuthoritativePlayerMovement(
                        initialState,
                        config.MovementSimulation,
                        session.CarryState,
                        harness.CollisionWorld))
                .Entity;
            harness.Bindings.Bind(peerId, entity.NetworkEntityId);
            var context = new RealtimePeerContext(peerId, DateTime.UtcNow, config.UdpQuotas) { Session = session };
            server.Registries.Hooks.RaisePlayerJoined(context, entity);
            leave.Handler(new RealtimeInboundMessage(
                new RecordingPeerConnection(peerId),
                context,
                RealtimeMessageType.LeaveRequest,
                RealtimeProtocol.EncodeLeaveRequest(session.SimulationSessionId.ToString("D")),
                RealtimeProtocol.ControlChannel,
                DeliveryMethod.ReliableOrdered));
            Assert.Null(context.Session);
            Assert.False(harness.Bindings.TryGetConnectionId(entity.NetworkEntityId, out _));
        }

        var phases = new SimulationTickPhases();
        foreach (var phase in server.TickPhases.Where(phase => phase.Name is "movement" or "combat" or "lingering bodies" or "character checkpoint"))
        {
            phases.Add(phase.Order, phase.Name, phase.Run);
        }

        Assert.Equal(4, phases.InTickOrder().Length);
        using var metrics = new RealtimePerformanceMetrics();
        var loop = new SimulationTickLoop(
            new RealtimeSimulationClock(),
            harness.EntityRegistry,
            phases,
            metrics,
            NullLogger.Instance);
        var nextTick = TimeSpan.Zero;
        RunTicks(loop, ref nextTick, 100);

        var allocated = new List<long>();
        for (var pass = 0; pass < 3 && !allocated.Contains(0); pass++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            RunTicks(loop, ref nextTick, 1_000);
            allocated.Add(GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Contains(0L, allocated);
        Assert.Equal(500, harness.EntityRegistry.PlayerCount);
        Assert.Empty(harness.GameService.Releases);
    }

    // One due tick per call, so the loop never resynchronizes.
    private static void RunTicks(SimulationTickLoop loop, ref TimeSpan nextTick, int count)
    {
        for (var tick = 0; tick < count; tick++)
        {
            loop.ProcessDueTicks(nextTick, TickInterval, ref nextTick);
        }
    }
}
