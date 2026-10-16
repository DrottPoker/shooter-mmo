using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// Hands decoded movement inputs to the peer's authoritative movement. Loop-confined.
internal sealed class MovementInputHandler(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender)
{
    public void HandleInput(in RealtimeInboundMessage message)
    {
        var peer = message.Peer;
        var context = message.Context;
        var packet = message.Packet;
        if (!peers.TryGetBoundPlayer(context, out var playerEntity))
        {
            sender.RejectProtocol(
                peer,
                context,
                "movement_unavailable",
                "Movement simulation is not active for this peer.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeMovementInputBatch(packet, out var batch, out var movementError))
        {
            sender.RejectProtocol(
                peer,
                context,
                "invalid_movement_input",
                movementError);
            return;
        }

        if (playerEntity.Movement.AcceptInputs(batch) == MovementInputAcceptance.UnknownPath)
        {
            sender.RejectProtocol(
                peer,
                context,
                "unknown_movement_path",
                "The movement input names a path the worker never received.");
        }
    }
}
