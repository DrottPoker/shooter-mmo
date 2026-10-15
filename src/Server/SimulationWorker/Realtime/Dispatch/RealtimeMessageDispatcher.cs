using LiteNetLib;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// Applies the per-packet checks every realtime message passes, then calls the route of
// its message type from the table that matches the peer's session state. Loop-confined.
internal sealed class RealtimeMessageDispatcher
{
    private readonly RealtimeMessageRoute?[] unauthenticatedRoutes = new RealtimeMessageRoute?[256];
    private readonly RealtimeMessageRoute?[] authenticatedRoutes = new RealtimeMessageRoute?[256];
    private readonly RealtimePeerRegistry peers;
    private readonly RealtimePeerSender sender;
    private readonly RealtimeNetworkMetrics networkMetrics;
    private readonly ILogger logger;

    public RealtimeMessageDispatcher(
        RealtimePeerRegistry peers,
        RealtimePeerSender sender,
        RealtimeNetworkMetrics networkMetrics,
        ILogger logger,
        IEnumerable<RealtimeMessageRoute> unauthenticatedRoutes,
        IEnumerable<RealtimeMessageRoute> authenticatedRoutes)
    {
        this.peers = peers;
        this.sender = sender;
        this.networkMetrics = networkMetrics;
        this.logger = logger;
        AddRoutes(this.unauthenticatedRoutes, unauthenticatedRoutes);
        AddRoutes(this.authenticatedRoutes, authenticatedRoutes);
    }

    public void Dispatch(
        IRealtimePeerConnection peer,
        byte[] packet,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        try
        {
            if (!peers.TryGetContext(peer.Id, out var context))
            {
                return;
            }

            networkMetrics.RecordReceived(packet.Length);
            if (context.DisconnectAfterUtc is not null)
            {
                return;
            }

            if (!context.Quota.TryConsumeInbound(packet.Length))
            {
                networkMetrics.RecordQuotaRejected();
                sender.RejectProtocol(
                    peer,
                    context,
                    "udp_rate_limited",
                    "The realtime connection exceeded its inbound UDP quota.");
                return;
            }
            if (!RealtimeProtocol.TryReadMessageType(packet, out var messageType))
            {
                sender.RejectProtocol(peer, context, "invalid_packet", "Realtime packet header is invalid.");
                return;
            }

            var session = context.Session;
            var routes = session is null ? unauthenticatedRoutes : authenticatedRoutes;
            var route = routes[(byte)messageType];
            if (route is null)
            {
                var fallback = session is null
                    ? RealtimeMessageRoutes.JoinRequired
                    : RealtimeMessageRoutes.UnexpectedMessage;
                sender.RejectProtocol(peer, context, fallback.Code, fallback.Message);
                return;
            }

            if (channel != route.Channel
                || deliveryMethod != route.Delivery
                || route.RejectWhen?.Invoke(context) == true)
            {
                sender.RejectProtocol(
                    peer,
                    context,
                    route.DeliveryRejection.Code,
                    route.DeliveryRejection.Message);
                return;
            }

            route.Handler(new RealtimeInboundMessage(
                peer,
                context,
                messageType,
                packet,
                channel,
                deliveryMethod));
        }
        catch (Exception exception)
        {
            // A packet that fails unexpectedly disconnects only its own peer.
            logger.LogError(
                exception,
                "[SIMULATION] Could not process a realtime packet from UDP peer {PeerId}.",
                peer.Id);
            if (peers.TryGetContext(peer.Id, out var failedContext))
            {
                sender.RejectProtocol(
                    peer,
                    failedContext,
                    "internal_error",
                    "SimulationWorker could not process the request.");
            }
        }
    }

    private static void AddRoutes(
        RealtimeMessageRoute?[] table,
        IEnumerable<RealtimeMessageRoute> routes)
    {
        foreach (var route in routes)
        {
            if (table[(byte)route.Type] is not null)
            {
                throw new InvalidOperationException(
                    $"Realtime message type {route.Type} has more than one route.");
            }

            table[(byte)route.Type] = route;
        }
    }
}
