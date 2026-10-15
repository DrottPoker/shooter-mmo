using LiteNetLib;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

internal readonly record struct RealtimeInboundMessage(
    IRealtimePeerConnection Peer,
    RealtimePeerContext Context,
    RealtimeMessageType Type,
    byte[] Packet,
    byte Channel,
    DeliveryMethod Delivery);

internal delegate void RealtimeMessageHandler(in RealtimeInboundMessage message);

internal readonly record struct RealtimeRejection(string Code, string Message);

// Accepts one message type on one channel and delivery method. A message that arrives
// another way, or while RejectWhen holds for the peer, is rejected with
// DeliveryRejection before the handler runs.
internal sealed record RealtimeMessageRoute(
    RealtimeMessageType Type,
    byte Channel,
    DeliveryMethod Delivery,
    RealtimeRejection DeliveryRejection,
    RealtimeMessageHandler Handler)
{
    public Func<RealtimePeerContext, bool>? RejectWhen { get; init; }
}
