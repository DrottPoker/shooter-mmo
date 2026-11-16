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

    // Sends a packet that a reusable buffer holds; the transport copies it.
    public void SendUnreliable(IRealtimePeerConnection peer, ReadOnlySpan<byte> packet)
    {
        try
        {
            peer.Send(packet, DeliveryMethod.Unreliable);
        }
        catch (Exception exception)
        {
            DisconnectAfterFailedSend(peer, exception);
            return;
        }

        networkMetrics.RecordSent(packet.Length);
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

    // Sends the reliable LeaveAccepted, ServerDisconnect, or JoinRejected that says why the
    // connection ends, and starts the peer's disconnect, which RealtimePeerSupervisor carries out
    // once the client acknowledged the message or the wait reached
    // GracefulDisconnectMaximumMilliseconds. The disconnect carries the message as its data, so a
    // client whose reliable copy was lost or held back still learns why. A peer that already goes
    // keeps its first reason and gets no second message.
    public void SendAndDisconnect(IRealtimePeerConnection peer, RealtimePeerContext context, byte[] packet)
    {
        SendReason(peer, context, packet, reportFailure: true);
    }

    private void SendReason(
        IRealtimePeerConnection peer,
        RealtimePeerContext context,
        byte[] packet,
        bool reportFailure)
    {
        var disconnect = context.BeginDisconnect(RealtimeDisconnectData.Fit(packet), DateTime.UtcNow);
        if (disconnect is null)
        {
            return;
        }

        try
        {
            peer.SendDisconnectReason(packet, disconnect);
        }
        catch (Exception exception)
        {
            // Nothing acknowledges a message that was not sent; the disconnect carries it alone.
            disconnect.MarkAcknowledged();
            if (reportFailure)
            {
                ReportFailedSend(peer, exception);
            }

            return;
        }

        networkMetrics.RecordSent(packet.Length);
    }

    // Counts and logs the first failed send of a peer and starts its disconnect; the sends that
    // still follow before the peer is gone fail quietly.
    private void DisconnectAfterFailedSend(IRealtimePeerConnection peer, Exception exception)
    {
        RealtimePeerContext? context = null;
        if (peers is not null && peers.TryGetContext(peer.Id, out context) && context.PendingDisconnect is not null)
        {
            return;
        }

        ReportFailedSend(peer, exception);
        if (context is null)
        {
            return;
        }

        // The reliable channel may still work when an unreliable send failed, and the disconnect
        // carries the reason either way.
        SendReason(
            peer,
            context,
            RealtimeProtocol.EncodeServerDisconnect(RealtimeFailureIsolation.Code, RealtimeFailureIsolation.Message),
            reportFailure: false);
    }

    private void ReportFailedSend(IRealtimePeerConnection peer, Exception exception)
    {
        if (loopGuard is not null)
        {
            loopGuard.PeerFailed(SendStepName, peer.Id, exception);
        }
        else
        {
            logger.LogError(exception, "[SIMULATION] A send to UDP peer {PeerId} failed.", peer.Id);
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
        SendAndDisconnect(peer, context, RealtimeProtocol.EncodeServerDisconnect(code, message));
    }
}
