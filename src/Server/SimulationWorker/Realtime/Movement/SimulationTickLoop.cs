using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Runs due fixed simulation ticks. Each tick advances the clock, fills the players of the tick
// once in entity id order, and runs the registered phases in SimulationTickOrder, timing each run
// that did its work under the phase's name with the garbage collection pause time inside it. With
// a CPU clock it also reads the loop thread's CPU time around each tick. A phase that throws is
// logged and skipped for this tick through the loop guard, and the later phases still run,
// unless the phase is a prerequisite of them, which ends the tick. At most five ticks catch up per
// call before the clock resynchronizes. Loop-confined.
internal sealed class SimulationTickLoop
{
    private readonly RealtimeSimulationClock clock;
    private readonly SimulationEntityRegistry entityRegistry;
    private readonly RealtimePerformanceMetrics performanceMetrics;
    private readonly RealtimeLoopGuard loopGuard;
    private readonly ILogger logger;
    private readonly RealtimeNetworkMetrics? networkMetrics;
    private readonly ThreadCpuClock? cpuClock;
    private readonly SimulationTickPhase[] orderedPhases;
    private readonly RealtimeLoopStep[] phaseSteps;
    private readonly RealtimePerformanceMetrics.DurationTimer[] phaseTimers;
    private readonly List<PlayerSimulationEntity> playerBuffer = [];
    private readonly PlayerSpatialGrid playerGrid = new();

    // With network metrics, every tick also counts the spawn and despawn packets it sent, for the
    // slowest tick's breakdown; with a CPU clock, which only the loop thread reads, it measures
    // how long the thread ran in each tick.
    public SimulationTickLoop(
        RealtimeSimulationClock clock,
        SimulationEntityRegistry entityRegistry,
        SimulationTickPhases phases,
        RealtimePerformanceMetrics performanceMetrics,
        RealtimeLoopGuard loopGuard,
        ILogger logger,
        RealtimeNetworkMetrics? networkMetrics = null,
        ThreadCpuClock? cpuClock = null)
    {
        this.clock = clock;
        this.entityRegistry = entityRegistry;
        this.performanceMetrics = performanceMetrics;
        this.loopGuard = loopGuard;
        this.logger = logger;
        this.networkMetrics = networkMetrics;
        this.cpuClock = cpuClock;
        orderedPhases = phases.InTickOrder();
        phaseSteps = [.. orderedPhases.Select(phase => loopGuard.TickPhase(phase.Name))];
        phaseTimers = [.. orderedPhases.Select(phase => performanceMetrics.TickPhaseTimer(phase.Name))];
    }

    public IReadOnlyList<SimulationTickPhase> Phases => orderedPhases;

    public ThreadCpuClock? CpuClock => cpuClock;

    public void ProcessDueTicks(
        TimeSpan elapsed,
        TimeSpan simulationInterval,
        ref TimeSpan nextSimulationTick)
    {
        const int maximumCatchUpTicks = 5;
        var processedTicks = 0;

        while (elapsed >= nextSimulationTick && processedTicks < maximumCatchUpTicks)
        {
            var tickStarted = Stopwatch.GetTimestamp();
            var cpuStarted = cpuClock?.Read() ?? 0L;
            // The process's total garbage collection pause time, read around the tick and each
            // phase, tells which samples a pause fell into.
            var tickPausesBefore = GC.GetTotalPauseDuration();
            var spawnsBefore = networkMetrics?.SpawnPackets ?? 0;
            var despawnsBefore = networkMetrics?.DespawnPackets ?? 0;
            performanceMetrics.RecordSimulationTickLag(elapsed - nextSimulationTick);
            clock.Advance();

            entityRegistry.CopyPlayersTo(playerBuffer);
            var tick = new SimulationTick(
                clock.ServerTick,
                new SimulationTickPlayers(playerBuffer),
                playerGrid);
            for (var index = 0; index < orderedPhases.Length; index++)
            {
                try
                {
                    // A phase that was not due did no work: its failed runs still count, and the
                    // tick is no sample of its duration.
                    var phaseStarted = Stopwatch.GetTimestamp();
                    var phasePausesBefore = GC.GetTotalPauseDuration();
                    if (orderedPhases[index].Run(in tick))
                    {
                        phaseTimers[index].Record(
                            Stopwatch.GetElapsedTime(phaseStarted),
                            GC.GetTotalPauseDuration() - phasePausesBefore);
                        phaseSteps[index].Succeeded();
                    }
                }
                catch (Exception exception)
                {
                    loopGuard.Failed(phaseSteps[index], exception);
                    if (orderedPhases[index].IsPrerequisite)
                    {
                        // The later phases rely on this one, so the tick ends here.
                        break;
                    }
                }
            }

            double? cpuMilliseconds = null;
            var cpuEnded = cpuClock?.Read() ?? 0L;
            var tickDuration = Stopwatch.GetElapsedTime(tickStarted);
            if (cpuClock is not null)
            {
                cpuMilliseconds = cpuClock.Milliseconds(cpuStarted, cpuEnded, tickDuration.TotalMilliseconds);
            }

            performanceMetrics.RecordSimulationTick(
                tickDuration,
                simulationInterval,
                GC.GetTotalPauseDuration() - tickPausesBefore,
                (networkMetrics?.SpawnPackets ?? 0) - spawnsBefore,
                (networkMetrics?.DespawnPackets ?? 0) - despawnsBefore,
                cpuMilliseconds);
            nextSimulationTick += simulationInterval;
            processedTicks++;
        }

        if (processedTicks == maximumCatchUpTicks && elapsed >= nextSimulationTick)
        {
            logger.LogWarning(
                "[SIMULATION] Movement simulation exceeded its catch-up budget at server tick {ServerTick}. Resynchronizing the fixed-tick clock.",
                clock.ServerTick);
            performanceMetrics.RecordTickResynchronization();
            nextSimulationTick = elapsed + simulationInterval;
        }
    }
}
