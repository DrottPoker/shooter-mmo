using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Runs due fixed simulation ticks. Each tick advances the clock, fills the players of the tick
// once in entity id order, and runs the registered phases in SimulationTickOrder. At most five
// ticks catch up per call before the clock resynchronizes. Loop-confined.
internal sealed class SimulationTickLoop(
    RealtimeSimulationClock clock,
    SimulationEntityRegistry entityRegistry,
    SimulationTickPhases phases,
    RealtimePerformanceMetrics performanceMetrics,
    ILogger logger)
{
    private readonly SimulationTickPhase[] orderedPhases = phases.InTickOrder();
    private readonly List<PlayerSimulationEntity> playerBuffer = [];
    private readonly PlayerSpatialGrid playerGrid = new();

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
            foreach (var phase in orderedPhases)
            {
                phase.Run(in tick);
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
