using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// The realtime route table that features add their routes to during composition, and that the
// dispatcher builds its tables from. A peer without a session may only join; a joined peer
// reaches the authenticated routes. Each route takes its channel and delivery from the
// generated protocol descriptor. Rejection codes and texts are part of the client protocol,
// and each feature keeps its own next to the handler that adds the route.
internal sealed class RealtimeMessageRoutes
{
    public static readonly RealtimeRejection JoinRequired = new(
        "join_required",
        "A single join request must complete before other messages are accepted.");

    public static readonly RealtimeRejection UnexpectedMessage = new(
        "unexpected_message",
        "The message is not valid for the current connection state.");

    private readonly List<RealtimeMessageRoute> unauthenticated = [];
    private readonly List<RealtimeMessageRoute> authenticated = [];

    public IReadOnlyList<RealtimeMessageRoute> Unauthenticated => unauthenticated;

    public IReadOnlyList<RealtimeMessageRoute> Authenticated => authenticated;

    // A route for a peer without a session.
    public void AddUnauthenticated(
        RealtimeMessageType type,
        RealtimeRejection deliveryRejection,
        RealtimeMessageHandler handler,
        Func<RealtimePeerContext, bool>? rejectWhen = null)
    {
        unauthenticated.Add(Route(type, deliveryRejection, handler, rejectWhen));
    }

    // A route for a peer whose session joined.
    public void AddAuthenticated(
        RealtimeMessageType type,
        RealtimeRejection deliveryRejection,
        RealtimeMessageHandler handler,
        Func<RealtimePeerContext, bool>? rejectWhen = null)
    {
        authenticated.Add(Route(type, deliveryRejection, handler, rejectWhen));
    }

    private static RealtimeMessageRoute Route(
        RealtimeMessageType type,
        RealtimeRejection deliveryRejection,
        RealtimeMessageHandler handler,
        Func<RealtimePeerContext, bool>? rejectWhen)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!RealtimeProtocol.TryGetDescriptor(type, out var descriptor)
            || descriptor.Direction != RealtimeMessageDirection.ClientToServer)
        {
            throw new InvalidOperationException($"Realtime message type {type} is not a client message.");
        }

        return new RealtimeMessageRoute(
            type,
            descriptor.Channel,
            RealtimeDeliveryMethods.ToDeliveryMethod(descriptor.Delivery),
            deliveryRejection,
            handler)
        {
            RejectWhen = rejectWhen
        };
    }
}
