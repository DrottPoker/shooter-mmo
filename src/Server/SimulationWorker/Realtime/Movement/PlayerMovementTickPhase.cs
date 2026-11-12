using System.Diagnostics;

namespace Mmorpg.SimulationWorker.Realtime;

// Applies each player's latest committed carry state and then steps its movement, in entity
// id order. Loop-confined.
internal sealed class PlayerMovementTickPhase(
    CarryStateSynchronizer carrySync,
    RealtimePerformanceMetrics performanceMetrics)
{
    public void AddTickPhase(SimulationTickPhases phases)
    {
        phases.Add(SimulationTickOrder.Movement, "movement", Run);
    }

    private void Run(in SimulationTick tick)
    {
        var started = Stopwatch.GetTimestamp();
        foreach (var entity in tick.Players)
        {
            carrySync.ApplyLatest(entity);
            entity.Movement.SimulateTick();
        }

        performanceMetrics.RecordMovementSimulation(Stopwatch.GetElapsedTime(started));
    }
}
