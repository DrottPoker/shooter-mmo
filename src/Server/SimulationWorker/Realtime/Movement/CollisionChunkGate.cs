using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.WorldCollision;

namespace Mmorpg.SimulationWorker.Realtime;

// Keeps everything off collision chunks that are still loading, because a missing chunk is no
// collision at all. A mover steps only where its own chunk and its neighbours are loaded; one that
// may not is counted and tries again next tick. A join or a teleport to a place whose chunks are
// not loaded waits: its completion is parked here, the place keeps its chunks loading like a
// player standing there, and once they are in, the completion goes back to the completion queue,
// whose next pass finalizes it. The place stays held until that pass came by: a tick between the
// release and the pass must not unload the chunks again, or the completion would wait for them
// once more. Without a streaming store everything is loaded. Loop-confined.
internal sealed class CollisionChunkGate(
    WorldCollisionStreamingStore? store,
    RealtimeCompletionQueue completions,
    RealtimePerformanceMetrics metrics) : IRealtimeCompletionHandler
{
    private readonly List<PlacementWait> waits = [];

    // The places of released completions that the completion queue has not passed yet.
    private readonly List<PlacementHold> holds = [];
    private long nextHoldId;

    public int WaitingCount => waits.Count;

    public int HeldCount => holds.Count;

    // Whether a mover at the position may step in this tick.
    public bool CanStep(SimulationVector3 position)
    {
        if (store is null || store.IsAreaLoaded(position))
        {
            return true;
        }

        metrics.RecordCollisionPendingStep();
        return false;
    }

    // Whether something may be placed at the position now.
    public bool IsLoadedAround(SimulationVector3 position)
    {
        return store is null || store.IsAreaLoaded(position);
    }

    // Finalizes the completion again once the chunks around the position are loaded.
    public void WaitFor(
        SimulationVector3 position,
        IRealtimeCompletionHandler handler,
        RealtimeOperationResult completion)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(completion);
        waits.Add(new PlacementWait(position, handler, completion));
    }

    // The places that wait or are held, as anchors of the collision streaming.
    public void AddAnchorsTo(List<SimulationVector3> anchors)
    {
        foreach (var wait in waits)
        {
            anchors.Add(wait.Position);
        }

        foreach (var hold in holds)
        {
            anchors.Add(hold.Position);
        }
    }

    // The completion queue passed a released completion: its place is no longer held. What the
    // completion placed there is an anchor of its own from the next tick on.
    public void Complete(RealtimeOperationResult completion)
    {
        if (completion is not PlacementHoldEnded ended)
        {
            return;
        }

        for (var index = 0; index < holds.Count; index++)
        {
            if (holds[index].Id == ended.HoldId)
            {
                holds.RemoveAt(index);
                return;
            }
        }
    }

    // After the streaming took finished chunks in: posts the completions whose places are loaded
    // now, in the order they started to wait, each followed by the end of its hold.
    public void ReleaseLoadedPlaces()
    {
        if (waits.Count == 0)
        {
            return;
        }

        var kept = 0;
        for (var index = 0; index < waits.Count; index++)
        {
            var wait = waits[index];
            if (IsLoadedAround(wait.Position))
            {
                var holdId = ++nextHoldId;
                holds.Add(new PlacementHold(holdId, wait.Position));
                completions.Post(wait.Handler, wait.Completion);
                completions.Post(this, new PlacementHoldEnded(wait.Completion.Context, holdId));
                continue;
            }

            waits[kept++] = wait;
        }

        waits.RemoveRange(kept, waits.Count - kept);
    }

    private readonly record struct PlacementHold(long Id, SimulationVector3 Position);

    private readonly record struct PlacementWait(
        SimulationVector3 Position,
        IRealtimeCompletionHandler Handler,
        RealtimeOperationResult Completion);
}
