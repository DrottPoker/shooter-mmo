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
    public const string CreditKill = "credit_quest_kill";

    // The development quest tools of the Admin menu, which only a Development GameService runs.
    public const string AdminAccept = "admin_accept_quest";
    public const string AdminCompleteObjective = "admin_complete_quest_objective";
    public const string AdminCompleteStep = "admin_complete_quest_step";
    public const string AdminMakeReady = "admin_make_quest_ready";
    public const string AdminReset = "admin_reset_quest";
    public const string AdminResetChain = "admin_reset_quest_chain";
    public const string AdminResetAll = "admin_reset_quests";
}

// One quest action of a live character. QuestNpcActorId is the actor definition of the NPC whose
// quests capability the open interaction validated, or null outside an interaction. A kill credit
// names the credited Mob's actor definition and tags instead of a quest, and since no client
// intent caused it, its quest log change names no operation.
public sealed record QuestOperation(
    Guid OperationId,
    string OperationKind,
    string? QuestId,
    string? QuestNpcActorId = null,
    string? ObjectiveId = null,
    string? RewardChoiceDefinitionId = null,
    string? MobActorId = null,
    IReadOnlyList<string>? MobTags = null,
    bool ClientIntent = true)
{
    public Guid ReportedOperationId => ClientIntent ? OperationId : Guid.Empty;
}

// What a quest action committed for the client: the item-state revision after it, the item and
// quest log changes to patch with, or why it failed and whether the session ends. An uncertain
// failure may have committed or never reached GameService, so a system operation such as a kill
// credit asks again later with the same operation id.
public sealed record QuestOperationOutcome(
    bool Succeeded,
    string Code,
    string Message,
    long ItemStateRevision = 0,
    bool ShouldDisconnect = false,
    RealtimeOwnerItemStateChanged? ItemStateChange = null,
    RealtimeQuestLogChanged? QuestLogChange = null,
    bool Uncertain = false)
{
    public static QuestOperationOutcome Rejected(string code, string message, bool shouldDisconnect = false)
    {
        return new QuestOperationOutcome(false, code, message, ShouldDisconnect: shouldDisconnect);
    }
}

// Runs one quest action of a live character through GameService and applies what it committed:
// the character's item state and its quest log. An answer that may have been lost after the
// commit, such as a timeout, asks once more with the same operation id, which replays a committed
// result. When that is uncertain too, the worker reads the quest log again and the client reloads
// its own; a log that cannot be read is read before the next quest action. It follows the quest
// log changes of gathering yields the same way. It runs off the realtime loop and touches only
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

        // The log the action builds on must hold every committed change.
        long? reloadFrom = null;
        if (questLogs.IsStale(session.CharacterId, session.SimulationSessionId))
        {
            reloadFrom = await ReadAgainAsync(session, cancellationToken);
            if (reloadFrom is null)
            {
                return QuestOperationOutcome.Rejected(
                    "quest_log_unavailable",
                    "The quest log could not be read from GameService. Try again shortly.") with
                {
                    Uncertain = true
                };
            }
        }

        var request = new SimulationQuestOperationRequest(
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
            operation.RewardChoiceDefinitionId,
            operation.MobActorId,
            operation.MobTags);
        var result = await gameServiceClient.MutateSimulationQuestsAsync(
            session.SimulationSessionId,
            request,
            cancellationToken);
        if (IsUncertain(result))
        {
            result = await gameServiceClient.MutateSimulationQuestsAsync(
                session.SimulationSessionId,
                request,
                cancellationToken);
        }

        if (!result.Succeeded)
        {
            var uncertain = IsUncertain(result);
            if (uncertain)
            {
                questLogs.MarkStale(session.CharacterId, session.SimulationSessionId);
                reloadFrom ??= CurrentRevision(session);
                await ReadAgainAsync(session, cancellationToken);
            }

            return QuestOperationOutcome.Rejected(
                result.Error!.Code,
                result.Error.Message,
                GameServiceOutcome.EndsItemSession(result.Error.Code)) with
            {
                QuestLogChange = ReloadNotice(session, operation.ReportedOperationId, reloadFrom),
                Uncertain = uncertain
            };
        }

        // An action that moved no item, such as a talk or a kill credit, leaves the item state as
        // the worker holds it, also for a session whose player already left.
        var transaction = result.Value!;
        var itemStateRevision = transaction.CharacterState!.ItemStateRevision;
        RealtimeOwnerItemStateChanged? itemChange = null;
        if (transaction.CharacterRevisions.Count > 0)
        {
            if (!CommittedCarryApplier.TryGetCommittedState(transaction, out var committed))
            {
                return Diverged();
            }

            var applied = carryApplier.Apply(session, committed, out var current);
            if (CommittedCarryApplier.IsDiverged(applied))
            {
                return Diverged();
            }

            itemStateRevision = current.ItemStateRevision;
            itemChange = applied == ItemStateApplyResult.Applied
                ? OwnerItemStateChangeMapper.TryCreate(operation.ReportedOperationId, session.CharacterId, transaction)
                : null;
        }

        var questChange = transaction.QuestLog is { } log
            && questLogs.TryApply(session.CharacterId, session.SimulationSessionId, log, out var previousRevision)
            && reloadFrom is null
                ? QuestLogChangeMapper.Create(operation.ReportedOperationId, previousRevision, log)
                : ReloadNotice(session, operation.ReportedOperationId, reloadFrom);
        return new QuestOperationOutcome(
            true,
            string.Empty,
            SuccessMessage(operation.OperationKind),
            itemStateRevision,
            ItemStateChange: itemChange,
            QuestLogChange: questChange);
    }

    // A committed gathering yield, with the quest log change it made when it counted for a quest.
    // The change patches the log and the client like a quest action; a stale log, which may miss
    // an earlier change, is read again instead, and the client reloads its own.
    public async Task<RealtimeQuestLogChanged?> FollowAsync(
        ActiveSimulationSession session,
        SimulationQuestLogChange? change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (change is not null
            && questLogs.TryApply(session.CharacterId, session.SimulationSessionId, change, out var previousRevision))
        {
            return QuestLogChangeMapper.Create(Guid.Empty, previousRevision, change);
        }

        if (!questLogs.IsStale(session.CharacterId, session.SimulationSessionId))
        {
            return null;
        }

        var reloadFrom = await ReadAgainAsync(session, cancellationToken);
        return ReloadNotice(session, Guid.Empty, reloadFrom);
    }

    // A gathering yield that may have counted for a quest before its answer was lost leaves the
    // log stale until GameService's replaces it, and the client reloads its own once it did.
    public async Task<RealtimeQuestLogChanged?> RecoverAsync(
        ActiveSimulationSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!questLogs.TryGet(session.CharacterId, session.SimulationSessionId, out var log))
        {
            return null;
        }

        questLogs.MarkStale(session.CharacterId, session.SimulationSessionId);
        await ReadAgainAsync(session, cancellationToken);
        return ReloadNotice(session, Guid.Empty, log!.Revision);
    }

    // A lost answer, or a failure after the commit such as a read of the committed state.
    private static bool IsUncertain(GameServiceResult<SimulationItemTransactionResponse> result)
    {
        return !result.Succeeded && GameServiceOutcome.MayHaveCommitted(result.Error!.Code, result.StatusCode);
    }

    // Replaces a stale log with GameService's and returns the revision it replaced, or null when
    // GameService could not be read and the log stays stale.
    private async Task<long?> ReadAgainAsync(ActiveSimulationSession session, CancellationToken cancellationToken)
    {
        var result = await gameServiceClient.ReadSimulationQuestLogAsync(
            session.SimulationSessionId,
            new SimulationQuestLogRequest(
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken),
            cancellationToken);
        return result.Succeeded
            && questLogs.TryReplace(
                session.CharacterId,
                session.SimulationSessionId,
                CharacterQuestLog.From(result.Value!),
                out var replaced)
                ? replaced
                : null;
    }

    private long CurrentRevision(ActiveSimulationSession session)
    {
        return questLogs.TryGet(session.CharacterId, session.SimulationSessionId, out var log) ? log!.Revision : 0;
    }

    // Tells the client to read its quest log again once the worker's moved past the revision it
    // last sent, because the client may have missed what came between.
    private RealtimeQuestLogChanged? ReloadNotice(ActiveSimulationSession session, Guid operationId, long? reloadFrom)
    {
        var current = CurrentRevision(session);
        return reloadFrom is { } previous && current > previous
            ? new RealtimeQuestLogChanged(operationId, previous, current, true, [])
            : null;
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
            QuestOperationKinds.CreditKill => "Kill counted.",
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
