using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Accepts corpse interaction intents and starts them: local rejections, the interaction
// lease and the viewer reservation are decided synchronously on the loop, and only the
// GameService part runs in CorpseAuthorityOperations. Every completion goes to the
// corpse finalizer. Loop-confined.
internal sealed class CorpseInteractionHandler(
    SimulationWorkerConfig config,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    CarryStateStore carryStateStore,
    DurableCorpseStore corpseStore,
    LiveMobCorpseStore? liveMobCorpseStore,
    CorpseViewerRegistry corpseViewers,
    WorldInteractionLeaseRegistry? worldInteractionLeases,
    AuthorityOperationScheduler authorityScheduler,
    RealtimeOperationTracker operationTracker,
    RealtimeCompletionQueue completions,
    CorpseAuthorityOperations authority,
    CorpseOperationFinalizer finalizer)
{
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
                "corpse_interaction_unavailable",
                "Corpse interaction is not active for this peer.");
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
                "Authoritative carry state is unavailable for corpse interaction.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeCorpseInteractionIntent(
                packet,
                out var intent,
                out var corpseError))
        {
            sender.RejectProtocol(
                peer,
                context,
                "invalid_corpse_operation",
                corpseError);
            return;
        }

        if (context.AuthorityOperationQueue.Count
            >= RealtimePeerContext.MaximumQueuedAuthorityOperations)
        {
            finalizer.SendCorpseOperationRejection(
                peer,
                playerEntity.Session,
                intent,
                "item_operation_queue_full",
                "Too many item or corpse operations are waiting for authority.");
            return;
        }

        authorityScheduler.Enqueue(context, new QueuedCorpseOperation(this, intent));
    }

    private void StartOperation(
        RealtimePeerContext context,
        ActiveSimulationSession session,
        RealtimeCorpseInteractionIntent intent,
        float positionX,
        float positionY,
        float positionZ)
    {
        if (intent.OperationKind == RealtimeCorpseInteractionKind.Close)
        {
            completions.Post(finalizer, new CorpseOperationCompleted(
                context,
                session,
                SimulationCorpseInteractionResult.Closed(intent),
                false));
            return;
        }

        LiveMobCorpseState? liveCorpse = null;
        var isLiveMobCorpse = liveMobCorpseStore is not null
            && liveMobCorpseStore.TryGetActive(intent.CorpseId, out liveCorpse);
        ICorpseRuntimePresence? corpse = isLiveMobCorpse
            ? liveCorpse
            : corpseStore.TryGetActive(intent.CorpseId, out var durableCorpse)
                ? durableCorpse
                : null;
        if (corpse is null)
        {
            completions.Post(finalizer, new CorpseOperationCompleted(
                context,
                session,
                SimulationCorpseInteractionResult.Rejected(
                    intent,
                    "corpse_not_found",
                    "The corpse is no longer active on this Shard.",
                    shouldCloseView: true),
                false));
            return;
        }

        if (!IsWithinCorpseInteractionRange(
                corpse!,
                positionX,
                positionY,
                positionZ))
        {
            completions.Post(finalizer, new CorpseOperationCompleted(
                context,
                session,
                SimulationCorpseInteractionResult.Rejected(
                    intent,
                    "corpse_out_of_range",
                    "Move closer to the corpse before interacting with it."),
                false));
            return;
        }

        if (intent.OperationKind == RealtimeCorpseInteractionKind.Open)
        {
            var acquiredInteractionLease = false;
            if (worldInteractionLeases is not null
                && !worldInteractionLeases.TryAcquireCorpse(
                    context.PeerId,
                    intent.CorpseId,
                    out acquiredInteractionLease,
                    out var leaseCode,
                    out var leaseMessage))
            {
                completions.Post(finalizer, new CorpseOperationCompleted(
                    context,
                    session,
                    SimulationCorpseInteractionResult.Rejected(
                        intent,
                        leaseCode,
                        leaseMessage),
                    false));
                return;
            }

            var alreadyViewing = corpseViewers.IsViewing(
                context.PeerId,
                intent.CorpseId);
            if (!corpseViewers.TryOpen(
                    context.PeerId,
                    intent.CorpseId,
                    out var code,
                    out var message))
            {
                if (acquiredInteractionLease)
                {
                    worldInteractionLeases?.ReleaseCorpse(
                        context.PeerId,
                        intent.CorpseId);
                }

                completions.Post(finalizer, new CorpseOperationCompleted(
                    context,
                    session,
                    SimulationCorpseInteractionResult.Rejected(intent, code, message),
                    false));
                return;
            }

            operationTracker.Track(authority.RunAsync(
                finalizer,
                context,
                session,
                intent,
                removeOpenReservationOnFailure: !alreadyViewing,
                isLiveMobCorpse));
            return;
        }

        if (!corpseViewers.IsViewing(context.PeerId, intent.CorpseId))
        {
            completions.Post(finalizer, new CorpseOperationCompleted(
                context,
                session,
                SimulationCorpseInteractionResult.Rejected(
                    intent,
                    "corpse_view_not_open",
                    "Open the corpse before refreshing or looting it."),
                false));
            return;
        }

        operationTracker.Track(authority.RunAsync(
            finalizer,
            context,
            session,
            intent,
            removeOpenReservationOnFailure: false,
            isLiveMobCorpse));
    }

    private bool IsWithinCorpseInteractionRange(
        ICorpseRuntimePresence corpse,
        float playerX,
        float playerY,
        float playerZ)
    {
        var deltaX = corpse.PositionX - playerX;
        var deltaY = corpse.PositionY - playerY;
        var deltaZ = corpse.PositionZ - playerZ;
        var radius = config.ItemInteraction.CorpseInteractionRadius;
        return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ)
            <= radius * radius;
    }

    private sealed record QueuedCorpseOperation(
        CorpseInteractionHandler Owner,
        RealtimeCorpseInteractionIntent Intent) : IRealtimeAuthorityOperation
    {
        public void Start(
            RealtimePeerContext context,
            PlayerSimulationEntity entity,
            PlayerMovementState state)
        {
            Owner.StartOperation(
                context,
                context.Session!,
                Intent,
                state.PositionX,
                state.PositionY,
                state.PositionZ);
        }
    }
}
