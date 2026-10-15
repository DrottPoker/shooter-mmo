using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Items;

namespace Mmorpg.SimulationWorker.Realtime;

// Applies the latest committed carry state to a player entity and tells the owning peer
// when it changed. Other services commit carry state from any thread, so the loop polls
// the store through this class every tick. Loop-confined.
internal sealed class CarryStateSynchronizer(
    CarryStateStore carryStateStore,
    ConnectionEntityBindingRegistry connectionBindings,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender)
{
    public void ApplyLatest(PlayerSimulationEntity entity)
    {
        if (!carryStateStore.TryGet(
                entity.Session.CharacterId,
                entity.Session.SimulationSessionId,
                out var carryState)
            || !entity.Movement.ApplyCarryState(carryState!))
        {
            return;
        }

        if (!connectionBindings.TryGetConnectionId(
                entity.NetworkEntityId,
                out var connectionId)
            || !peers.TryGetContext(connectionId, out var context)
            || context.Session?.SimulationSessionId != entity.Session.SimulationSessionId
            || !peers.TryGetCurrentPeer(context, out var peer))
        {
            return;
        }

        sender.SendControl(
            peer,
            RealtimeProtocol.EncodeCarryStateChanged(
                RealtimeStateMapper.ToRealtimeCarryState(entity.Movement.CarryState)));
    }
}
