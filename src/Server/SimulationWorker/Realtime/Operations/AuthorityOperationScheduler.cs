using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Realtime;

// Serializes item, corpse and world interaction authority per peer: at most one
// operation runs while the rest wait in the peer's queue. Finalizers release the lane and
// call StartNext. While GameService does not answer, a client operation that it must commit
// never enters the lane, and one that waited in it is refused instead of started, so no lane
// waits for a timeout. Loop-confined.
internal sealed class AuthorityOperationScheduler(
    RealtimePeerRegistry peers,
    GameServiceAvailability? availability = null)
{
    // True while GameService does not answer: the caller refuses its client operation of this
    // kind at once with game_service_unavailable instead of queuing it. The refusal is counted.
    public bool RefusesDurableIntent(string kind)
    {
        if (availability is null || availability.IsAvailable)
        {
            return false;
        }

        availability.RecordRefusedIntent(kind);
        return true;
    }

    public void Enqueue(RealtimePeerContext context, IRealtimeAuthorityOperation operation)
    {
        context.AuthorityOperationQueue.Enqueue(operation);
        StartNext(context);
    }

    public void StartNext(RealtimePeerContext context)
    {
        while (!context.AuthorityOperationInFlight
            && context.AuthorityOperationQueue.Count > 0
            && context.Session is not null
            && peers.TryGetBoundPlayer(context, out var entity))
        {
            var operation = context.AuthorityOperationQueue.Dequeue();
            if (operation.DurableIntentKind is { } kind && RefusesDurableIntent(kind))
            {
                operation.Refuse(
                    context,
                    GameServiceAvailability.UnavailableCode,
                    GameServiceAvailability.UnavailableMessage);
                continue;
            }

            var state = entity.Movement.State;
            context.AuthorityOperationInFlight = true;
            operation.Start(context, entity, state);
            return;
        }
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
