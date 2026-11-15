using System.Diagnostics;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.WorldCollision;

namespace Mmorpg.SimulationWorker.Realtime;

// Keeps the collision chunks around the players, the World spawn, the moving Mobs, and the places
// that a join or a teleport waits for loaded. The store plans only when an anchor changed its
// chunk, loads on its background loader, and takes the finished chunks in here; then the waiting
// joins and teleports whose places are loaded go back to the completion queue. No file is read
// here. Without a streaming store it does nothing, but its duration is still recorded.
// Loop-confined.
internal sealed class CollisionStreamingTickPhase(
    SimulationWorkerConfig config,
    MobSystem mobs,
    WorldCollisionStreamingStore? collisionStreamingStore,
    CollisionChunkGate gate,
    RealtimePerformanceMetrics performanceMetrics)
{
    private readonly List<SimulationVector3> anchorBuffer = [];

    public void AddTickPhase(SimulationTickPhases phases)
    {
        // Movement steps against the loaded chunks, and a missing chunk is no collision at all.
        phases.AddPrerequisite(SimulationTickOrder.CollisionStreaming, "collision streaming", Run);
    }

    private void Run(in SimulationTick tick)
    {
        var started = Stopwatch.GetTimestamp();
        Refresh(tick.Players);
        performanceMetrics.RecordCollisionStreaming(Stopwatch.GetElapsedTime(started));
    }

    private void Refresh(SimulationTickPlayers players)
    {
        if (collisionStreamingStore is null)
        {
            return;
        }

        anchorBuffer.Clear();
        foreach (var entity in players)
        {
            anchorBuffer.Add(new SimulationVector3(
                entity.Movement.State.PositionX,
                entity.Movement.State.PositionY,
                entity.Movement.State.PositionZ));
        }

        anchorBuffer.Add(new SimulationVector3(
            config.MovementSpawn.X,
            config.MovementSpawn.Y,
            config.MovementSpawn.Z));
        mobs.CopyCollisionAnchorsTo(anchorBuffer);
        gate.AddAnchorsTo(anchorBuffer);
        collisionStreamingStore.Refresh(anchorBuffer);
        gate.ReleaseLoadedPlaces();
    }
}
