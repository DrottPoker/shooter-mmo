using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Entities;

namespace Mmorpg.SimulationWorker.Realtime;

// One queued authority operation of a peer. It starts itself through the handler that
// queued it, on the realtime loop, with the player state captured at dequeue time.
internal interface IRealtimeAuthorityOperation
{
    // The kind of a client operation that GameService must commit, such as item_operation, which
    // is refused instead of started while GameService does not answer. Null for the system
    // operations, which wait for GameService, and for client operations it takes no part in.
    string? DurableIntentKind => null;

    void Start(
        RealtimePeerContext context,
        PlayerSimulationEntity entity,
        PlayerMovementState state);

    // Rejects a client operation that never started, such as one waiting behind a death.
    // System operations are never cancelled this way.
    void Cancel(RealtimePeerContext context, string code, string message)
    {
    }

    // Rejects a client operation that never started because GameService does not answer.
    void Refuse(RealtimePeerContext context, string code, string message)
    {
        Cancel(context, code, message);
    }
}
