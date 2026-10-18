using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Detaches peers from sessions and entities: leave, invalidation, reconnect replacement
// and authority failures. Every path releases the corpse view and interaction lease the
// peer held. Loop-confined.
internal sealed class RealtimeSessionTeardown(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    EntityPresenceReplicator presence,
    ActiveSimulationSessionStore sessionStore,
    CharacterItemStateStore itemStateStore,
    CorpseViewerRegistry corpseViewers,
    WorldInteractionLeaseRegistry? worldInteractionLeases,
    ConnectionEntityBindingRegistry connectionBindings,
    SimulationEntityRegistry entityRegistry,
    RealtimeNetworkMetrics networkMetrics)
{
    public IPlayerEntityRemovalObserver? RemovalObserver { get; set; }

    public void RemoveBoundEntity(
        RealtimePeerContext context,
        ActiveSimulationSession session,
        string reason)
    {
        corpseViewers.RemovePeer(context.PeerId);
        worldInteractionLeases?.RemovePeer(context.PeerId);
        context.AuthorityOperationQueue.Clear();
        if (!connectionBindings.TryGetEntityId(context.PeerId, out var entityId)
            || !entityRegistry.RemovePlayer(
                entityId,
                session.SimulationSessionId,
                out _))
        {
            return;
        }

        itemStateStore.Remove(session.CharacterId, session.SimulationSessionId);
        connectionBindings.UnbindConnection(context.PeerId, out _);
        presence.ForgetConnection(context.PeerId);
        presence.BroadcastEntityDespawn(entityId, reason, context.PeerId);
        networkMetrics.SetActiveEntities(presence.ActiveEntityCount);
        RemovalObserver?.OnPlayerEntityRemoved(context, session, entityId);
    }

    public void DetachSupersededPeer(RealtimePeerContext context)
    {
        corpseViewers.RemovePeer(context.PeerId);
        worldInteractionLeases?.RemovePeer(context.PeerId);
        context.AuthorityOperationQueue.Clear();
        connectionBindings.UnbindConnection(context.PeerId, out _);
        presence.ForgetConnection(context.PeerId);
        context.Session = null;
        peers.RefreshPopulationMetrics();
    }

    public void DisconnectReplacedConnection(int? replacedConnectionId)
    {
        if (replacedConnectionId is null
            || !peers.TryGetContext(replacedConnectionId.Value, out var previousContext))
        {
            return;
        }

        if (peers.TryGetCurrentPeer(previousContext, out var previousPeer))
        {
            sender.SendControl(
                previousPeer,
                RealtimeProtocol.EncodeServerDisconnect(
                    "session_reconnected",
                    "This character connected from another client."));
            previousContext.Session = null;
            previousContext.AuthorityOperationQueue.Clear();
            corpseViewers.RemovePeer(previousContext.PeerId);
            worldInteractionLeases?.RemovePeer(previousContext.PeerId);
            peers.RefreshPopulationMetrics();
            previousContext.ScheduleDisconnect(DateTime.UtcNow);
        }

        presence.ForgetConnection(replacedConnectionId.Value);
    }

    public void RemoveReplacedEntity(PlayerSimulationEntity replacedEntity)
    {
        itemStateStore.Remove(
            replacedEntity.Session.CharacterId,
            replacedEntity.Session.SimulationSessionId);
        int? replacedConnectionId = null;
        if (connectionBindings.UnbindEntity(
                replacedEntity.NetworkEntityId,
                out var connectionId))
        {
            replacedConnectionId = connectionId;
            DisconnectReplacedConnection(connectionId);
        }

        presence.BroadcastEntityDespawn(
            replacedEntity.NetworkEntityId,
            "entity_replaced",
            replacedConnectionId);
        RemovalObserver?.OnPlayerEntityRemoved(
            RealtimePeerContext.System,
            replacedEntity.Session,
            replacedEntity.NetworkEntityId);
    }

    // Ends a session whose authoritative state can no longer be trusted.
    public void TerminateForAuthorityFailure(
        IRealtimePeerConnection peer,
        RealtimePeerContext context,
        ActiveSimulationSession session,
        string code,
        string message)
    {
        sender.SendControl(peer, RealtimeProtocol.EncodeServerDisconnect(code, message));
        sessionStore.Invalidate(
            session.CharacterId,
            session.SimulationSessionId,
            session.SimulationSessionToken,
            code,
            message);
        RemoveBoundEntity(context, session, code);
        itemStateStore.Remove(session.CharacterId, session.SimulationSessionId);
        context.AuthorityOperationQueue.Clear();
        corpseViewers.RemovePeer(context.PeerId);
        worldInteractionLeases?.RemovePeer(context.PeerId);
        context.Session = null;
        peers.RefreshPopulationMetrics();
        context.ScheduleDisconnect(DateTime.UtcNow);
    }
}
