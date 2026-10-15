using LiteNetLib;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// Sends realtime packets and records the sent-packet metrics. Loop-confined.
internal sealed class RealtimePeerSender(
    RealtimeNetworkMetrics networkMetrics,
    ILogger logger)
{
    public void SendControl(IRealtimePeerConnection peer, byte[] packet)
    {
        peer.Send(packet, DeliveryMethod.ReliableOrdered);
        networkMetrics.RecordSent(packet.Length);
    }

    public void SendUnreliable(IRealtimePeerConnection peer, byte[] packet)
    {
        peer.Send(packet, DeliveryMethod.Unreliable);
        networkMetrics.RecordSent(packet.Length);
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
