using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Text;

namespace Mmorpg.SimulationWorker.WorldActors;

// The codes of the texts that the client shows for a committed world interaction. The worker
// sends a code and named values, never a sentence: the client owns the text.
public static class WorldInteractionNoticeCodes
{
    public const string InteractionOpened = "interaction_opened";
    public const string GatheringStarted = "gathering_started";
    public const string InsuranceApplied = "insurance_applied";
    public const string InsuranceRemoved = "insurance_removed";
    public const string VendorPurchased = "vendor_purchased";
    public const string VendorSold = "vendor_sold";
    public const string QuestAccepted = "quest_accepted";
    public const string QuestAbandoned = "quest_abandoned";
    public const string QuestItemsRegranted = "quest_items_regranted";
    public const string QuestObjectiveCompleted = "quest_objective_completed";
    public const string QuestItemsDelivered = "quest_items_delivered";
    public const string QuestCompleted = "quest_completed";
    public const string QuestKillCounted = "quest_kill_counted";
}

public static class WorldInteractionNotices
{
    public const string CapabilityArgument = "capability";

    // A capability that only opens: its service stays available while the interaction is open.
    public static WorldActorCapabilityDispatchResult Opened(WorldActorCapabilityOperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new WorldActorCapabilityDispatchResult(
            true,
            WorldInteractionNoticeCodes.InteractionOpened,
            string.Empty,
            Arguments: [Capability(context)]);
    }

    // The name of the capability, which the client finds in its text tables.
    public static RealtimeMessageArgument Capability(WorldActorCapabilityOperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RealtimeMessageArgument.TextKey(
            CapabilityArgument,
            TextKeys.ActorCapabilityName(context.Actor.Definition.Id, context.Capability.Id));
    }
}
