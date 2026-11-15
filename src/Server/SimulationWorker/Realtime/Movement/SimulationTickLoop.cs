using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Runs due fixed simulation ticks. Each tick advances the clock, fills the players of the tick
// once in entity id order, and runs the registered phases in SimulationTickOrder. A phase that
// throws is logged and skipped for this tick through the loop guard, and the later phases still
// run, unless the phase is a prerequisite of them, which ends the tick. At most five ticks catch up per call before the clock resynchronizes. Loop-confined.
internal sealed class SimulationTickLoop
{
    private readonly RealtimeSimulationClock clock;
    private readonly SimulationEntityRegistry entityRegistry;
    private readonly RealtimePerformanceMetrics performanceMetrics;
    private readonly RealtimeLoopGuard loopGuard;
    private readonly ILogger logger;
    private readonly SimulationTickPhase[] orderedPhases;
    private readonly RealtimeLoopStep[] phaseSteps;
    private readonly List<PlayerSimulationEntity> playerBuffer = [];
    private readonly PlayerSpatialGrid playerGrid = new();

    public SimulationTickLoop(
        RealtimeSimulationClock clock,
        SimulationEntityRegistry entityRegistry,
        SimulationTickPhases phases,
        RealtimePerformanceMetrics performanceMetrics,
        RealtimeLoopGuard loopGuard,
        ILogger logger)
    {
        this.clock = clock;
        this.entityRegistry = entityRegistry;
        this.performanceMetrics = performanceMetrics;
        this.loopGuard = loopGuard;
        this.logger = logger;
        orderedPhases = phases.InTickOrder();
        phaseSteps = [.. orderedPhases.Select(phase => loopGuard.TickPhase(phase.Name))];
    }

    public IReadOnlyList<SimulationTickPhase> Phases => orderedPhases;

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
                    // A phase that was not due did no work, so its failed runs still count.
                    if (orderedPhases[index].Run(in tick))
                    {
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

            performanceMetrics.RecordSimulationTick(
                Stopwatch.GetElapsedTime(tickStarted),
                simulationInterval);
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
