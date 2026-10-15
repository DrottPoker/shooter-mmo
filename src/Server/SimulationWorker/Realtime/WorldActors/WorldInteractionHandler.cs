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
    WorldInteractionAuthorityOperations authority) : IRealtimeCompletionHandler
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
                        new RealtimeError(
                            WorldInteractionIntentLimiter.RejectionCode,
                            "World interaction intents exceeded the per-peer rate limit.")),
                    null));
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
                        new RealtimeError(
                            "world_interaction_queue_full",
                            "Too many authority operations are waiting for this peer.")),
                    null));
            return;
        }

        authorityScheduler.Enqueue(
            context,
            new QueuedWorldInteractionOperation(this, intent));
    }

    private void Start(
        RealtimePeerContext context,
        PlayerSimulationEntity entity,
        RealtimeWorldInteractionIntent intent)
    {
        if (worldInteractionAuthority is null
            || !peers.TryGetCurrentPeer(context, out _))
        {
            context.AuthorityOperationInFlight = false;
            return;
        }

        operationTracker.Track(authority.RunAsync(
            this,
            context,
            entity,
            intent));
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

        if (entityRegistry.TryGetPlayerByCharacterId(
                completed.Session.CharacterId,
                out var entity))
        {
            carrySync.ApplyLatest(entity!);
        }

        SendWorldInteractionResult(peer, completed.Result);
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
    }
}
