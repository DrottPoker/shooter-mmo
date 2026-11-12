using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Detaches peers from sessions and entities: a connection that ends while its session goes on,
// invalidation, reconnect replacement, authority failures, and the end of a lingering body.
// Every path releases the corpse view and interaction lease the peer held, and a removed entity
// takes its character's state out of the registered per-character stores. Loop-confined.
internal sealed class RealtimeSessionTeardown(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    EntityPresenceReplicator presence,
    ActiveSimulationSessionStore sessionStore,
    CharacterSessionStores characterStores,
    CorpseViewerRegistry corpseViewers,
    WorldInteractionLeaseRegistry? worldInteractionLeases,
    ConnectionEntityBindingRegistry connectionBindings,
    SimulationEntityRegistry entityRegistry,
    RealtimeNetworkMetrics networkMetrics,
    RealtimeLifecycleHooks hooks,
    LingeringBodies bodies)
{
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

        characterStores.Remove(session.CharacterId, session.SimulationSessionId);
        connectionBindings.UnbindConnection(context.PeerId, out _);
        presence.ForgetConnection(context.PeerId);
        presence.BroadcastEntityDespawn(entityId, reason, context.PeerId);
        networkMetrics.SetActiveEntities(presence.ActiveEntityCount);
        hooks.RaisePlayerRemoved(context, session, entityId);
    }

    // The connection of a joined player ended while its session goes on: by a leave, a lost or
    // kicked connection, or a reconnect from another client that takes the session over. The
    // peer lets go of everything it held and of the session, and the entity stays in the world
    // as a lingering body: it stops where it stands, and the connection-ended hooks let the
    // features drop what only a connected player does. False when the peer held no entity of the
    // session.
    public bool LeaveBody(RealtimePeerContext context, ActiveSimulationSession session)
    {
        corpseViewers.RemovePeer(context.PeerId);
        worldInteractionLeases?.RemovePeer(context.PeerId);
        context.AuthorityOperationQueue.Clear();
        context.Session = null;
        peers.RefreshPopulationMetrics();
        var bound = connectionBindings.UnbindConnection(context.PeerId, out var entityId);
        presence.ForgetConnection(context.PeerId);
        if (!bound
            || !entityRegistry.TryGetPlayer(entityId, out var entity)
            || entity!.Session.SimulationSessionId != session.SimulationSessionId)
        {
            return false;
        }

        entity.Movement.Halt();
        bodies.Begin(entity, session);
        hooks.RaiseConnectionEnded(context, session, entity);
        return true;
    }

    // Takes a lingering body out of the world, as RemoveBoundEntity does for a player with a
    // peer. Whoever ends the body decides whether its session is released.
    public void RemoveBody(LingeringBody body, string reason)
    {
        var entityId = body.Entity.NetworkEntityId;
        var session = body.Session;
        bodies.Remove(entityId);
        if (!entityRegistry.RemovePlayer(entityId, session.SimulationSessionId, out _))
        {
            return;
        }

        characterStores.Remove(session.CharacterId, session.SimulationSessionId);
        presence.BroadcastEntityDespawn(entityId, reason, null);
        networkMetrics.SetActiveEntities(presence.ActiveEntityCount);
        hooks.RaisePlayerRemoved(RealtimePeerContext.System, session, entityId);
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
        bodies.Remove(replacedEntity.NetworkEntityId);
        characterStores.Remove(
            CharacterSessionRemovals.ReplacedEntity,
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
        hooks.RaisePlayerRemoved(
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
        characterStores.Remove(session.CharacterId, session.SimulationSessionId);
        context.AuthorityOperationQueue.Clear();
        corpseViewers.RemovePeer(context.PeerId);
        worldInteractionLeases?.RemovePeer(context.PeerId);
        context.Session = null;
        peers.RefreshPopulationMetrics();
        context.ScheduleDisconnect(DateTime.UtcNow);
    }
}
