namespace Mmorpg.SimulationWorker.Realtime;

// Serializes item, corpse and world interaction authority per peer: at most one
// operation runs while the rest wait in the peer's queue. Finalizers clear the in-flight
// flag and call StartNext. Loop-confined.
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
}
