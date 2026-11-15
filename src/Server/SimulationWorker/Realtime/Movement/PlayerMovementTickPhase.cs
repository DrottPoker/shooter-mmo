using System.Diagnostics;

namespace Mmorpg.SimulationWorker.Realtime;

// Applies each player's latest committed carry state and then steps its movement, in entity
// id order. A player whose step throws leaves the Shard alone; the others still move. A player
// whose collision chunks still load does not step in this tick; its inputs wait for the next.
// Loop-confined.
internal sealed class PlayerMovementTickPhase(
    CarryStateSynchronizer carrySync,
    RealtimePerformanceMetrics performanceMetrics,
    RealtimeFailureIsolation isolation,
    CollisionChunkGate? collisionChunks = null)
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
                if (collisionChunks?.CanStep(entity.Movement.State.Position) ?? true)
                {
                    entity.Movement.SimulateTick();
                }
            }
            catch (Exception exception)
            {
                isolation.RemovePlayer(Name, entity, exception);
            }
        }

        performanceMetrics.RecordMovementSimulation(Stopwatch.GetElapsedTime(started));
    }
}
