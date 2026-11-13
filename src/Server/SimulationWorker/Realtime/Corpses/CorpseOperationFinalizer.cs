using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Corpses;
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
    CharacterItemStateStore itemStateStore,
    CarryStateSynchronizer carrySync,
    RealtimeSessionTeardown teardown,
    OperationCompletionTargets targets,
    AuthorityOperationScheduler authorityScheduler,
    CorpseViewerRegistry corpseViewers,
    WorldInteractionLeaseRegistry? worldInteractionLeases,
    CorpseViewDistributor corpseViews,
    DurableCorpseStore corpseStore,
    LiveMobCorpseStore? liveMobCorpseStore,
    ICombatOperationGate? combatGate = null) : IRealtimeCompletionHandler
{
    private readonly CommittedCarryApplier carryApplier = new(itemStateStore, sessionStore);

    public void Complete(RealtimeOperationResult completion)
    {
        if (completion is CorpseOperationCompleted completed)
        {
            FinalizeOperation(completed);
        }
    }

    // The operation committed at GameService whether or not its peer is still there, so the
    // character's item state and the corpse's state take what it committed while the worker holds
    // the session. What belongs to the peer that sent it, its result, its corpse view, and the
    // view's interaction lease, needs that peer; the item change goes to the peer that plays the
    // character now.
    private void FinalizeOperation(CorpseOperationCompleted completed)
    {
        combatGate?.ClientOperationFinished(completed.Session);
        authorityScheduler.Release(completed.Context);
        var resolved = targets.TryResolve(completed.Context, completed.Session, out var target);
        var requester = resolved ? target.Requester : null;
        if (requester is null
            && completed.RemoveOpenReservationOnFailure
            && peers.OwnsPeerId(completed.Context))
        {
            // LiteNetLib reuses peer ids, so only a context that still owns its id may
            // release this open reservation. Every path that disconnects or detaches a
            // context has already released the reservations it held.
            corpseViewers.Close(
                completed.Context.PeerId,
                completed.Result.Intent.CorpseId);
            worldInteractionLeases?.ReleaseCorpse(
                completed.Context.PeerId,
                completed.Result.Intent.CorpseId);
        }

        if (!resolved)
        {
            return;
        }

        var session = target.Session;
        if (!itemStateStore.TryGet(session.CharacterId, session.SimulationSessionId, out _))
        {
            teardown.EndForAuthorityFailure(
                target,
                "carry_state_missing",
                "Authoritative carry state disappeared during the corpse operation.");
            return;
        }

        if (completed.Result.ShouldDisconnect)
        {
            if (requester is not null)
            {
                SendCorpseOperationResult(requester, session, completed.Result);
            }

            teardown.EndForAuthorityFailure(
                target,
                completed.Result.Error!.Code,
                completed.Result.Error.Message);
            return;
        }

        if (!completed.Result.Succeeded)
        {
            if (requester is not null)
            {
                if (completed.RemoveOpenReservationOnFailure)
                {
                    corpseViewers.RemovePeer(completed.Context.PeerId);
                    worldInteractionLeases?.ReleaseCorpse(
                        completed.Context.PeerId,
                        completed.Result.Intent.CorpseId);
                }

                SendCorpseOperationResult(requester, session, completed.Result);
            }

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
                else if (requester is not null)
                {
                    corpseViews.CloseCorpseForPeer(
                        completed.Context,
                        requester,
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
            if (requester is not null)
            {
                corpseViewers.Close(
                    completed.Context.PeerId,
                    completed.Result.Intent.CorpseId);
                worldInteractionLeases?.ReleaseCorpse(
                    completed.Context.PeerId,
                    completed.Result.Intent.CorpseId);
                SendCorpseOperationResult(requester, session, completed.Result);
                sender.SendControl(
                    requester,
                    RealtimeProtocol.EncodeCorpseViewClosed(new RealtimeCorpseViewClosed(
                        completed.Result.Intent.CorpseId,
                        RealtimeCorpseViewClosureCodes.ViewClosed,
                        "The corpse view was closed.")));
            }

            authorityScheduler.StartNext(completed.Context);
            return;
        }

        if (completed.Result.Mutation is not null
            && completed.Result.RequiresInventoryRefresh
            && !TryApplyCorpseCarryState(
                target,
                completed.Result.Intent.OperationId,
                completed.Result.Mutation))
        {
            return;
        }

        if (requester is not null)
        {
            SendCorpseOperationResult(requester, session, completed.Result);
        }

        var resultSnapshot = completed.Result.Snapshot;
        if (resultSnapshot is null)
        {
            corpseViews.CloseInactiveCorpseForAll(
                completed.Result.Intent.CorpseId,
                RealtimeCorpseViewClosureCodes.Invalidated,
                "The corpse is no longer available.");
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

        if (requester is not null && distribution.RequesterChunks.Count > 0)
        {
            corpseViews.SendCorpseState(requester, CorpseViewDistributor.EncodeCorpseStatePackets(distribution.RequesterChunks));
        }

        // Without its requester the corpse goes to every viewer, since the requester's peer views
        // nothing any more and its id may already belong to another viewer.
        if (distribution.ViewerChunks.Count > 0)
        {
            corpseViews.BroadcastCorpseState(
                resultSnapshot.CorpseId,
                CorpseViewDistributor.EncodeCorpseStatePackets(distribution.ViewerChunks),
                requester is null || distribution.RequesterReceivesViewerChunks ? null : completed.Context.PeerId);
        }

        authorityScheduler.StartNext(completed.Context);
    }

    private bool TryApplyCorpseCarryState(
        in OperationCompletionTarget target,
        Guid operationId,
        CorpseMutationResponse mutation)
    {
        // A durable corpse operation answers with the state next to the transaction; a live
        // Mob corpse grant carries it inside its transaction.
        var characterState = mutation.CharacterState ?? mutation.Transaction.CharacterState;
        if (!CommittedCarryApplier.TryGetCommittedState(
                mutation.Transaction with { CharacterState = characterState },
                out var committedState))
        {
            teardown.EndForAuthorityFailure(
                target,
                "item_state_diverged",
                "The corpse transaction returned no committed character state.");
            return false;
        }

        var session = target.Session;
        var carryResult = carryApplier.Apply(session, committedState, out _);
        if (CommittedCarryApplier.IsDiverged(carryResult))
        {
            teardown.EndForAuthorityFailure(
                target,
                "item_state_diverged",
                "Committed corpse transfer and carry revisions diverged from the active simulation state.");
            return false;
        }

        // Sent before the carry state and the result, so the client has patched its inventory
        // when it sees the new revision.
        var change = carryResult == ItemStateApplyResult.Applied
            ? OwnerItemStateChangeMapper.TryCreate(operationId, session.CharacterId, mutation.Transaction)
            : null;
        if (change is not null && target.Owner is { } owner)
        {
            sender.SendControl(owner, RealtimeProtocol.EncodeOwnerItemStateChanged(change));
        }

        carrySync.ApplyLatest(target.Entity);
        return true;
    }

    public void SendCorpseOperationResult(
        IRealtimePeerConnection peer,
        ActiveSimulationSession session,
        SimulationCorpseInteractionResult result)
    {
        if (!itemStateStore.TryGet(
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
