using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// Accepts item operation intents, queues them behind the peer's other authority work
// and finalizes their results. Loop-confined; GameService calls run in
// ItemAuthorityOperations.
internal sealed class ItemOperationHandler(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    ActiveSimulationSessionStore sessionStore,
    CharacterItemStateStore itemStateStore,
    CarryStateSynchronizer carrySync,
    RealtimeSessionTeardown teardown,
    OperationCompletionTargets targets,
    AuthorityOperationScheduler authorityScheduler,
    RealtimeOperationTracker operationTracker,
    ItemInteractionAccessService accessService,
    ItemAuthorityOperations authority,
    ICombatOperationGate? combatGate = null) : IRealtimeCompletionHandler
{
    private static readonly RealtimeRejection DeliveryRejection = new(
        "invalid_item_operation_delivery",
        "Item operation intents require the reliable ordered control channel.");

    private readonly CommittedCarryApplier carryApplier = new(itemStateStore, sessionStore);

    public static void AddRoute(RealtimeMessageRoutes routes, RealtimeMessageHandler handler)
    {
        routes.AddAuthenticated(RealtimeMessageType.ItemOperationIntent, DeliveryRejection, handler);
    }

    public void HandleIntent(in RealtimeInboundMessage message)
    {
        var peer = message.Peer;
        var context = message.Context;
        var packet = message.Packet;
        if (!peers.TryGetBoundPlayer(context, out var playerEntity))
        {
            sender.RejectProtocol(
                peer,
                context,
                "item_operation_unavailable",
                "Item interaction is not active for this peer.");
            return;
        }

        if (!itemStateStore.TryGet(
                playerEntity.Session.CharacterId,
                playerEntity.Session.SimulationSessionId,
                out _))
        {
            sender.RejectProtocol(
                peer,
                context,
                "carry_state_missing",
                "Authoritative carry state is unavailable for item interaction.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeItemOperationIntent(
                packet,
                out var intent,
                out var itemError))
        {
            sender.RejectProtocol(
                peer,
                context,
                "invalid_item_operation",
                itemError);
            return;
        }

        if (combatGate is not null && combatGate.IsCharacterDead(context))
        {
            SendItemOperationRejection(
                peer,
                playerEntity.Session,
                intent,
                RealtimeCombatErrorCodes.CharacterDead,
                "The character is dead.");
            return;
        }

        if (context.AuthorityOperationQueue.Count
            >= RealtimePeerContext.MaximumQueuedAuthorityOperations)
        {
            SendItemOperationRejection(
                peer,
                playerEntity.Session,
                intent,
                "item_operation_queue_full",
                "Too many item operations are waiting for authority.");
            return;
        }

        // Spent arrows commit first, so the operation sees the arrow stack GameService has.
        combatGate?.BeforeClientOperation(context);
        authorityScheduler.Enqueue(context, new QueuedItemOperation(this, intent));
    }

    private void Start(
        RealtimePeerContext context,
        ActiveSimulationSession session,
        RealtimeItemOperationIntent intent)
    {
        combatGate?.ClientOperationStarting(context, MayChangeEquipment(session, intent));
        // Access is evaluated on the loop when the operation starts, while the interaction
        // that grants it is still known to be open.
        operationTracker.Track(authority.RunAsync(
            this,
            context,
            session,
            intent,
            accessService.Evaluate(context.PeerId, session),
            combatGate?.RebaseExpectedCharacterRevision(context, intent.ExpectedCharacterRevision)
                ?? intent.ExpectedCharacterRevision));
    }

    // An operation that may change which item sits in an equipment slot holds attacks
    // until it finishes. Splitting or topping up the equipped stack keeps its item.
    private bool MayChangeEquipment(ActiveSimulationSession session, RealtimeItemOperationIntent intent)
    {
        switch (intent.OperationKind)
        {
            case RealtimeItemOperationKind.Equip:
            case RealtimeItemOperationKind.Unequip:
                return true;
            case RealtimeItemOperationKind.Relocate:
            case RealtimeItemOperationKind.Destroy:
            case RealtimeItemOperationKind.MergeStacks:
            case RealtimeItemOperationKind.SwapContainerItems:
                return itemStateStore.TryGetItemState(session.CharacterId, session.SimulationSessionId, out var state)
                    && state!.Equipment.Any(item =>
                        item.ItemInstanceId == intent.ItemInstanceId
                        || (intent.OperationKind == RealtimeItemOperationKind.SwapContainerItems
                            && item.ItemInstanceId == intent.TargetItemInstanceId));
            default:
                return false;
        }
    }

    public void Complete(RealtimeOperationResult completion)
    {
        if (completion is ItemOperationCompleted completed)
        {
            FinalizeOperation(completed);
        }
    }

    // The operation committed at GameService whether or not its peer is still there, so what it
    // committed reaches the character while the worker holds its session: the item state, which
    // a lingering body's spent arrows and death build on, the carry state, and the look. The
    // result goes only to the peer that sent the operation, the item change to the peer that
    // plays the character now.
    private void FinalizeOperation(ItemOperationCompleted completed)
    {
        combatGate?.ClientOperationFinished(completed.Session);
        authorityScheduler.Release(completed.Context);
        if (!targets.TryResolve(completed.Context, completed.Session, out var target))
        {
            return;
        }

        var session = target.Session;
        if (!itemStateStore.TryGet(session.CharacterId, session.SimulationSessionId, out _))
        {
            teardown.EndForAuthorityFailure(
                target,
                "carry_state_missing",
                "Authoritative carry state disappeared during the item operation.");
            return;
        }

        if (completed.Result.ShouldDisconnect)
        {
            if (target.Requester is { } failedRequester)
            {
                SendItemOperationResult(
                    failedRequester,
                    session,
                    completed.Result,
                    requiresInventoryRefresh: true);
            }

            teardown.EndForAuthorityFailure(
                target,
                completed.Result.Error!.Code,
                completed.Result.Error.Message);
            return;
        }

        var requiresRefresh = completed.Result.RequiresInventoryRefresh;
        if (completed.Result.Succeeded)
        {
            // Matches the corpse path: an unexpected revision set ends the session instead
            // of failing the completion with internal_error.
            if (!CommittedCarryApplier.TryGetCommittedState(
                    completed.Result.Transaction!,
                    out var committedState))
            {
                teardown.EndForAuthorityFailure(
                    target,
                    "item_state_diverged",
                    "The item transaction returned no committed character state.");
                return;
            }

            var carryResult = carryApplier.Apply(session, committedState, out _);
            if (CommittedCarryApplier.IsDiverged(carryResult))
            {
                teardown.EndForAuthorityFailure(
                    target,
                    "item_state_diverged",
                    "Committed item and carry revisions diverged from the active simulation state.");
                return;
            }

            requiresRefresh |= carryResult == ItemStateApplyResult.Stale;
            // The change goes before the carry state and the result, so the client has
            // patched its inventory when it sees the new revision and needs no refresh.
            var change = carryResult == ItemStateApplyResult.Applied
                ? OwnerItemStateChangeMapper.TryCreate(
                    completed.Result.Intent.OperationId,
                    session.CharacterId,
                    completed.Result.Transaction!)
                : null;
            if (change is not null && target.Owner is { } owner)
            {
                sender.SendControl(owner, RealtimeProtocol.EncodeOwnerItemStateChanged(change));
            }

            carrySync.ApplyLatest(target.Entity);
        }

        if (target.Requester is { } requester)
        {
            SendItemOperationResult(
                requester,
                session,
                completed.Result,
                requiresRefresh);
        }

        authorityScheduler.StartNext(completed.Context);
    }

    private void SendItemOperationResult(
        IRealtimePeerConnection peer,
        ActiveSimulationSession session,
        SimulationItemInteractionResult result,
        bool requiresInventoryRefresh)
    {
        if (!itemStateStore.TryGet(
                session.CharacterId,
                session.SimulationSessionId,
                out var carryState))
        {
            return;
        }

        var transaction = result.Transaction;
        var packetResult = new RealtimeItemOperationResult(
            result.Intent.OperationId,
            result.Intent.OperationKind,
            result.Succeeded,
            requiresInventoryRefresh,
            RealtimeStateMapper.ToRealtimeCarryState(carryState!),
            result.Succeeded
                ? null
                : new RealtimeError(result.Error!.Code, result.Error.Message),
            result.Succeeded
                ? transaction!.ItemRevisions
                    .Select(revision => new RealtimeItemRevision(
                        revision.ItemInstanceId,
                        revision.Revision))
                    .ToArray()
                : Array.Empty<RealtimeItemRevision>(),
            result.Succeeded
                ? transaction!.ContainerRevisions
                    .Select(revision => new RealtimeContainerRevision(
                        revision.ContainerId,
                        revision.Revision))
                    .ToArray()
                : Array.Empty<RealtimeContainerRevision>(),
            result.Succeeded
                ? transaction!.RecoveryDeliveryIds.ToArray()
                : Array.Empty<Guid>());
        sender.SendControl(peer, RealtimeProtocol.EncodeItemOperationResult(packetResult));
    }

    private void Cancel(
        RealtimePeerContext context,
        RealtimeItemOperationIntent intent,
        string code,
        string message)
    {
        if (context.Session is not null && peers.TryGetCurrentPeer(context, out var peer))
        {
            SendItemOperationRejection(peer, context.Session, intent, code, message);
        }
    }

    private void SendItemOperationRejection(
        IRealtimePeerConnection peer,
        ActiveSimulationSession session,
        RealtimeItemOperationIntent intent,
        string code,
        string message)
    {
        SendItemOperationResult(
            peer,
            session,
            SimulationItemInteractionResult.Rejected(intent, code, message),
            requiresInventoryRefresh: false);
    }

    private sealed record QueuedItemOperation(
        ItemOperationHandler Owner,
        RealtimeItemOperationIntent Intent) : IRealtimeAuthorityOperation
    {
        public void Start(
            RealtimePeerContext context,
            PlayerSimulationEntity entity,
            PlayerMovementState state)
        {
            Owner.Start(context, context.Session!, Intent);
        }

        public void Cancel(RealtimePeerContext context, string code, string message)
        {
            Owner.Cancel(context, Intent, code, message);
        }
    }
}
