using LiteNetLib;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.WorldData.Actors;

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

    // Every tick and phase is timed with the garbage collection pause inside it, and the slowest
    // tick of the interval keeps its phases and the spawn and despawn packets it sent.
    [Fact]
    public void TheSlowestTickKeepsItsPhasesAndTheSpawnsItSent()
    {
        using var fixture = new RealtimeLoopFixture();
        using var metrics = new RealtimePerformanceMetrics();
        using var network = new RealtimeNetworkMetrics();
        var phases = new SimulationTickPhases();
        phases.Add(SimulationTickOrder.Movement, "movement", (in SimulationTick _) => { });
        phases.Add(SimulationTickOrder.Snapshots, "snapshots", (in SimulationTick tick) =>
        {
            if (tick.ServerTick != 2)
            {
                return;
            }

            // The second tick sends a wave of spawns and takes the longest.
            for (var index = 0; index < 5; index++)
            {
                network.RecordSpawnPacket();
            }

            network.RecordDespawnPacket();
            Thread.Sleep(20);
        });
        var guard = new RealtimeLoopGuard(SimulationWorkerConfig.DefaultTickPhaseFailureLimit, fixture.Clock, metrics, NullLogger.Instance);
        var loop = new SimulationTickLoop(fixture.Clock, fixture.Entities, phases, metrics, guard, NullLogger.Instance, network);
        var nextTick = TimeSpan.Zero;

        RunTicks(loop, ref nextTick, 3);

        var snapshot = metrics.CaptureAndReset();
        var slowest = snapshot.SlowestTick!;
        Assert.True(slowest.Milliseconds >= 20d);
        Assert.Equal(5, slowest.SpawnPackets);
        Assert.Equal(1, slowest.DespawnPackets);
        Assert.Equal(["movement", "snapshots"], slowest.Phases.Select(phase => phase.Name));
        Assert.True(slowest.Phases[1].Milliseconds >= 20d);
        Assert.NotNull(snapshot.SimulationTick!.GcPauses);
        Assert.All(snapshot.TickPhases, phase => Assert.NotNull(phase.Summary.GcPauses));
        Assert.Null(slowest.CpuMilliseconds);
        Assert.Null(snapshot.SimulationTickNotRunning);
    }

    // The health file of the worker reads when the loop last completed a tick: every tick stamps
    // it at its end, also a tick in which a phase failed, and a loop that never ticked has none.
    [Fact]
    public void EveryCompletedTickStampsTheLoopLiveness()
    {
        using var fixture = new RealtimeLoopFixture();
        using var metrics = new RealtimePerformanceMetrics();
        var liveness = new RealtimeLoopLiveness();
        var phases = new SimulationTickPhases();
        phases.Add(SimulationTickOrder.Movement, "movement", (in SimulationTick tick) =>
        {
            if (tick.ServerTick == 2)
            {
                throw new InvalidOperationException("A phase fails in the second tick.");
            }
        });
        var guard = new RealtimeLoopGuard(SimulationWorkerConfig.DefaultTickPhaseFailureLimit, fixture.Clock, metrics, NullLogger.Instance);
        var loop = new SimulationTickLoop(fixture.Clock, fixture.Entities, phases, metrics, guard, NullLogger.Instance, liveness: liveness);
        var nextTick = TimeSpan.Zero;
        Assert.False(liveness.HasTicked);

        RunTicks(loop, ref nextTick, 1);
        var afterFirst = liveness.SilenceFor(TimeProvider.System);
        Thread.Sleep(300);
        RunTicks(loop, ref nextTick, 1);
        var afterFailing = liveness.SilenceFor(TimeProvider.System);

        Assert.NotNull(afterFirst);
        Assert.True(liveness.HasTicked);
        // The failing tick stamped the liveness again: the silence since it is shorter than the pause.
        Assert.True(afterFailing < TimeSpan.FromMilliseconds(250), $"The silence after the failing tick was {afterFailing}.");
    }
    // With a CPU clock every tick also measures how long the loop thread ran in it: the slowest
    // tick keeps its CPU time, and the interval sums the CPU time and the time the thread did not
    // run, the wall time minus the CPU time, per tick.
    [Fact]
    public void ATickMeasuresHowLongTheLoopThreadRanInIt()
    {
        using var fixture = new RealtimeLoopFixture();
        using var metrics = new RealtimePerformanceMetrics();
        var cpu = new ScriptedCpuClock();
        var phases = new SimulationTickPhases();
        phases.Add(SimulationTickOrder.Movement, "movement", (in SimulationTick tick) =>
        {
            if (tick.ServerTick == 2)
            {
                // The second tick runs 5 ms and waits at least 20 ms in which it does not run.
                cpu.RunMilliseconds(5);
                Thread.Sleep(20);
            }
        });
        var guard = new RealtimeLoopGuard(SimulationWorkerConfig.DefaultTickPhaseFailureLimit, fixture.Clock, metrics, NullLogger.Instance);
        var loop = new SimulationTickLoop(fixture.Clock, fixture.Entities, phases, metrics, guard, NullLogger.Instance, cpuClock: cpu);
        var nextTick = TimeSpan.Zero;

        RunTicks(loop, ref nextTick, 3);

        var snapshot = metrics.CaptureAndReset();
        var slowest = snapshot.SlowestTick!;
        Assert.Equal(5d, slowest.CpuMilliseconds);
        Assert.True(slowest.Milliseconds >= 20d);
        Assert.Equal(3, snapshot.SimulationTickCpu!.Samples);
        Assert.Equal(5d, snapshot.SimulationTickCpu.AverageMilliseconds * 3, 6);
        var notRunning = snapshot.SimulationTickNotRunning!;
        Assert.Equal(3, notRunning.Samples);
        Assert.True(notRunning.MaximumMilliseconds >= 15d);
        // Both sums round each sample up to a microsecond.
        Assert.InRange(
            (notRunning.AverageMilliseconds * 3) - ((snapshot.SimulationTick!.AverageMilliseconds * 3) - 5d),
            -0.01d,
            0.01d);
    }

    // The clock of this platform counts the CPU time of the thread that reads it: a spin is CPU
    // time, a sleep is not, and no span is longer than its wall time.
    [Fact]
    public void TheThreadCpuClockCountsRunningButNotSleeping()
    {
        var clock = ThreadCpuClock.ForCurrentPlatform();
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(clock);
        }

        if (clock is null)
        {
            return;
        }

        var start = clock.Read();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < 5d)
        {
        }

        var spun = clock.Read();
        var spinWall = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Thread.Sleep(30);
        var slept = clock.Read();
        var sleepWall = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds - spinWall;

        var spinCpu = clock.Milliseconds(start, spun, spinWall);
        Assert.InRange(spinCpu, 0.5d, spinWall);
        Assert.InRange(clock.Milliseconds(spun, slept, sleepWall), 0d, 5d);
        Assert.False(string.IsNullOrWhiteSpace(clock.Description));
    }

    // A CPU clock that counts what the phases say they ran, one unit per millisecond.
    private sealed class ScriptedCpuClock : ThreadCpuClock
    {
        private long microseconds;

        public override string Description => "scripted";

        public void RunMilliseconds(double milliseconds)
        {
            microseconds += (long)(milliseconds * 1000d);
        }

        public override long Read()
        {
            return microseconds;
        }

        public override double Milliseconds(long start, long end, double wallMilliseconds)
        {
            return Math.Min(wallMilliseconds, (end - start) / 1000d);
        }
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

    // Every phase is timed under its name, in tick order, but only on the ticks on which it did
    // its work: a periodic phase that was not due and a run that threw are no samples. The loop
    // busy time adds the ticks, which hold the phases.
    [Fact]
    public void EveryPhaseIsTimedOnlyOnTheTicksOnWhichItDidItsWork()
    {
        using var fixture = new RealtimeLoopFixture();
        using var metrics = new RealtimePerformanceMetrics();
        var phases = new SimulationTickPhases();
        phases.AddPeriodic(SimulationTickOrder.Snapshots, "every second tick", static (in SimulationTick tick) => tick.ServerTick % 2 == 0);
        phases.Add(SimulationTickOrder.Movement, "every tick", static (in SimulationTick _) => { });
        phases.Add(SimulationTickOrder.Mobs, "failing at 3", static (in SimulationTick tick) =>
        {
            if (tick.ServerTick == 3)
            {
                throw new InvalidOperationException("Tick 3 broke.");
            }
        });
        var loop = CreateLoop(fixture.Clock, fixture.Entities, phases, metrics, out _);
        var nextTick = TimeSpan.Zero;

        RunTicks(loop, ref nextTick, 6);

        var interval = metrics.CaptureAndReset();
        Assert.Equal(
            ["every tick", "failing at 3", "every second tick"],
            interval.TickPhases.Select(phase => phase.Name));
        Assert.Equal(6, interval.TickPhases[0].Summary.Samples);
        Assert.Equal(5, interval.TickPhases[1].Summary.Samples);
        Assert.Equal(3, interval.TickPhases[2].Summary.Samples);
        Assert.Equal(
            interval.SimulationTick!.AverageMilliseconds * interval.SimulationTick.Samples,
            interval.LoopBusyMilliseconds,
            9);
        Assert.Empty(metrics.CaptureAndReset().TickPhases);
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

        // The interval is 6,000 ticks, counted from the first tick, so the checkpoint is due at
        // tick 6,001 and waits for the first free tick: 6,001 is corpse presence's and 6,002 a
        // snapshot tick, so it runs at 6,003.
        RunTicks(loop, ref nextTick, 6_000 - 100 - (allocated.Count * 1_000) + 3);
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
                RealtimeProtocol.OwnerChannel,
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

    // The composed collision streaming plans its chunks only when an anchor entered another chunk,
    // so a tick in which nobody crossed a chunk border plans nothing and, with the movement guard of
    // every player, allocates nothing. A player that crosses a border plans once.
    [Fact]
    public async Task CollisionStreamingAndMovementAllocateAndPlanNothingWhileNoAnchorChangesItsChunk()
    {
        var spawn = new SimulationVector3(0f, 0f, -1f);
        using var streamed = new StreamedTestCollision(spawn);
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            StreamedCollision = streamed
        });
        var config = harness.Config;
        var initialState = PlayerMovementSimulation.CreateInitialState(
            config.MovementSimulation,
            harness.CollisionWorld,
            spawn.X,
            spawn.Y,
            spawn.Z,
            0f);
        var players = new List<PlayerSimulationEntity>();
        for (var index = 0; index < 50; index++)
        {
            var session = RealtimeTestSessions.Create($"Streaming Player {index}");
            Assert.Equal(ActiveSimulationSessionRegistration.Joined, harness.SessionStore.Register(session));
            players.Add(harness.EntityRegistry.RegisterPlayer(
                    session,
                    () => new AuthoritativePlayerMovement(
                        initialState,
                        config.MovementSimulation,
                        session.CarryState,
                        harness.CollisionWorld))
                .Entity);
        }

        var phases = new SimulationTickPhases();
        foreach (var phase in harness.Service.Server.TickPhases.Where(phase => phase.Name is "collision streaming" or "movement"))
        {
            phases.AddPeriodic(phase.Order, phase.Name, phase.Run);
        }

        Assert.Equal(2, phases.InTickOrder().Length);
        using var metrics = new RealtimePerformanceMetrics();
        var loop = CreateLoop(new RealtimeSimulationClock(), harness.EntityRegistry, phases, metrics, out var guard);
        var nextTick = TimeSpan.Zero;
        RunTicks(loop, ref nextTick, 100);
        var plans = streamed.Store.PlanCount;

        var allocated = new List<long>();
        for (var pass = 0; pass < 3 && !allocated.Contains(0); pass++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            RunTicks(loop, ref nextTick, 1_000);
            allocated.Add(GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Contains(0L, allocated);
        Assert.Equal(plans, streamed.Store.PlanCount);

        // A step within the chunk plans nothing; entering the next chunk east plans once.
        players[0].Movement.Teleport(PlayerMovementSimulation.CreateInitialState(
            config.MovementSimulation,
            harness.CollisionWorld,
            3f,
            0f,
            -1f,
            0f));
        RunTicks(loop, ref nextTick, 10);
        Assert.Equal(plans, streamed.Store.PlanCount);
        players[0].Movement.Teleport(PlayerMovementSimulation.CreateInitialState(
            config.MovementSimulation,
            harness.CollisionWorld,
            5f,
            0f,
            -1f,
            0f));
        RunTicks(loop, ref nextTick, 10);
        Assert.Equal(plans + 1, streamed.Store.PlanCount);
        Assert.Equal(0, guard.TotalFailures);
    }

    // The world actor phase walks the store's ordered actors, reuses the scheduler's list, and
    // counts the population once a second, so a tick in which no actor changes its tier allocates
    // nothing, also on the ticks that count the population.
    [Fact]
    public void TheWorldActorPhaseAllocatesNothingOnTicksThatChangeNoTier()
    {
        using var fixture = new RealtimeLoopFixture(enableWorldActors: true);
        var players = new List<PlayerSimulationEntity>();
        for (var peerId = 1; peerId <= 20; peerId++)
        {
            players.Add(fixture.ConnectPlayer(peerId, $"Actor Watcher {peerId}").Entity);
        }

        var driver = new WorldActorRuntimeDriver(
            fixture.Config,
            fixture.WorldActors,
            new WorldActorActivityScheduler(fixture.WorldActors!),
            fixture.WorldActorMetrics,
            null,
            fixture.Presence,
            fixture.Metrics,
            fixture.Timers,
            NullLogger.Instance);
        var grid = new PlayerSpatialGrid();
        grid.Rebuild(players);
        var tick = 1u;
        for (; tick <= 300; tick++)
        {
            driver.EvaluateTick(grid, tick);
        }

        Assert.Contains(fixture.WorldActors!.ListActors(), actor => actor.ActivityTier == WorldActorActivityTierIds.Active);
        var allocated = new List<long>();
        for (var pass = 0; pass < 3 && !allocated.Contains(0); pass++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 1_000; index++, tick++)
            {
                driver.EvaluateTick(grid, tick);
            }

            allocated.Add(GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Contains(0L, allocated);
        Assert.True(fixture.WorldActorMetrics!.Capture().ActivePopulation > 0);
    }

    // A corpse presence broadcast that changes nothing for any peer sends nothing and allocates
    // nothing; one in which a corpse changed sends every peer that sees it its presence again, in
    // corpse id order, each viewer one body per loot group.
    [Fact]
    public void ACorpsePresenceBroadcastThatChangesNothingSendsAndAllocatesNothing()
    {
        using var fixture = new RealtimeLoopFixture();
        var first = fixture.ConnectPlayer(1, "First Viewer");
        var second = fixture.ConnectPlayer(2, "Second Viewer");
        var durable = new DurableCorpseStore(TimeProvider.System);
        var live = new LiveMobCorpseStore(TimeProvider.System);
        var views = new CorpseViewDistributor(fixture.CorpseViewers, fixture.Leases, fixture.Registry, fixture.Sender, durable, live);
        var replicator = new CorpsePresenceReplicator(fixture.Config, durable, live, null, views, fixture.Registry, fixture.Sender);
        var near = Guid.NewGuid();
        var far = Guid.NewGuid();
        SeedDurableCorpse(durable, near, 2d, 1);
        SeedDurableCorpse(durable, far, 200d, 1);
        var group = Guid.NewGuid();
        var firstOwned = live.CreateOrGet(LiveCorpse(group, first.Session.CharacterId));
        var secondOwned = live.CreateOrGet(LiveCorpse(group, second.Session.CharacterId));

        replicator.Broadcast();

        Assert.Equal(
            new[] { near, firstOwned.CorpseId }.Order(),
            PresentedCorpses(first.Peer).Single());
        Assert.Equal(
            new[] { near, secondOwned.CorpseId }.Order(),
            PresentedCorpses(second.Peer).Single());
        for (var broadcast = 0; broadcast < 100; broadcast++)
        {
            replicator.Broadcast();
        }

        var allocated = new List<long>();
        for (var pass = 0; pass < 3 && !allocated.Contains(0); pass++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var broadcast = 0; broadcast < 1_000; broadcast++)
            {
                replicator.Broadcast();
            }

            allocated.Add(GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Contains(0L, allocated);
        Assert.Single(PresentedCorpses(first.Peer));
        Assert.Single(PresentedCorpses(second.Peer));

        SeedDurableCorpse(durable, near, 2d, 2);
        replicator.Broadcast();

        Assert.Equal(2, PresentedCorpses(first.Peer).Count);
        Assert.Equal(2, PresentedCorpses(second.Peer).Count);
        Assert.Equal(PresentedCorpses(first.Peer)[0], PresentedCorpses(first.Peer)[1]);
    }

    // A broadcast tests each peer only against the corpses of the cells its discovery radius
    // reaches, and still finds exactly the corpses a test of every active corpse finds: on the
    // radius, just beyond it, on cell borders, above and below the peer, and at negative
    // coordinates.
    [Fact]
    public void ACorpsePresenceBroadcastFindsTheCorpsesThatATestOfEveryCorpseFinds()
    {
        using var fixture = new RealtimeLoopFixture();
        var radius = fixture.Config.ItemInteraction.CorpseDiscoveryRadius;
        var random = new Random(1116);
        var players = new List<ConnectedPlayer>();
        for (var peerId = 1; peerId <= 40; peerId++)
        {
            var player = fixture.ConnectPlayer(peerId, $"Corpse Finder {peerId}");
            // Every fourth peer stands on a cell corner.
            var x = peerId % 4 == 0 ? radius * random.Next(-3, 4) : (float)((random.NextDouble() * 240d) - 120d);
            var z = peerId % 4 == 0 ? radius * random.Next(-3, 4) : (float)((random.NextDouble() * 240d) - 120d);
            player.Entity.Movement.Teleport(new PlayerMovementState(x, 0f, z, 0f, 0f, 0f, 0f, true));
            players.Add(player);
        }

        var durable = new DurableCorpseStore(TimeProvider.System);
        var live = new LiveMobCorpseStore(TimeProvider.System);
        var views = new CorpseViewDistributor(fixture.CorpseViewers, fixture.Leases, fixture.Registry, fixture.Sender, durable, live);
        var replicator = new CorpsePresenceReplicator(fixture.Config, durable, live, null, views, fixture.Registry, fixture.Sender);
        var corpses = new List<LiveMobCorpseState>();
        for (var index = 0; index < 400; index++)
        {
            corpses.Add(live.CreateOrGet(LiveCorpse(Guid.Empty, Guid.Empty) with
            {
                CorpseId = Guid.NewGuid(),
                PositionX = (random.NextDouble() * 300d) - 150d,
                PositionY = (random.NextDouble() * 20d) - 10d,
                PositionZ = (random.NextDouble() * 300d) - 150d
            }));
        }

        // Around a few peers, corpses on the radius, just inside, and just beyond it.
        foreach (var player in players.Take(10))
        {
            var state = player.Entity.Movement.State;
            foreach (var (deltaX, deltaZ) in new[] { (radius, 0f), (0f, -radius), (radius * 0.999f, 0f), (-radius * 1.001f, 0f), (0f, radius * 1.0001f) })
            {
                corpses.Add(live.CreateOrGet(LiveCorpse(Guid.Empty, Guid.Empty) with
                {
                    CorpseId = Guid.NewGuid(),
                    PositionX = state.PositionX + deltaX,
                    PositionY = state.PositionY,
                    PositionZ = state.PositionZ + deltaZ
                }));
            }
        }

        replicator.Broadcast();

        var radiusSquared = radius * radius;
        foreach (var player in players)
        {
            var state = player.Entity.Movement.State;
            var expected = corpses
                .Where(corpse =>
                {
                    var deltaX = corpse.PositionX - state.PositionX;
                    var deltaY = corpse.PositionY - state.PositionY;
                    var deltaZ = corpse.PositionZ - state.PositionZ;
                    return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ) <= radiusSquared;
                })
                .Select(corpse => corpse.CorpseId)
                .Order()
                .ToArray();
            Assert.Equal(expected, PresentedCorpseIds(player.Peer));
        }

        Assert.Contains(players, player => PresentedCorpseIds(player.Peer).Length > 6);
    }

    // The corpse ids of the peer's first corpse presence, over all its chunks.
    private static Guid[] PresentedCorpseIds(RecordingPeerConnection peer)
    {
        var ids = new List<Guid>();
        foreach (var send in peer.Sent)
        {
            if (RealtimeProtocol.TryReadMessageType(send.Data, out var type)
                && type == RealtimeMessageType.CorpsePresenceSnapshotChunk
                && RealtimeProtocol.TryDecodeCorpsePresenceSnapshotChunk(send.Data, out var chunk, out _))
            {
                ids.AddRange(chunk.Corpses.Select(corpse => corpse.CorpseId));
                if (chunk.ChunkIndex == chunk.ChunkCount - 1)
                {
                    break;
                }
            }
        }

        return [.. ids];
    }

    // The corpse ids of every corpse presence the peer was sent, one list per presence.
    private static List<Guid[]> PresentedCorpses(RecordingPeerConnection peer)
    {
        var presences = new List<Guid[]>();
        foreach (var send in peer.Sent)
        {
            if (RealtimeProtocol.TryReadMessageType(send.Data, out var type)
                && type == RealtimeMessageType.CorpsePresenceSnapshotChunk
                && RealtimeProtocol.TryDecodeCorpsePresenceSnapshotChunk(send.Data, out var chunk, out _))
            {
                Assert.Equal(1, chunk.ChunkCount);
                presences.Add(chunk.Corpses.Select(corpse => corpse.CorpseId).ToArray());
            }
        }

        return presences;
    }

    private static void SeedDurableCorpse(DurableCorpseStore store, Guid corpseId, double positionX, long revision)
    {
        var now = DateTime.UtcNow;
        store.Apply(new DurableCorpseResponse(
            corpseId,
            null,
            "Fallen Hero",
            RealtimeServerHarness.ShardId,
            positionX,
            0d,
            -1d,
            0d,
            0d,
            0d,
            1d,
            "player_corpse",
            revision,
            now.AddSeconds(-1),
            now.AddMinutes(5),
            false,
            [
                new CorpseSectionResponse("general_inventory", Guid.NewGuid(), 0, 1),
                new CorpseSectionResponse("equipment", Guid.NewGuid(), 0, 0),
                new CorpseSectionResponse("bag", Guid.NewGuid(), 0, 0)
            ]));
    }

    private static LiveMobCorpseState LiveCorpse(Guid lootGroupId, Guid owner)
    {
        var now = DateTime.UtcNow;
        return new LiveMobCorpseState(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "mob.feral_alpha",
            RealtimeServerHarness.ShardId,
            1d,
            0d,
            -1d,
            0f,
            MobCorpseLifecycleService.DefaultPresentationKey,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            now,
            now.AddMinutes(10),
            [new LiveMobCorpseLootEntry(Guid.NewGuid(), Guid.NewGuid(), "fixture.ore", 1, 0)])
        {
            OwnerCharacterId = owner,
            LootGroupId = lootGroupId
        };
    }

    // The loop with its guard, as the composition builds it.
    internal static SimulationTickLoop CreateLoop(
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
    internal static void RunTicks(SimulationTickLoop loop, ref TimeSpan nextTick, int count)
    {
        for (var tick = 0; tick < count; tick++)
        {
            loop.ProcessDueTicks(nextTick, TickInterval, ref nextTick);
        }
    }
}
