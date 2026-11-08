using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

internal sealed record QuestIntentCompleted(
    RealtimePeerContext PeerContext,
    ActiveSimulationSession Session,
    RealtimeQuestIntent Intent,
    QuestOperationOutcome Outcome)
    : RealtimeOperationResult(PeerContext);

// Quest actions outside any interaction, such as abandoning a quest from the quest log or a
// development tool of the Admin menu, which only a worker with the admin tools carries out. They
// queue behind the peer's other authority work, so the worker applies every committed quest
// change in order. The changed items and quest log go to the client before the result.
// Loop-confined; the GameService call runs off the loop.
internal sealed class QuestIntentHandler(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    SimulationEntityRegistry entityRegistry,
    CarryStateSynchronizer carrySync,
    RealtimeSessionTeardown teardown,
    AuthorityOperationScheduler authorityScheduler,
    RealtimeOperationTracker operationTracker,
    RealtimeCompletionQueue completions,
    QuestOperationExecutor? executor,
    DevelopmentAdminOptions adminOptions,
    ILogger logger,
    ICombatOperationGate? combatGate = null) : IRealtimeCompletionHandler
{
    public void HandleIntent(in RealtimeInboundMessage message)
    {
        var peer = message.Peer;
        var context = message.Context;
        if (!peers.TryGetBoundPlayer(context, out var entity))
        {
            sender.RejectProtocol(peer, context, "quest_intent_unavailable", "Quests are not active for this peer.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeQuestIntent(message.Packet, out var intent, out var error))
        {
            sender.RejectProtocol(peer, context, "invalid_quest_intent", error);
            return;
        }

        // Quest intents share the peer's interaction rate limit with the NPC quest actions.
        if (!context.WorldInteractionIntentQuota.TryConsume())
        {
            SendRejection(
                peer,
                intent,
                WorldInteractionIntentLimiter.RejectionCode,
                "Quest intents exceeded the per-peer rate limit.");
            return;
        }

        if (executor is null || entity.Session.IsSyntheticBot)
        {
            SendRejection(peer, intent, "quests_unavailable", "This worker runs no quests for the character.");
            return;
        }

        if (intent.Kind != RealtimeQuestIntentKind.Abandon && !adminOptions.Enabled)
        {
            SendRejection(peer, intent, "admin_tools_disabled", "Only a Development worker allows the admin tools.");
            return;
        }

        if (combatGate is not null && combatGate.IsCharacterDead(context))
        {
            SendRejection(peer, intent, RealtimeCombatErrorCodes.CharacterDead, "The character is dead.");
            return;
        }

        if (context.AuthorityOperationQueue.Count >= RealtimePeerContext.MaximumQueuedAuthorityOperations)
        {
            SendRejection(peer, intent, "quest_intent_queue_full", "Too many authority operations are waiting for this peer.");
            return;
        }

        // Spent arrows commit first, so the quest action sees the arrow stack GameService has.
        combatGate?.BeforeClientOperation(context);
        authorityScheduler.Enqueue(context, new QueuedQuestIntent(this, intent));
    }

    public void Complete(RealtimeOperationResult completion)
    {
        if (completion is QuestIntentCompleted completed)
        {
            FinalizeIntent(completed);
        }
    }

    private void Start(RealtimePeerContext context, ActiveSimulationSession session, RealtimeQuestIntent intent)
    {
        combatGate?.ClientOperationStarting(context, false);
        operationTracker.Track(RunAsync(context, session, intent));
    }

    private async Task RunAsync(RealtimePeerContext context, ActiveSimulationSession session, RealtimeQuestIntent intent)
    {
        QuestOperationOutcome outcome;
        try
        {
            outcome = await executor!.ExecuteAsync(session, OperationOf(intent), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while running a quest intent for simulation session {SimulationSessionId}.",
                session.SimulationSessionId);
            outcome = QuestOperationOutcome.Rejected(
                "quest_intent_failed",
                "SimulationWorker could not complete the quest action.");
        }

        completions.Post(this, new QuestIntentCompleted(context, session, intent, outcome));
    }

    private static QuestOperation OperationOf(RealtimeQuestIntent intent)
    {
        var kind = intent.Kind switch
        {
            RealtimeQuestIntentKind.Abandon => QuestOperationKinds.Abandon,
            RealtimeQuestIntentKind.AdminAccept => QuestOperationKinds.AdminAccept,
            RealtimeQuestIntentKind.AdminCompleteObjective => QuestOperationKinds.AdminCompleteObjective,
            RealtimeQuestIntentKind.AdminCompleteStep => QuestOperationKinds.AdminCompleteStep,
            RealtimeQuestIntentKind.AdminMakeReady => QuestOperationKinds.AdminMakeReady,
            RealtimeQuestIntentKind.AdminReset => QuestOperationKinds.AdminReset,
            RealtimeQuestIntentKind.AdminResetChain => QuestOperationKinds.AdminResetChain,
            RealtimeQuestIntentKind.AdminResetAll => QuestOperationKinds.AdminResetAll,
            _ => throw new InvalidOperationException($"Quest intent kind {intent.Kind} has no operation.")
        };
        return new QuestOperation(
            intent.OperationId,
            kind,
            string.IsNullOrEmpty(intent.QuestId) ? null : intent.QuestId,
            ObjectiveId: string.IsNullOrEmpty(intent.ObjectiveId) ? null : intent.ObjectiveId);
    }

    private void FinalizeIntent(QuestIntentCompleted completed)
    {
        authorityScheduler.Release(completed.Context);
        combatGate?.ClientOperationFinished(completed.Context);
        if (!peers.TryGetCurrentPeer(completed.Context, out var peer)
            || completed.Context.Session is null
            || completed.Context.Session.SimulationSessionId != completed.Session.SimulationSessionId
            || !string.Equals(
                completed.Context.Session.SimulationSessionToken,
                completed.Session.SimulationSessionToken,
                StringComparison.Ordinal))
        {
            return;
        }

        var outcome = completed.Outcome;
        if (outcome.ShouldDisconnect)
        {
            SendResult(peer, completed.Intent, outcome);
            teardown.TerminateForAuthorityFailure(
                peer,
                completed.Context,
                completed.Session,
                outcome.Code,
                outcome.Message);
            return;
        }

        if (outcome.ItemStateChange is not null)
        {
            sender.SendControl(peer, RealtimeProtocol.EncodeOwnerItemStateChanged(outcome.ItemStateChange));
        }

        if (outcome.QuestLogChange is not null)
        {
            sender.SendControl(peer, RealtimeProtocol.EncodeQuestLogChanged(outcome.QuestLogChange));
        }

        if (entityRegistry.TryGetPlayerByCharacterId(completed.Session.CharacterId, out var entity))
        {
            carrySync.ApplyLatest(entity!);
        }

        SendResult(peer, completed.Intent, outcome);
        authorityScheduler.StartNext(completed.Context);
    }

    private void Cancel(RealtimePeerContext context, RealtimeQuestIntent intent, string code, string message)
    {
        if (peers.TryGetCurrentPeer(context, out var peer))
        {
            SendRejection(peer, intent, code, message);
        }
    }

    private void SendRejection(IRealtimePeerConnection peer, RealtimeQuestIntent intent, string code, string message)
    {
        SendResult(peer, intent, QuestOperationOutcome.Rejected(code, message));
    }

    private void SendResult(IRealtimePeerConnection peer, RealtimeQuestIntent intent, QuestOperationOutcome outcome)
    {
        sender.SendControl(peer, RealtimeProtocol.EncodeQuestIntentResult(new RealtimeQuestIntentResult(
            intent.OperationId,
            intent.Kind,
            outcome.Succeeded,
            outcome.ItemStateRevision,
            outcome.Succeeded ? null! : new RealtimeError(outcome.Code, outcome.Message))));
    }

    private sealed record QueuedQuestIntent(QuestIntentHandler Owner, RealtimeQuestIntent Intent)
        : IRealtimeAuthorityOperation
    {
        public void Start(RealtimePeerContext context, PlayerSimulationEntity entity, PlayerMovementState state)
        {
            Owner.Start(context, context.Session!, Intent);
        }

        public void Cancel(RealtimePeerContext context, string code, string message)
        {
            Owner.Cancel(context, Intent, code, message);
        }
    }
}
