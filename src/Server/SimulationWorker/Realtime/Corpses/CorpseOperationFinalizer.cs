using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Finalizes corpse operations on the realtime loop: fences stale completions, commits
// carry state, sends results and distributes corpse view state. LiteNetLib reuses peer
// ids, so reservations are released only by the context that still owns its id.
internal sealed class CorpseOperationFinalizer(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    ActiveSimulationSessionStore sessionStore,
    CarryStateStore carryStateStore,
    SimulationEntityRegistry entityRegistry,
    CarryStateSynchronizer carrySync,
    RealtimeSessionTeardown teardown,
    AuthorityOperationScheduler authorityScheduler,
    CorpseViewerRegistry corpseViewers,
    WorldInteractionLeaseRegistry? worldInteractionLeases,
    CorpseViewDistributor corpseViews,
    DurableCorpseStore corpseStore,
    LiveMobCorpseStore? liveMobCorpseStore) : IRealtimeCompletionHandler
{
    private readonly CommittedCarryApplier carryApplier = new(carryStateStore, sessionStore);

    public void Complete(RealtimeOperationResult completion)
    {
        if (completion is CorpseOperationCompleted completed)
        {
            FinalizeOperation(completed);
        }
    }

    private void FinalizeOperation(CorpseOperationCompleted completed)
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
            // LiteNetLib reuses peer ids, so only a context that still owns its id may
            // release this open reservation. Every path that disconnects or detaches a
            // context has already released the reservations it held.
            if (completed.RemoveOpenReservationOnFailure && peers.OwnsPeerId(completed.Context))
            {
                corpseViewers.Close(
                    completed.Context.PeerId,
                    completed.Result.Intent.CorpseId);
                worldInteractionLeases?.ReleaseCorpse(
                    completed.Context.PeerId,
                    completed.Result.Intent.CorpseId);
            }

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
                "Authoritative carry state disappeared during the corpse operation.");
            return;
        }

        if (completed.Result.ShouldDisconnect)
        {
            SendCorpseOperationResult(peer, completed.Session, completed.Result);
            teardown.TerminateForAuthorityFailure(
                peer,
                completed.Context,
                completed.Session,
                completed.Result.Error!.Code,
                completed.Result.Error.Message);
            return;
        }

        if (!completed.Result.Succeeded)
        {
            if (completed.RemoveOpenReservationOnFailure)
            {
                corpseViewers.RemovePeer(completed.Context.PeerId);
                worldInteractionLeases?.ReleaseCorpse(
                    completed.Context.PeerId,
                    completed.Result.Intent.CorpseId);
            }

            SendCorpseOperationResult(peer, completed.Session, completed.Result);
            if (completed.Result.ShouldCloseView)
            {
                if (CorpseViewDistributor.ShouldCloseCorpseForAll(completed.Result.Error!.Code))
                {
                    corpseViews.CloseCorpseForAll(
                        completed.Result.Intent.CorpseId,
                        completed.Result.Error.Code,
                        completed.Result.Error.Message,
                        removeFromStore: true);
                }
                else
                {
                    corpseViews.CloseCorpseForPeer(
                        completed.Context,
                        peer,
                        completed.Result.Intent.CorpseId,
                        completed.Result.Error.Code,
                        completed.Result.Error.Message);
                }
            }

            authorityScheduler.StartNext(completed.Context);
            return;
        }

        if (completed.Result.Intent.OperationKind
            == RealtimeCorpseInteractionKind.Close)
        {
            corpseViewers.Close(
                completed.Context.PeerId,
                completed.Result.Intent.CorpseId);
            worldInteractionLeases?.ReleaseCorpse(
                completed.Context.PeerId,
                completed.Result.Intent.CorpseId);
            SendCorpseOperationResult(peer, completed.Session, completed.Result);
            sender.SendControl(
                peer,
                RealtimeProtocol.EncodeCorpseViewClosed(new RealtimeCorpseViewClosed(
                    completed.Result.Intent.CorpseId,
                    "corpse_view_closed",
                    "The corpse view was closed.")));
            authorityScheduler.StartNext(completed.Context);
            return;
        }

        if (completed.Result.Mutation is not null
            && completed.Result.RequiresInventoryRefresh
            && !TryApplyCorpseCarryState(
                peer,
                completed.Context,
                completed.Session,
                completed.Result.Mutation.Transaction))
        {
            return;
        }

        SendCorpseOperationResult(peer, completed.Session, completed.Result);
        var resultSnapshot = completed.Result.Snapshot;
        if (resultSnapshot is null)
        {
            corpseViews.CloseCorpseForAll(
                completed.Result.Intent.CorpseId,
                "corpse_invalidated",
                "The corpse is no longer available.",
                removeFromStore: true);
            authorityScheduler.StartNext(completed.Context);
            return;
        }

        corpseViews.TryGetCachedSnapshot(resultSnapshot.CorpseId, out var cachedSnapshot);
        var distribution = CorpseStateDistribution.Plan(
            cachedSnapshot,
            resultSnapshot,
            completed.Result.Mutation is not null);
        if (distribution.CachesResult)
        {
            if (liveMobCorpseStore is null
                || !liveMobCorpseStore.TryGetActive(resultSnapshot.CorpseId, out _))
            {
                corpseStore.ApplySnapshot(resultSnapshot);
            }

            corpseViews.CacheSnapshot(resultSnapshot);
        }

        if (distribution.RequesterChunks.Count > 0)
        {
            corpseViews.SendCorpseState(peer, CorpseViewDistributor.EncodeCorpseStatePackets(distribution.RequesterChunks));
        }

        if (distribution.ViewerChunks.Count > 0)
        {
            corpseViews.BroadcastCorpseState(
                resultSnapshot.CorpseId,
                CorpseViewDistributor.EncodeCorpseStatePackets(distribution.ViewerChunks),
                distribution.RequesterReceivesViewerChunks ? null : completed.Context.PeerId);
        }

        authorityScheduler.StartNext(completed.Context);
    }

    private bool TryApplyCorpseCarryState(
        IRealtimePeerConnection peer,
        RealtimePeerContext context,
        ActiveSimulationSession session,
        SimulationItemTransactionResponse transaction)
    {
        if (!CommittedCarryApplier.TryGetCommittedCarry(transaction, out var committedCarry))
        {
            teardown.TerminateForAuthorityFailure(
                peer,
                context,
                session,
                "item_state_diverged",
                "The corpse transaction returned an invalid character revision set.");
            return false;
        }

        var carryResult = carryApplier.Apply(session, committedCarry, out _);
        if (CommittedCarryApplier.IsDiverged(carryResult))
        {
            teardown.TerminateForAuthorityFailure(
                peer,
                context,
                session,
                "item_state_diverged",
                "Committed corpse transfer and carry revisions diverged from the active simulation state.");
            return false;
        }

        if (entityRegistry.TryGetPlayerByCharacterId(
                session.CharacterId,
                out var entity))
        {
            carrySync.ApplyLatest(entity!);
        }

        return true;
    }

    public void SendCorpseOperationResult(
        IRealtimePeerConnection peer,
        ActiveSimulationSession session,
        SimulationCorpseInteractionResult result)
    {
        if (!carryStateStore.TryGet(
                session.CharacterId,
                session.SimulationSessionId,
                out var carryState))
        {
            return;
        }

        sender.SendControl(
            peer,
            RealtimeProtocol.EncodeCorpseInteractionResult(
                new RealtimeCorpseInteractionResult(
                    result.Intent.OperationId,
                    result.Intent.OperationKind,
                    result.Intent.CorpseId,
                    result.Succeeded,
                    result.RequiresCorpseRefresh,
                    result.RequiresInventoryRefresh,
                    RealtimeStateMapper.ToRealtimeCarryState(carryState!),
                    result.Succeeded
                        ? null!
                        : new RealtimeError(
                            result.Error!.Code,
                            result.Error.Message))));
    }

    public void SendCorpseOperationRejection(
        IRealtimePeerConnection peer,
        ActiveSimulationSession session,
        RealtimeCorpseInteractionIntent intent,
        string code,
        string message)
    {
        SendCorpseOperationResult(
            peer,
            session,
            SimulationCorpseInteractionResult.Rejected(intent, code, message));
    }
}
