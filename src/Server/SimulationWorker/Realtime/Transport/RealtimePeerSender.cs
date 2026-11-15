using LiteNetLib;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// Sends realtime packets and records the sent-packet metrics. A send never throws: a packet
// that cannot be sent leaves that client behind what the worker told it, so the peer is
// disconnected alone, with internal_error when that still reaches it, and its character stays
// as a body. Every caller, such as a spawn in the middle of an interest refresh or a despawn in
// the middle of a teardown, therefore goes on with the other peers. Loop-confined.
internal sealed class RealtimePeerSender(
    RealtimeNetworkMetrics networkMetrics,
    ILogger logger,
    RealtimePeerRegistry? peers = null,
    RealtimeLoopGuard? loopGuard = null)
{
    public const string SendStepName = "send";

    public void SendControl(IRealtimePeerConnection peer, byte[] packet)
    {
        Send(peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendUnreliable(IRealtimePeerConnection peer, byte[] packet)
    {
        Send(peer, packet, DeliveryMethod.Unreliable);
    }

    private void Send(IRealtimePeerConnection peer, byte[] packet, DeliveryMethod deliveryMethod)
    {
        try
        {
            peer.Send(packet, deliveryMethod);
        }
        catch (Exception exception)
        {
            DisconnectAfterFailedSend(peer, exception);
            return;
        }

        networkMetrics.RecordSent(packet.Length);
    }

    // Counts and logs the first failed send of a peer and schedules its disconnect; the sends
    // that still follow before the peer is gone fail quietly.
    private void DisconnectAfterFailedSend(IRealtimePeerConnection peer, Exception exception)
    {
        RealtimePeerContext? context = null;
        if (peers is not null && peers.TryGetContext(peer.Id, out context) && context.DisconnectAfterUtc is not null)
        {
            return;
        }

        if (loopGuard is not null)
        {
            loopGuard.PeerFailed(SendStepName, peer.Id, exception);
        }
        else
        {
            logger.LogError(exception, "[SIMULATION] A send to UDP peer {PeerId} failed.", peer.Id);
        }

        if (context is null)
        {
            return;
        }

        context.ScheduleDisconnect(DateTime.UtcNow);
        try
        {
            // The reliable channel may still work when an unreliable send failed.
            peer.Send(
                RealtimeProtocol.EncodeServerDisconnect(RealtimeFailureIsolation.Code, RealtimeFailureIsolation.Message),
                DeliveryMethod.ReliableOrdered);
        }
        catch (Exception)
        {
            // The peer goes without the message.
        }
    }

    public void RejectProtocol(
        IRealtimePeerConnection peer,
        RealtimePeerContext context,
        string code,
        string message)
    {
        logger.LogWarning(
            "[SIMULATION] Disconnecting UDP peer {PeerId} with code {Code}: {Message}",
            context.PeerId,
            code,
            message);
        SendControl(peer, RealtimeProtocol.EncodeServerDisconnect(code, message));
        context.ScheduleDisconnect(DateTime.UtcNow);
    }
}
