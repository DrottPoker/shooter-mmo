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
}
