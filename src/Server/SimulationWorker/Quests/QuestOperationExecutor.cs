using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Quests;

// The quest route's operation kinds, as GameService names them.
public static class QuestOperationKinds
{
    public const string Accept = "accept_quest";
    public const string Abandon = "abandon_quest";
    public const string RegrantItems = "regrant_quest_items";
    public const string TalkTo = "talk_to_quest_npc";
    public const string Deliver = "deliver_quest_items";
    public const string TurnIn = "turn_in_quest";
}

// One quest action of a live character. QuestNpcActorId is the actor definition of the NPC whose
// quests capability the open interaction validated, or null outside an interaction.
public sealed record QuestOperation(
    Guid OperationId,
    string OperationKind,
    string QuestId,
    string? QuestNpcActorId = null,
    string? ObjectiveId = null,
    string? RewardChoiceDefinitionId = null);

// What a quest action committed for the client: the item-state revision after it, the item and
// quest log changes to patch with, or why it failed and whether the session ends.
public sealed record QuestOperationOutcome(
    bool Succeeded,
    string Code,
    string Message,
    long ItemStateRevision = 0,
    bool ShouldDisconnect = false,
    RealtimeOwnerItemStateChanged? ItemStateChange = null,
    RealtimeQuestLogChanged? QuestLogChange = null)
{
    public static QuestOperationOutcome Rejected(string code, string message, bool shouldDisconnect = false)
    {
        return new QuestOperationOutcome(false, code, message, ShouldDisconnect: shouldDisconnect);
    }
}

// Runs one quest action of a live character through GameService and applies what it committed:
// the character's item state and its quest log. It runs off the realtime loop and touches only
// the synchronized stores.
public sealed class QuestOperationExecutor(
    GameServiceClient gameServiceClient,
    CharacterItemStateStore itemStateStore,
    ActiveSimulationSessionStore sessionStore,
    CharacterQuestLogStore questLogs)
{
    private readonly CommittedCarryApplier carryApplier = new(itemStateStore, sessionStore);

    public async Task<QuestOperationOutcome> ExecuteAsync(
        ActiveSimulationSession session,
        QuestOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(operation);
        if (session.IsSyntheticBot)
        {
            return QuestOperationOutcome.Rejected(
                "quests_unavailable",
                "Synthetic development sessions never take part in quests.");
        }

        var result = await gameServiceClient.MutateSimulationQuestsAsync(
            session.SimulationSessionId,
            new SimulationQuestOperationRequest(
                operation.OperationId,
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                operation.OperationKind,
                operation.QuestId,
                operation.QuestNpcActorId,
                operation.ObjectiveId,
                operation.RewardChoiceDefinitionId),
            cancellationToken);
        if (!result.Succeeded)
        {
            return QuestOperationOutcome.Rejected(
                result.Error!.Code,
                result.Error.Message,
                GameServiceOutcome.EndsItemSession(result.Error.Code));
        }

        var transaction = result.Value!;
        if (!CommittedCarryApplier.TryGetCommittedState(transaction, out var committed))
        {
            return Diverged();
        }

        var applied = carryApplier.Apply(session, committed, out var current);
        if (CommittedCarryApplier.IsDiverged(applied))
        {
            return Diverged();
        }

        var itemChange = applied == ItemStateApplyResult.Applied
            ? OwnerItemStateChangeMapper.TryCreate(operation.OperationId, session.CharacterId, transaction)
            : null;
        var questChange = transaction.QuestLog is { } log
            && questLogs.TryApply(session.CharacterId, session.SimulationSessionId, log, out var previousRevision)
                ? QuestLogChangeMapper.Create(operation.OperationId, previousRevision, log)
                : null;
        return new QuestOperationOutcome(
            true,
            string.Empty,
            SuccessMessage(operation.OperationKind),
            current.ItemStateRevision,
            ItemStateChange: itemChange,
            QuestLogChange: questChange);
    }

    private static QuestOperationOutcome Diverged()
    {
        return QuestOperationOutcome.Rejected(
            "item_state_diverged",
            "The committed quest action diverged from the active simulation state.",
            shouldDisconnect: true);
    }

    private static string SuccessMessage(string operationKind)
    {
        return operationKind switch
        {
            QuestOperationKinds.Accept => "Quest accepted.",
            QuestOperationKinds.Abandon => "Quest abandoned.",
            QuestOperationKinds.RegrantItems => "You have the quest's items again.",
            QuestOperationKinds.TalkTo => "Objective complete.",
            QuestOperationKinds.Deliver => "Items handed over.",
            QuestOperationKinds.TurnIn => "Quest completed.",
            _ => string.Empty
        };
    }
}

// Turns a committed quest log change into the message the client patches its quest log with. A
// change that one packet cannot list asks the client to read its quest log again.
internal static class QuestLogChangeMapper
{
    public static RealtimeQuestLogChanged Create(
        Guid operationId,
        long previousRevision,
        SimulationQuestLogChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var quests = change.Quests
            .Select(quest => new RealtimeQuestChange(
                quest.QuestId,
                quest.StepId ?? string.Empty,
                quest.Objectives
                    .Select(objective => new RealtimeQuestObjectiveProgress(objective.ObjectiveId, objective.Progress))
                    .ToArray(),
                quest.CompletionCount))
            .ToArray();
        var listed = new RealtimeQuestLogChanged(operationId, previousRevision, change.Revision, false, quests);
        return quests.Length > 0 && RealtimeProtocol.CanEncodeQuestLogChanged(listed)
            ? listed
            : new RealtimeQuestLogChanged(operationId, previousRevision, change.Revision, true, []);
    }
}
