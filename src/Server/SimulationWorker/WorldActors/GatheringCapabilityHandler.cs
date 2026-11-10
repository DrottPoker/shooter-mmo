using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Text;

namespace Mmorpg.SimulationWorker.WorldActors;

// Validates a StartGathering action at a resource node. The authority service already
// checked the interaction, range, line of sight, and capability revision; this handler
// checks that the Tool slot holds an item of the node's tool kind, as GameService last
// committed the equipment, and that the node still has a charge to gather. The realtime
// loop then runs the gathering cycles, and GameService checks the tool again with every
// yield, so a tool taken off later stops gathering.
public sealed class GatheringWorldActorCapabilityHandler : IWorldActorCapabilityHandler
{
    private readonly CharacterItemStateStore itemStates;
    private readonly Dictionary<string, string[]> toolKindsByDefinition;

    public GatheringWorldActorCapabilityHandler(
        CharacterItemStateStore itemStates,
        ItemCatalogRuntimeDocument itemCatalog)
    {
        ArgumentNullException.ThrowIfNull(itemStates);
        ArgumentNullException.ThrowIfNull(itemCatalog);
        this.itemStates = itemStates;
        toolKindsByDefinition = (itemCatalog.Definitions ?? [])
            .ToDictionary(
                definition => definition.Id,
                definition => definition.ToolKinds ?? [],
                StringComparer.Ordinal);
    }

    public RealtimeWorldActorCapabilityKind Kind =>
        RealtimeWorldActorCapabilityKind.Gathering;

    public Task<WorldActorCapabilityDispatchResult> ExecuteAsync(
        WorldActorCapabilityOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(Execute(context));
    }

    private WorldActorCapabilityDispatchResult Execute(WorldActorCapabilityOperationContext context)
    {
        if (context.Intent.CapabilityAction != RealtimeCapabilityActionKind.StartGathering)
        {
            return Rejected(
                "gathering_action_invalid",
                "A resource node only accepts the start gathering action.");
        }

        if (context.Session.IsSyntheticBot)
        {
            return Rejected(
                "gathering_unavailable",
                "Synthetic development sessions cannot gather durable items.");
        }

        var node = context.Actor;
        if (node.ResourceProfile is null || node.RemainingResourceCharges <= 0)
        {
            return Rejected(GatheringStopCodes.Depleted, "The resource node is depleted.");
        }

        if (!itemStates.TryGetItemState(
                context.Session.CharacterId,
                context.Session.SimulationSessionId,
                out var itemState))
        {
            return Rejected(
                "gathering_unavailable",
                "The character's equipment is not loaded yet.");
        }

        var requiredToolKind = node.ResourceProfile.RequiredToolKind;
        if (!HoldsToolKind(itemState!, requiredToolKind))
        {
            // The same code GameService gives when the tool is gone at a yield.
            return new WorldActorCapabilityDispatchResult(
                false,
                GatheringStopCodes.ToolRequired,
                "A tool of the kind '" + requiredToolKind + "' is required in the Tool slot.",
                Arguments: GatheringMessageArguments.For(GatheringStopCodes.ToolRequired, requiredToolKind));
        }

        return new WorldActorCapabilityDispatchResult(
            true,
            WorldInteractionNoticeCodes.GatheringStarted,
            string.Empty,
            GatheringNode: node,
            Arguments: [WorldInteractionNotices.Capability(context)]);
    }

    private bool HoldsToolKind(CharacterItemState itemState, string toolKind)
    {
        var tool = itemState.FindEquipped(ItemEquipmentSlotIds.Tool);
        return tool is not null
            && toolKindsByDefinition.TryGetValue(tool.DefinitionId, out var kinds)
            && kinds.Contains(toolKind, StringComparer.Ordinal);
    }

    private static WorldActorCapabilityDispatchResult Rejected(string code, string message)
    {
        return new WorldActorCapabilityDispatchResult(false, code, message);
    }
}

// The named values of the client's texts for gathering codes: the text for a missing tool
// names the kind of tool that the node needs.
public static class GatheringMessageArguments
{
    public const string ToolKind = "toolKind";

    public static RealtimeMessageArgument[] For(string code, string? requiredToolKind)
    {
        return string.Equals(code, GatheringStopCodes.ToolRequired, StringComparison.Ordinal)
            && TextKeys.IsValidId(requiredToolKind)
                ? [RealtimeMessageArgument.TextKey(ToolKind, TextKeys.ToolKindName(requiredToolKind))]
                : RealtimeMessageArgument.None;
    }
}

// Stable codes for why gathering stopped or could not start, sent in GatheringState
// Stopped or the interaction result.
public static class GatheringStopCodes
{
    public const string Depleted = "resource_node_depleted";
    public const string Interrupted = "gathering_interrupted";
    public const string InteractionClosed = "world_interaction_closed";
    public const string Failed = "gathering_failed";
    public const string ToolRequired = "gathering_tool_required";
}
