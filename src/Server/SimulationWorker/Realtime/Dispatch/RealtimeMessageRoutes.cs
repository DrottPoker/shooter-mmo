using LiteNetLib;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// The realtime route tables. A peer without a session may only join; a joined peer may
// move, interact and leave. Rejection codes and texts are part of the client protocol.
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
            new RealtimeMessageRoute(
                RealtimeMessageType.JoinRequest,
                RealtimeProtocol.ControlChannel,
                DeliveryMethod.ReliableOrdered,
                JoinRequired,
                join)
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
        RealtimeMessageHandler leave)
    {
        return
        [
            new RealtimeMessageRoute(
                RealtimeMessageType.MovementInputBatch,
                RealtimeProtocol.MovementInputChannel,
                DeliveryMethod.Sequenced,
                new RealtimeRejection(
                    "invalid_movement_delivery",
                    "Movement inputs require the sequenced movement channel."),
                movement),
            new RealtimeMessageRoute(
                RealtimeMessageType.ItemOperationIntent,
                RealtimeProtocol.ControlChannel,
                DeliveryMethod.ReliableOrdered,
                new RealtimeRejection(
                    "invalid_item_operation_delivery",
                    "Item operation intents require the reliable ordered control channel."),
                itemOperation),
            new RealtimeMessageRoute(
                RealtimeMessageType.CorpseInteractionIntent,
                RealtimeProtocol.ControlChannel,
                DeliveryMethod.ReliableOrdered,
                new RealtimeRejection(
                    "invalid_corpse_operation_delivery",
                    "Corpse interaction intents require the reliable ordered control channel."),
                corpseInteraction),
            new RealtimeMessageRoute(
                RealtimeMessageType.WorldInteractionIntent,
                RealtimeProtocol.ControlChannel,
                DeliveryMethod.ReliableOrdered,
                new RealtimeRejection(
                    "invalid_world_interaction_delivery",
                    "World interaction intents require the reliable ordered control channel."),
                worldInteraction),
            new RealtimeMessageRoute(
                RealtimeMessageType.LeaveRequest,
                RealtimeProtocol.ControlChannel,
                DeliveryMethod.ReliableOrdered,
                UnexpectedMessage,
                leave)
            {
                RejectWhen = static context => context.LeaveStarted
            }
        ];
    }
}
