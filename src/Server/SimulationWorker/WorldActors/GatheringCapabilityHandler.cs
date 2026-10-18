using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.WorldActors;

// Validates a StartGathering action at a resource node. The authority service already
// checked the interaction, range, line of sight, and capability revision; this handler
// checks that the node still has a charge to gather. The realtime loop then runs the
// gathering cycles, and GameService checks the tool with every yield.
public sealed class GatheringWorldActorCapabilityHandler : IWorldActorCapabilityHandler
{
    public RealtimeWorldActorCapabilityKind Kind =>
        RealtimeWorldActorCapabilityKind.Gathering;

    public Task<WorldActorCapabilityDispatchResult> ExecuteAsync(
        WorldActorCapabilityOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Intent.CapabilityAction != RealtimeCapabilityActionKind.StartGathering)
        {
            return Task.FromResult(new WorldActorCapabilityDispatchResult(
                false,
                "gathering_action_invalid",
                "A resource node only accepts the start gathering action."));
        }

        if (context.Session.IsSyntheticBot)
        {
            return Task.FromResult(new WorldActorCapabilityDispatchResult(
                false,
                "gathering_unavailable",
                "Synthetic development sessions cannot gather durable items."));
        }

        var node = context.Actor;
        if (node.ResourceProfile is null || node.RemainingResourceCharges <= 0)
        {
            return Task.FromResult(new WorldActorCapabilityDispatchResult(
                false,
                GatheringStopCodes.Depleted,
                "The resource node is depleted."));
        }

        return Task.FromResult(new WorldActorCapabilityDispatchResult(
            true,
            string.Empty,
            context.Capability.DisplayName + ".",
            GatheringNode: node));
    }
}

// Stable codes for why gathering stopped, sent in GatheringState Stopped.
public static class GatheringStopCodes
{
    public const string Depleted = "resource_node_depleted";
    public const string Interrupted = "gathering_interrupted";
    public const string InteractionClosed = "world_interaction_closed";
    public const string Failed = "gathering_failed";
}
