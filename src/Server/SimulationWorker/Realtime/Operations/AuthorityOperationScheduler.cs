namespace Mmorpg.SimulationWorker.Realtime;

// Serializes item, corpse and world interaction authority per peer: at most one
// operation runs while the rest wait in the peer's queue. Finalizers release the lane and
// call StartNext. Loop-confined.
internal sealed class AuthorityOperationScheduler(RealtimePeerRegistry peers)
{
    public void Enqueue(RealtimePeerContext context, IRealtimeAuthorityOperation operation)
    {
        context.AuthorityOperationQueue.Enqueue(operation);
        StartNext(context);
    }

    public void StartNext(RealtimePeerContext context)
    {
        if (context.AuthorityOperationInFlight
            || context.AuthorityOperationQueue.Count == 0
            || context.Session is null
            || !peers.TryGetBoundPlayer(context, out var entity))
        {
            return;
        }

        var operation = context.AuthorityOperationQueue.Dequeue();
        var state = entity.Movement.State;
        context.AuthorityOperationInFlight = true;
        operation.Start(context, entity, state);
    }

    // A reconnect moved a session to a new peer while an operation still runs on the old
    // peer's lane. The new lane stays closed until that operation ends, so the session's
    // operations keep their order.
    public static void HandOver(RealtimePeerContext previous, RealtimePeerContext next)
    {
        previous.LaneSuccessor = next;
        next.AuthorityOperationInFlight = true;
    }

    // Ends the operation that holds the peer's lane. A lane that a reconnect handed on opens
    // its successor's lane, even when the old peer is gone.
    public void Release(RealtimePeerContext context)
    {
        context.AuthorityOperationInFlight = false;
        if (context.LaneSuccessor is not { } successor)
        {
            return;
        }

        context.LaneSuccessor = null;
        Release(successor);
        StartNext(successor);
    }
}
