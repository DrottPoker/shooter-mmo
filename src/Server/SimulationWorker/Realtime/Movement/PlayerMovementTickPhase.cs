using System.Diagnostics;

namespace Mmorpg.SimulationWorker.Realtime;

// Applies each player's latest committed carry state and then steps its movement, in entity
// id order. A player whose step throws leaves the Shard alone; the others still move.
// Loop-confined.
internal sealed class PlayerMovementTickPhase(
    CarryStateSynchronizer carrySync,
    RealtimePerformanceMetrics performanceMetrics,
    RealtimeFailureIsolation isolation)
{
    public const string Name = "movement";

    public void AddTickPhase(SimulationTickPhases phases)
    {
        phases.Add(SimulationTickOrder.Movement, Name, Run);
    }

    private void Run(in SimulationTick tick)
    {
        var started = Stopwatch.GetTimestamp();
        foreach (var entity in tick.Players)
        {
            try
            {
                carrySync.ApplyLatest(entity);
                entity.Movement.SimulateTick();
            }
            catch (Exception exception)
            {
                isolation.RemovePlayer(Name, entity, exception);
            }
        }

        performanceMetrics.RecordMovementSimulation(Stopwatch.GetElapsedTime(started));
    }
}
