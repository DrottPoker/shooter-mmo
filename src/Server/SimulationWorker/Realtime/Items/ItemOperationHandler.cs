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
    CarryStateStore carryStateStore,
    SimulationEntityRegistry entityRegistry,
    CarryStateSynchronizer carrySync,
    RealtimeSessionTeardown teardown,
    AuthorityOperationScheduler authorityScheduler,
    RealtimeOperationTracker operationTracker,
    ItemInteractionAccessService accessService,
    ItemAuthorityOperations authority) : IRealtimeCompletionHandler
{
    private readonly CommittedCarryApplier carryApplier = new(carryStateStore, sessionStore);

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

        if (!carryStateStore.TryGet(
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

        authorityScheduler.Enqueue(context, new QueuedItemOperation(this, intent));
    }

    private void Start(
        RealtimePeerContext context,
        ActiveSimulationSession session,
        RealtimeItemOperationIntent intent)
    {
        // Access is evaluated on the loop when the operation starts, while the interaction
        // that grants it is still known to be open.
        operationTracker.Track(authority.RunAsync(
            this,
            context,
            session,
            intent,
            accessService.Evaluate(context.PeerId, session)));
    }

    public void Complete(RealtimeOperationResult completion)
    {
        if (completion is ItemOperationCompleted completed)
        {
            FinalizeOperation(completed);
        }
    }

    private void FinalizeOperation(ItemOperationCompleted completed)
    {
        completed.Context.AuthorityOperationInFlight = false;
        if (!peers.TryGetCurrentPeer(completed.Context, out var peer)
            || completed.Context.Session is null
            || completed.Context.Session.SimulationSessionId
                != completed.Session.SimulationSessionId
            || !string.Equals(
                completed.Context.Session.SimulationSessionToken,
                completed.Session.SimulationSessionToken,
                StringComparison.Ordinal))
        {
            return;
        }

        if (!carryStateStore.TryGet(
                completed.Session.CharacterId,
                completed.Session.SimulationSessionId,
                out _))
        {
            teardown.TerminateForAuthorityFailure(
                peer,
                completed.Context,
                completed.Session,
                "carry_state_missing",
                "Authoritative carry state disappeared during the item operation.");
            return;
        }

        if (completed.Result.ShouldDisconnect)
        {
            SendItemOperationResult(
                peer,
                completed.Session,
                completed.Result,
                requiresInventoryRefresh: true);
            teardown.TerminateForAuthorityFailure(
                peer,
                completed.Context,
                completed.Session,
                completed.Result.Error!.Code,
                completed.Result.Error.Message);
            return;
        }

        var requiresRefresh = completed.Result.RequiresInventoryRefresh;
        if (completed.Result.Succeeded)
        {
            // Matches the corpse path: an unexpected revision set ends the session instead
            // of failing the completion with internal_error.
            if (!CommittedCarryApplier.TryGetCommittedCarry(
                    completed.Result.Transaction!,
                    out var committedCarry))
            {
                teardown.TerminateForAuthorityFailure(
                    peer,
                    completed.Context,
                    completed.Session,
                    "item_state_diverged",
                    "The item transaction returned an invalid character revision set.");
                return;
            }

            var carryResult = carryApplier.Apply(completed.Session, committedCarry, out _);
            if (CommittedCarryApplier.IsDiverged(carryResult))
            {
                teardown.TerminateForAuthorityFailure(
                    peer,
                    completed.Context,
                    completed.Session,
                    "item_state_diverged",
                    "Committed item and carry revisions diverged from the active simulation state.");
                return;
            }

            requiresRefresh |= carryResult == CarryStateApplyResult.Stale;
            if (entityRegistry.TryGetPlayerByCharacterId(
                    completed.Session.CharacterId,
                    out var entity))
            {
                carrySync.ApplyLatest(entity!);
            }
        }

        SendItemOperationResult(
            peer,
            completed.Session,
            completed.Result,
            requiresRefresh);
        authorityScheduler.StartNext(completed.Context);
    }

    private void SendItemOperationResult(
        IRealtimePeerConnection peer,
        ActiveSimulationSession session,
        SimulationItemInteractionResult result,
        bool requiresInventoryRefresh)
    {
        if (!carryStateStore.TryGet(
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
    }
}
