using LiteNetLib;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
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
        var loop = CreateLoop(fixture.Clock, fixture.Entities, phases, metrics, out var guard);
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
        Assert.Equal(0, guard.TotalFailures);
    }

    // A phase that throws is logged once and skipped for this tick only: the later phases of the
    // tick run, the tick counts, and the next tick runs every phase.
    [Fact]
    public void APhaseThatThrowsOnceIsSkippedAndTheTickGoesOn()
    {
        using var fixture = new RealtimeLoopFixture();
        using var metrics = new RealtimePerformanceMetrics();
        var logger = new CapturingLogger<RealtimeSimulationService>();
        var phases = new SimulationTickPhases();
        var calls = new List<string>();
        phases.Add(SimulationTickOrder.Movement, "early", (in SimulationTick tick) => calls.Add($"early {tick.ServerTick}"));
        phases.Add(SimulationTickOrder.Mobs, "failing", (in SimulationTick tick) =>
        {
            if (tick.ServerTick == 1)
            {
                throw new InvalidOperationException("The failing phase broke.");
            }

            calls.Add($"failing {tick.ServerTick}");
        });
        phases.Add(SimulationTickOrder.Snapshots, "late", (in SimulationTick tick) => calls.Add($"late {tick.ServerTick}"));
        var loop = CreateLoop(fixture.Clock, fixture.Entities, phases, metrics, out var guard, logger: logger);
        var nextTick = TimeSpan.Zero;

        RunTicks(loop, ref nextTick, 2);

        Assert.Equal(["early 1", "late 1", "early 2", "failing 2", "late 2"], calls);
        Assert.Equal(2u, fixture.Clock.ServerTick);
        Assert.Equal(TickInterval * 2, nextTick);
        var error = Assert.Single(logger.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Contains("tick phase failing failed at server tick 1", error.Message, StringComparison.Ordinal);
        Assert.Equal("The failing phase broke.", error.Exception!.Message);
        Assert.Equal(1, guard.TotalFailures);
        var interval = metrics.CaptureAndReset();
        Assert.Equal(1, interval.LoopStepFailures);
        Assert.Equal(0, interval.LoopIsolatedFailures);
    }

    // Only failures in a row count toward the limit: a run that succeeds starts the count again,
    // and the run that reaches the limit logs one critical line and ends the loop.
    [Fact]
    public void OnlyFailuresInARowEndTheLoop()
    {
        using var fixture = new RealtimeLoopFixture();
        using var metrics = new RealtimePerformanceMetrics();
        var logger = new CapturingLogger<RealtimeSimulationService>();
        var phases = new SimulationTickPhases();
        var failingTicks = new HashSet<uint> { 1, 3, 4, 6, 7, 8 };
        phases.Add(SimulationTickOrder.Mobs, "flaky", (in SimulationTick tick) =>
        {
            if (failingTicks.Contains(tick.ServerTick))
            {
                throw new InvalidOperationException($"Tick {tick.ServerTick} broke.");
            }
        });
        var loop = CreateLoop(fixture.Clock, fixture.Entities, phases, metrics, out var guard, failureLimit: 3, logger: logger);
        var nextTick = TimeSpan.Zero;

        // Fails, succeeds, fails twice, succeeds: never three in a row.
        RunTicks(loop, ref nextTick, 5);
        Assert.Equal(3, guard.TotalFailures);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Critical);

        // Fails at 6 and 7, and the third failure in a row at 8 ends the loop.
        RunTicks(loop, ref nextTick, 2);
        var stop = Assert.Throws<InvalidOperationException>(() => RunTicks(loop, ref nextTick, 1));

        Assert.Equal("Tick 8 broke.", stop.InnerException!.Message);
        Assert.Equal(6, guard.TotalFailures);
        Assert.Equal(6, logger.Entries.Count(entry => entry.Level == LogLevel.Error));
        var critical = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Critical);
        Assert.Contains("tick phase flaky failed 3 runs in a row at server tick 8", critical.Message, StringComparison.Ordinal);
    }

    // A prerequisite phase that throws ends its tick: the phases after it, which rely on it, do
    // not run, the phase before it did, and the next tick runs them all again.
    [Fact]
    public void APrerequisitePhaseThatThrowsSkipsTheRestOfItsTick()
    {
        using var fixture = new RealtimeLoopFixture();
        using var metrics = new RealtimePerformanceMetrics();
        var logger = new CapturingLogger<RealtimeSimulationService>();
        var phases = new SimulationTickPhases();
        var ran = new List<string>();
        phases.Add(SimulationTickOrder.CollisionStreaming - 1, "before", (in SimulationTick tick) => ran.Add($"before {tick.ServerTick}"));
        phases.AddPrerequisite(SimulationTickOrder.CollisionStreaming, "prerequisite", (in SimulationTick tick) =>
        {
            if (tick.ServerTick == 1)
            {
                throw new InvalidOperationException("The prerequisite broke.");
            }

            ran.Add($"prerequisite {tick.ServerTick}");
        });
        phases.Add(SimulationTickOrder.Movement, "movement", (in SimulationTick tick) => ran.Add($"movement {tick.ServerTick}"));
        phases.Add(SimulationTickOrder.CharacterCheckpoint, "checkpoint", (in SimulationTick tick) => ran.Add($"checkpoint {tick.ServerTick}"));
        var loop = CreateLoop(fixture.Clock, fixture.Entities, phases, metrics, out var guard, logger: logger);
        var nextTick = TimeSpan.Zero;

        RunTicks(loop, ref nextTick, 2);

        Assert.Equal(
            ["before 1", "before 2", "prerequisite 2", "movement 2", "checkpoint 2"],
            ran);
        Assert.Equal(1, guard.TotalFailures);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal(2u, fixture.Clock.ServerTick);
    }

    // A phase that works on every second tick and fails each time it does: the ticks between,
    // on which it was not due, are no successful runs, so its failures still count as in a row
    // and the third ends the loop.
    [Fact]
    public void ATickOnWhichAPhaseWasNotDueDoesNotResetItsFailedRuns()
    {
        using var fixture = new RealtimeLoopFixture();
        using var metrics = new RealtimePerformanceMetrics();
        var logger = new CapturingLogger<RealtimeSimulationService>();
        var phases = new SimulationTickPhases();
        phases.AddPeriodic(SimulationTickOrder.Snapshots, "every second tick", (in SimulationTick tick) =>
        {
            if (tick.ServerTick % 2 != 0)
            {
                return false;
            }

            throw new InvalidOperationException($"Tick {tick.ServerTick} broke.");
        });
        var loop = CreateLoop(fixture.Clock, fixture.Entities, phases, metrics, out var guard, failureLimit: 3, logger: logger);
        var nextTick = TimeSpan.Zero;

        // Due and failing at ticks 2 and 4, not due at 1, 3, and 5.
        RunTicks(loop, ref nextTick, 5);
        Assert.Equal(2, guard.TotalFailures);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Critical);

        var stop = Assert.Throws<InvalidOperationException>(() => RunTicks(loop, ref nextTick, 1));

        Assert.Equal("Tick 6 broke.", stop.InnerException!.Message);
        Assert.Equal(3, guard.TotalFailures);
        var critical = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Critical);
        Assert.Contains("failed 3 runs in a row at server tick 6", critical.Message, StringComparison.Ordinal);
    }

    // A periodic phase that did its work resets the count like any other run that succeeds.
    [Fact]
    public void ADueRunThatSucceedsResetsTheFailedRunsOfAPeriodicPhase()
    {
        using var fixture = new RealtimeLoopFixture();
        using var metrics = new RealtimePerformanceMetrics();
        var phases = new SimulationTickPhases();
        var failingTicks = new HashSet<uint> { 2, 4, 8, 10 };
        phases.AddPeriodic(SimulationTickOrder.Snapshots, "every second tick", (in SimulationTick tick) =>
        {
            if (tick.ServerTick % 2 != 0)
            {
                return false;
            }

            return failingTicks.Contains(tick.ServerTick)
                ? throw new InvalidOperationException($"Tick {tick.ServerTick} broke.")
                : true;
        });
        var loop = CreateLoop(fixture.Clock, fixture.Entities, phases, metrics, out var guard, failureLimit: 3);
        var nextTick = TimeSpan.Zero;

        // Fails at 2 and 4, works at 6, fails at 8 and 10: never three in a row.
        RunTicks(loop, ref nextTick, 11);

        Assert.Equal(4, guard.TotalFailures);
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
        var loop = CreateLoop(fixture.Clock, fixture.Entities, phases, metrics, out var guard);
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
        Assert.Equal(0, guard.TotalFailures);
    }

    // The guard around every phase costs nothing on a tick that does not throw, also after a
    // phase failed once: the failure allocates, the ticks after it do not.
    [Fact]
    public void AGuardedTickAllocatesNothingAfterAPhaseFailed()
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
            if (tick.ServerTick == 1)
            {
                throw new InvalidOperationException("The first tick broke.");
            }

            foreach (var player in tick.Players)
            {
                visited += (long)player.NetworkEntityId;
            }
        });
        var loop = CreateLoop(fixture.Clock, fixture.Entities, phases, metrics, out var guard);
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
        Assert.True(visited > 0);
        Assert.Equal(1, guard.TotalFailures);
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
        var sessions = new List<ActiveSimulationSession>();
        for (var peerId = 1; peerId <= 50; peerId++)
        {
            var session = RealtimeTestSessions.Create($"Checkpoint Player {peerId}");
            Assert.Equal(ActiveSimulationSessionRegistration.Joined, harness.SessionStore.Register(session));
            sessions.Add(session);
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
        phases.AddPeriodic(checkpoint.Order, checkpoint.Name, checkpoint.Run);
        using var metrics = new RealtimePerformanceMetrics();
        var loop = CreateLoop(new RealtimeSimulationClock(), harness.EntityRegistry, phases, metrics, out var guard);
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
        Assert.Equal(0, guard.TotalFailures);

        // A session that the worker no longer holds, as after an exception in the player's own
        // step, is left out of the checkpoint although its entity is still among the players:
        // its state may be broken.
        var broken = sessions[7];
        Assert.True(harness.SessionStore.Invalidate(
            broken.CharacterId,
            broken.SimulationSessionId,
            broken.SimulationSessionToken,
            RealtimeFailureIsolation.Code,
            RealtimeFailureIsolation.Message));

        // The interval is 6,000 ticks, counted from the first tick.
        RunTicks(loop, ref nextTick, 6_000 - 100 - (allocated.Count * 1_000) + 1);
        await harness.PollUntilAsync(() => harness.GameService.Checkpoints.Count == 1, "the due checkpoint");
        var states = Assert.Single(harness.GameService.Checkpoints).States;
        Assert.Equal(49, states.Count);
        Assert.DoesNotContain(states, state => state.SimulationSessionId == broken.SimulationSessionId);
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
            phases.AddPeriodic(phase.Order, phase.Name, phase.Run);
        }

        Assert.Equal(4, phases.InTickOrder().Length);
        using var metrics = new RealtimePerformanceMetrics();
        var loop = CreateLoop(new RealtimeSimulationClock(), harness.EntityRegistry, phases, metrics, out var guard);
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
        Assert.Equal(0, guard.TotalFailures);
    }

    // The loop with its guard, as the composition builds it.
    private static SimulationTickLoop CreateLoop(
        RealtimeSimulationClock clock,
        SimulationEntityRegistry entities,
        SimulationTickPhases phases,
        RealtimePerformanceMetrics metrics,
        out RealtimeLoopGuard guard,
        int failureLimit = SimulationWorkerConfig.DefaultTickPhaseFailureLimit,
        ILogger? logger = null)
    {
        guard = new RealtimeLoopGuard(failureLimit, clock, metrics, logger ?? NullLogger.Instance);
        return new SimulationTickLoop(clock, entities, phases, metrics, guard, logger ?? NullLogger.Instance);
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
