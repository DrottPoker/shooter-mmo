using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// The realtime route tables. A peer without a session may only join; a joined peer may
// move, interact and leave. Rejection codes and texts are part of the client protocol.
// Each route takes its channel and delivery from the generated protocol descriptor.
internal static class RealtimeMessageRoutes
{
    public static readonly RealtimeRejection JoinRequired = new(
        "join_required",
        "A single join request must complete before other messages are accepted.");

    public static readonly RealtimeRejection UnexpectedMessage = new(
        "unexpected_message",
        "The message is not valid for the current connection state.");

    public static IReadOnlyList<RealtimeMessageRoute> CreateUnauthenticated(
        RealtimeMessageHandler join)
    {
        return
        [
            Route(RealtimeMessageType.JoinRequest, JoinRequired, join) with
            {
                RejectWhen = static context => context.JoinStarted
            }
        ];
    }

    public static IReadOnlyList<RealtimeMessageRoute> CreateAuthenticated(
        RealtimeMessageHandler movement,
        RealtimeMessageHandler itemOperation,
        RealtimeMessageHandler corpseInteraction,
        RealtimeMessageHandler worldInteraction,
        RealtimeMessageHandler leave,
        RealtimeMessageHandler combat)
    {
        return
        [
            Route(
                RealtimeMessageType.MovementInputBatch,
                new RealtimeRejection(
                    "invalid_movement_delivery",
                    "Movement inputs require the sequenced movement channel."),
                movement),
            Route(
                RealtimeMessageType.ItemOperationIntent,
                new RealtimeRejection(
                    "invalid_item_operation_delivery",
                    "Item operation intents require the reliable ordered control channel."),
                itemOperation),
            Route(
                RealtimeMessageType.CorpseInteractionIntent,
                new RealtimeRejection(
                    "invalid_corpse_operation_delivery",
                    "Corpse interaction intents require the reliable ordered control channel."),
                corpseInteraction),
            Route(
                RealtimeMessageType.WorldInteractionIntent,
                new RealtimeRejection(
                    "invalid_world_interaction_delivery",
                    "World interaction intents require the reliable ordered control channel."),
                worldInteraction),
            Route(
                RealtimeMessageType.CombatIntent,
                new RealtimeRejection(
                    "invalid_combat_delivery",
                    "Combat intents require the reliable ordered control channel."),
                combat),
            Route(RealtimeMessageType.LeaveRequest, UnexpectedMessage, leave) with
            {
                RejectWhen = static context => context.LeaveStarted
            }
        ];
    }

    private static RealtimeMessageRoute Route(
        RealtimeMessageType type,
        RealtimeRejection deliveryRejection,
        RealtimeMessageHandler handler)
    {
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
            handler);
    }
}
