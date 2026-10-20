using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Accepts world interaction intents, applies the per-peer intent rate limit before the
// shared authority queue, finalizes results and revalidates open interactions.
// Loop-confined.
internal sealed class WorldInteractionHandler(
    WorldInteractionAuthorityService? worldInteractionAuthority,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    SimulationEntityRegistry entityRegistry,
    CarryStateSynchronizer carrySync,
    RealtimeSessionTeardown teardown,
    AuthorityOperationScheduler authorityScheduler,
    RealtimeOperationTracker operationTracker,
    WorldInteractionAuthorityOperations authority,
    GatheringHandler gathering,
    WorldInteractionLeaseRegistry? leases = null,
    ICombatOperationGate? combatGate = null) : IRealtimeCompletionHandler
{
    public bool IsActive => worldInteractionAuthority is not null;

    public void HandleIntent(in RealtimeInboundMessage message)
    {
        var peer = message.Peer;
        var context = message.Context;
        var packet = message.Packet;
        if (worldInteractionAuthority is null
            || !peers.TryGetBoundPlayer(context, out _))
        {
            sender.RejectProtocol(
                peer,
                context,
                "world_interaction_unavailable",
                "World interaction authority is not active for this peer.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeWorldInteractionIntent(
                packet,
                out var intent,
                out var interactionError))
        {
            sender.RejectProtocol(
                peer,
                context,
                "invalid_world_interaction",
                interactionError);
            return;
        }

        if (!context.WorldInteractionIntentQuota.TryConsume())
        {
            SendWorldInteractionResult(
                peer,
                new WorldInteractionAuthorityResult(
                    null,
                    new RealtimeWorldInteractionResult(
                        intent.OperationId,
                        intent.InteractionSessionId,
                        intent.OperationKind,
                        false,
                        0,
                        itemStateRevision: 0,
                        message: string.Empty,
                        error: new RealtimeError(
                            WorldInteractionIntentLimiter.RejectionCode,
                            "World interaction intents exceeded the per-peer rate limit.")),
                    null));
            return;
        }

        if (combatGate is not null
            && intent.OperationKind != RealtimeWorldInteractionOperationKind.Close
            && combatGate.IsCharacterDead(context))
        {
            Reject(peer, intent, RealtimeCombatErrorCodes.CharacterDead, "The character is dead.");
            return;
        }

        if (context.AuthorityOperationQueue.Count
            >= RealtimePeerContext.MaximumQueuedAuthorityOperations)
        {
            SendWorldInteractionResult(
                peer,
                new WorldInteractionAuthorityResult(
                    null,
                    new RealtimeWorldInteractionResult(
                        intent.OperationId,
                        intent.InteractionSessionId,
                        intent.OperationKind,
                        false,
                        0,
                        itemStateRevision: 0,
                        message: string.Empty,
                        error: new RealtimeError(
                            "world_interaction_queue_full",
                            "Too many authority operations are waiting for this peer.")),
                    null));
            return;
        }

        combatGate?.BeforeClientOperation(context);
        authorityScheduler.Enqueue(
            context,
            new QueuedWorldInteractionOperation(this, intent));
    }

    private void Reject(
        IRealtimePeerConnection peer,
        RealtimeWorldInteractionIntent intent,
        string code,
        string message)
    {
        SendWorldInteractionResult(
            peer,
            new WorldInteractionAuthorityResult(
                null,
                new RealtimeWorldInteractionResult(
                    intent.OperationId,
                    intent.InteractionSessionId,
                    intent.OperationKind,
                    false,
                    0,
                    itemStateRevision: 0,
                    message: string.Empty,
                    error: new RealtimeError(code, message)),
                null));
    }

    private void Cancel(RealtimePeerContext context, RealtimeWorldInteractionIntent intent, string code, string message)
    {
        if (peers.TryGetCurrentPeer(context, out var peer))
        {
            Reject(peer, intent, code, message);
        }
    }

    private void Start(
        RealtimePeerContext context,
        PlayerSimulationEntity entity,
        RealtimeWorldInteractionIntent intent)
    {
        if (worldInteractionAuthority is null
            || !peers.TryGetCurrentPeer(context, out _))
        {
            // The operation is dropped, so the rest of the queue must not wait for an
            // intent that may never arrive.
            context.AuthorityOperationInFlight = false;
            authorityScheduler.StartNext(context);
            return;
        }

        // Spent arrows commit ahead of the action, so an action computed on the state before
        // them carries the revision they advanced to.
        operationTracker.Track(authority.RunAsync(
            this,
            context,
            entity,
            intent,
            combatGate?.RebaseExpectedCharacterRevision(context, intent.ExpectedCharacterRevision)));
    }

    public void Complete(RealtimeOperationResult completion)
    {
        if (completion is WorldInteractionOperationCompleted completed)
        {
            FinalizeOperation(completed);
        }
    }

    private void FinalizeOperation(
        WorldInteractionOperationCompleted completed)
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

        if (completed.Result.ShouldDisconnect)
        {
            SendWorldInteractionResult(peer, completed.Result);
            var error = completed.Result.Result!.Error!;
            teardown.TerminateForAuthorityFailure(
                peer,
                completed.Context,
                completed.Session,
                error.Code,
                error.Message);
            return;
        }

        // The item change goes before the carry state, so the client patches its inventory
        // when it sees the new revision.
        if (completed.Result.ItemStateChange is not null)
        {
            sender.SendControl(
                peer,
                RealtimeProtocol.EncodeOwnerItemStateChanged(completed.Result.ItemStateChange));
        }

        if (entityRegistry.TryGetPlayerByCharacterId(
                completed.Session.CharacterId,
                out var entity))
        {
            carrySync.ApplyLatest(entity!);
        }

        SendWorldInteractionResult(peer, completed.Result);
        if (completed.Result.GatheringStart is not null)
        {
            gathering.Begin(
                peer,
                completed.Context,
                completed.Session,
                completed.Result.GatheringStart);
        }

        authorityScheduler.StartNext(completed.Context);
    }

    private void SendWorldInteractionResult(
        IRealtimePeerConnection peer,
        WorldInteractionAuthorityResult result)
    {
        if (result.Opened is not null)
        {
            sender.SendControl(
                peer,
                RealtimeProtocol.EncodeWorldInteractionOpened(result.Opened));
        }

        if (result.Result is not null)
        {
            sender.SendControl(
                peer,
                RealtimeProtocol.EncodeWorldInteractionResult(result.Result));
        }

        if (result.Closed is not null)
        {
            sender.SendControl(
                peer,
                RealtimeProtocol.EncodeWorldInteractionClosed(result.Closed));
        }
    }

    // Closes the peer's world actor interaction, such as when its character died, and
    // tells the peer why. Gathering at the actor stops with the interaction.
    public void CloseForPeer(RealtimePeerContext context, string code, string message)
    {
        if (leases is null
            || !leases.TryGet(context.PeerId, out var lease)
            || lease!.TargetKind != RealtimeWorldInteractionTargetKind.WorldActor
            || !leases.ReleaseWorldActor(context.PeerId, lease.InteractionSessionId)
            || !peers.TryGetCurrentPeer(context, out var peer))
        {
            return;
        }

        sender.SendControl(peer, RealtimeProtocol.EncodeWorldInteractionClosed(new RealtimeWorldInteractionClosed(
            lease.InteractionSessionId,
            lease.TargetKind,
            lease.TargetEntityId,
            lease.TargetRuntimeId,
            code,
            message)));
    }

    // Closes world actor interactions whose player, session, target, range or line of
    // sight no longer holds, and tells each affected peer.
    public void RevalidateLeases()
    {
        foreach (var closure in worldInteractionAuthority!.Revalidate(
                     peers.ResolvePlayerForInteraction))
        {
            if (peers.TryGetAuthenticatedPeer(
                    closure.PeerId,
                    out _,
                    out var peer))
            {
                sender.SendControl(
                    peer,
                    RealtimeProtocol.EncodeWorldInteractionClosed(
                        closure.Closed));
            }
        }
    }

    private sealed record QueuedWorldInteractionOperation(
        WorldInteractionHandler Owner,
        RealtimeWorldInteractionIntent Intent) : IRealtimeAuthorityOperation
    {
        public void Start(
            RealtimePeerContext context,
            PlayerSimulationEntity entity,
            PlayerMovementState state)
        {
            Owner.Start(
                context,
                entity,
                Intent);
        }

        public void Cancel(RealtimePeerContext context, string code, string message)
        {
            Owner.Cancel(context, Intent, code, message);
        }
    }
}
