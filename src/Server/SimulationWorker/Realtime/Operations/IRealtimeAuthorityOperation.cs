using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Entities;

namespace Mmorpg.SimulationWorker.Realtime;

// One queued authority operation of a peer. It starts itself through the handler that
// queued it, on the realtime loop, with the player state captured at dequeue time.
internal interface IRealtimeAuthorityOperation
{
    void Start(
        RealtimePeerContext context,
        PlayerSimulationEntity entity,
        PlayerMovementState state);

    // Rejects a client operation that never started, such as one waiting behind a death.
    // System operations are never cancelled this way.
    void Cancel(RealtimePeerContext context, string code, string message)
    {
    }
}
