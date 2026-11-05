using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Quests;

namespace Mmorpg.SimulationWorker.WorldActors;

// Opens a quest NPC's quests and runs its quest actions through GameService. The open
// interaction has checked range and line of sight, and the NPC's actor definition is the quest
// NPC that GameService checks every action against.
public sealed class QuestsWorldActorCapabilityHandler(QuestOperationExecutor executor) : IWorldActorCapabilityHandler
{
    public RealtimeWorldActorCapabilityKind Kind => RealtimeWorldActorCapabilityKind.Quests;

    public async Task<WorldActorCapabilityDispatchResult> ExecuteAsync(
        WorldActorCapabilityOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var intent = context.Intent;
        if (intent.CapabilityAction == RealtimeCapabilityActionKind.None)
        {
            return new WorldActorCapabilityDispatchResult(
                true,
                string.Empty,
                context.Capability.DisplayName + " is open while this interaction stays open.");
        }

        var operationKind = intent.CapabilityAction switch
        {
            RealtimeCapabilityActionKind.AcceptQuest => QuestOperationKinds.Accept,
            RealtimeCapabilityActionKind.RegrantQuestItems => QuestOperationKinds.RegrantItems,
            RealtimeCapabilityActionKind.TalkToQuestNpc => QuestOperationKinds.TalkTo,
            RealtimeCapabilityActionKind.DeliverQuestItems => QuestOperationKinds.Deliver,
            RealtimeCapabilityActionKind.TurnInQuest => QuestOperationKinds.TurnIn,
            _ => null
        };
        if (operationKind is null)
        {
            return new WorldActorCapabilityDispatchResult(
                false,
                "quest_action_invalid",
                "The requested action is not a quest action.");
        }

        var outcome = await executor.ExecuteAsync(
            context.Session,
            new QuestOperation(
                intent.OperationId,
                operationKind,
                intent.QuestId,
                context.Actor.Definition.Id,
                Optional(intent.QuestObjectiveId),
                Optional(intent.RewardChoiceDefinitionId)),
            cancellationToken);
        return new WorldActorCapabilityDispatchResult(
            outcome.Succeeded,
            outcome.Code,
            outcome.Message,
            outcome.ItemStateRevision,
            outcome.ShouldDisconnect,
            ItemStateChange: outcome.ItemStateChange,
            QuestLogChange: outcome.QuestLogChange);
    }

    private static string? Optional(string value)
    {
        return value.Length == 0 ? null : value;
    }
}
