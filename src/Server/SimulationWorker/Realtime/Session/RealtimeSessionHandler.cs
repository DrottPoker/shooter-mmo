using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// Accepts join and leave requests and finalizes joins: a join registers the entity, or resumes a
// lingering body of its session, binds the peer and sends the accept, the baseline and the spawns
// in that order. A leave ends the connection at once: the entity stays as a lingering body while
// the session goes on. Loop-confined; GameService calls run in SessionAuthorityOperations.
internal sealed class RealtimeSessionHandler(
    SimulationWorkerConfig config,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    ActiveSimulationSessionStore sessionStore,
    CharacterItemStateStore itemStateStore,
    SimulationEntityRegistry entityRegistry,
    ConnectionEntityBindingRegistry connectionBindings,
    ChunkedStaticCollisionWorld staticCollisionWorld,
    ICollisionWorld collisionWorld,
    PeerAdmissionGuard admissionGuard,
    EntityPresenceReplicator presence,
    RealtimeLifecycleHooks hooks,
    RealtimeSessionTeardown teardown,
    LingeringBodies bodies,
    SessionAuthorityOperations sessionOperations,
    RealtimeOperationTracker operationTracker,
    SimulationWorkerRegistrationLease? registrationLease,
    RealtimeNetworkMetrics networkMetrics,
    RealtimePerformanceMetrics performanceMetrics,
    ILogger logger,
    SimulationShardRules? shardRules = null,
    PlayerCombatAuthority? combatAuthority = null) : IRealtimeCompletionHandler
{
    // A peer without a session may send one join request, and a joined peer a leave request, which
    // ends its session on the peer at once.
    public static void AddRoutes(
        RealtimeMessageRoutes routes,
        RealtimeMessageHandler join,
        RealtimeMessageHandler leave)
    {
        routes.AddUnauthenticated(
            RealtimeMessageType.JoinRequest,
            RealtimeMessageRoutes.JoinRequired,
            join,
            static context => context.JoinStarted);
        routes.AddAuthenticated(
            RealtimeMessageType.LeaveRequest,
            RealtimeMessageRoutes.UnexpectedMessage,
            leave);
    }

    public void HandleJoinRequest(in RealtimeInboundMessage message)
    {
        var peer = message.Peer;
        var context = message.Context;
        var packet = message.Packet;
        if (registrationLease is not null && !registrationLease.IsValid)
        {
            sender.RejectProtocol(
                peer,
                context,
                "worker_registry_lease_unavailable",
                "SimulationWorker does not currently hold a valid shard registration lease.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeJoinRequest(packet, out var joinTicket, out var error))
        {
            sender.RejectProtocol(peer, context, "invalid_join_request", error);
            return;
        }

        context.JoinStarted = true;
        operationTracker.Track(sessionOperations.RunJoinAsync(this, context, joinTicket));
    }

    public void HandleLeaveRequest(in RealtimeInboundMessage message)
    {
        var peer = message.Peer;
        var context = message.Context;
        var packet = message.Packet;
        if (!RealtimeProtocol.TryDecodeLeaveRequest(packet, out var simulationSessionId, out var error))
        {
            sender.SendControl(peer, RealtimeProtocol.EncodeLeaveRejected("invalid_leave_request", error));
            return;
        }

        if (!Guid.TryParse(simulationSessionId, out var parsedSessionId))
        {
            sender.SendControl(
                peer,
                RealtimeProtocol.EncodeLeaveRejected(
                    "invalid_leave_request",
                    "The leave request must name a simulation session id."));
            return;
        }

        var session = context.Session!;
        if (session.SimulationSessionId != parsedSessionId)
        {
            sender.SendControl(
                peer,
                RealtimeProtocol.EncodeLeaveRejected(
                    "simulation_session_changed",
                    "The active simulation session no longer matches the leave request."));
            return;
        }

        // A leave follows the rule of a lost connection: while the session goes on, the character
        // stays in the world as a lingering body, and the session is released when the body goes.
        // A session that ended already takes its entity with it.
        var lingers = sessionStore.IsCurrent(session, DateTime.UtcNow)
            || sessionStore.IsSupersededByReconnect(session);
        if (lingers)
        {
            teardown.LeaveBody(context, session);
        }
        else
        {
            teardown.RemoveBoundEntity(context, session, "left_shard");
            context.Session = null;
            peers.RefreshPopulationMetrics();
        }

        LogLeave(context, session, lingers);
        sender.SendControl(
            peer,
            RealtimeProtocol.EncodeLeaveAccepted((ushort)(lingers ? bodies.Seconds : 0)));
        context.ScheduleDisconnect(DateTime.UtcNow);
    }

    public void Complete(RealtimeOperationResult operation)
    {
        if (operation is not JoinCompleted joinCompleted)
        {
            return;
        }

        var joinStarted = Stopwatch.GetTimestamp();
        performanceMetrics.RecordJoinQueueDelay(
            Stopwatch.GetElapsedTime(
                joinCompleted.EnqueuedTimestamp,
                joinStarted));
        try
        {
            ProcessJoinCompleted(joinCompleted);
        }
        finally
        {
            performanceMetrics.RecordJoinFinalization(
                Stopwatch.GetElapsedTime(joinStarted));
        }
    }

    // A join that registered its session but never bound it to the peer would leave
    // the session leased, so it is released before the peer is disconnected.
    public void OnCompletionFailed(RealtimeOperationResult operation)
    {
        if (operation is JoinCompleted { Result.Succeeded: true } joinCompleted
            && joinCompleted.Context.Session is null
            && joinCompleted.Result.Value is not null)
        {
            EndUnboundJoin(joinCompleted.Result.Value);
        }
    }

    private void ProcessJoinCompleted(JoinCompleted completed)
    {
        if (!peers.TryGetCurrentPeer(completed.Context, out var peer))
        {
            if (completed.Result.Succeeded
                && completed.Result.Value is not null)
            {
                EndUnboundJoin(completed.Result.Value);
            }

            return;
        }

        if (!completed.Result.Succeeded)
        {
            logger.LogWarning(
                "[SIMULATION] UDP peer {PeerId} was denied shard access with code {Code}: {Message}",
                completed.Context.PeerId,
                completed.Result.Error!.Code,
                completed.Result.Error.Message);
            sender.SendControl(
                peer,
                RealtimeProtocol.EncodeJoinRejected(
                    completed.Result.Error!.Code,
                    completed.Result.Error.Message));
            networkMetrics.RecordJoinRejected();
            completed.Context.ScheduleDisconnect(DateTime.UtcNow);
            return;
        }

        var session = completed.Result.Value!;
        if (!IsCurrentSession(session))
        {
            sessionOperations.ReleaseDetachedInBackground(session);
            sender.RejectProtocol(peer, completed.Context, "session_missing", "The joined simulation session is no longer active.");
            return;
        }

        if (!itemStateStore.TryGet(
                session.CharacterId,
                session.SimulationSessionId,
                out var currentCarryState))
        {
            sessionOperations.ReleaseDetachedInBackground(session);
            sender.RejectProtocol(
                peer,
                completed.Context,
                "carry_state_missing",
                "The authoritative carry state is no longer active.");
            return;
        }

        session = session with { CarryState = currentCarryState! };

        // A registered placement, such as a saved place, decides where a new entity starts.
        var initialMovementState = hooks.PlaceNewPlayer(session)
            ?? PlayerMovementSimulation.CreateInitialState(
                config.MovementSimulation,
                collisionWorld,
                config.MovementSpawn.X,
                config.MovementSpawn.Y,
                config.MovementSpawn.Z,
                config.MovementSpawn.YawDegrees);
        var registration = entityRegistry.RegisterPlayer(
            session,
            () => new AuthoritativePlayerMovement(
                initialMovementState,
                config.MovementSimulation,
                session.CarryState,
                collisionWorld));
        if (registration.ReplacedEntity is not null)
        {
            teardown.RemoveReplacedEntity(registration.ReplacedEntity);
        }

        var entity = registration.Entity;
        if (!registration.IsNewEntity)
        {
            entity.Movement.ResetInputSequence();
        }

        entity.Movement.ApplyCarryState(session.CarryState);
        var binding = connectionBindings.Bind(
            completed.Context.PeerId,
            entity.NetworkEntityId);
        completed.Context.Session = session;

        // A reconnect resumes the lingering body of its session where it stands, with its
        // vitals, and its time ends.
        bodies.Remove(entity.NetworkEntityId);
        admissionGuard.OnPeerAuthenticated(completed.Context.PeerId);
        peers.RefreshPopulationMetrics();
        teardown.DisconnectReplacedConnection(binding.ReplacedConnectionId);
        var response = ActiveSimulationSessionResponse.FromSession(session);
        var connectionsEnteringNewEntity = presence.TrackJoiningEntity(
            entity,
            registration.IsNewEntity);
        var joiningInterest = presence.RefreshConnection(
            completed.Context.PeerId,
            entity.NetworkEntityId);

        sender.SendControl(
            peer,
            RealtimeProtocol.EncodeJoinAccepted(new RealtimeJoinAccepted(
                response.SimulationSessionId.ToString("D"),
                response.AccountId.ToString("D"),
                response.CharacterId.ToString("D"),
                response.CharacterName,
                response.ShardId,
                response.WorldId,
                entity.NetworkEntityId,
                GameSimulationCompatibility.Revision,
                staticCollisionWorld.Revision,
                response.JoinedAt.ToString("O"),
                response.SessionExpiresAt.ToString("O"),
                response.IsReconnect,
                RealtimeStateMapper.ToRealtimeCarryState(entity.Movement.CarryState),
                RealtimeStateMapper.ToRealtimeMovementSettings(config),
                RealtimeStateMapper.ToRealtimePlayerState(entity.Movement.State),
                new RealtimeShardRules(shardRules?.Current.PlayerVersusPlayer ?? false))));
        hooks.RaisePlayerJoined(completed.Context, entity);
        presence.SendEntityBaseline(peer, joiningInterest.Visible);
        presence.SendEntitySpawnToConnections(entity, connectionsEnteringNewEntity);
        networkMetrics.RecordJoinAccepted();
        networkMetrics.SetActiveEntities(presence.ActiveEntityCount);

        if (session.IsSyntheticBot)
        {
            logger.LogDebug(
                "[SIMULATION] Synthetic bot {CharacterName} joined shard {ShardId}. Simulation session {SimulationSessionId}, network entity {EntityId}, UDP peer {PeerId}.",
                response.CharacterName,
                response.ShardId,
                response.SimulationSessionId,
                entity.NetworkEntityId,
                completed.Context.PeerId);
        }
        else
        {
            logger.LogInformation(
                "[SIMULATION] Account {AccountId} with character {CharacterName} ({CharacterId}) connected to shard {ShardId} for world {WorldId}. Simulation session {SimulationSessionId}, network entity {EntityId}, UDP peer {PeerId}.",
                response.AccountId,
                response.CharacterName,
                response.CharacterId,
                response.ShardId,
                response.WorldId,
                response.SimulationSessionId,
                entity.NetworkEntityId,
                completed.Context.PeerId);
        }
    }

    private void LogLeave(RealtimePeerContext context, ActiveSimulationSession session, bool lingers)
    {
        if (session.IsSyntheticBot)
        {
            logger.LogDebug(
                "[SIMULATION] Synthetic bot {CharacterName} left shard {ShardId}. Simulation session {SimulationSessionId}, UDP peer {PeerId}.",
                session.CharacterName,
                session.ShardId,
                session.SimulationSessionId,
                context.PeerId);
        }
        else
        {
            logger.LogInformation(
                "[SIMULATION] Account {AccountId} with character {CharacterName} ({CharacterId}) left shard {ShardId}. Simulation session {SimulationSessionId}, UDP peer {PeerId}.",
                session.AccountId,
                session.CharacterName,
                session.CharacterId,
                session.ShardId,
                session.SimulationSessionId,
                context.PeerId);
        }

        if (lingers)
        {
            logger.LogDebug(
                "[SIMULATION] The body of character {CharacterId} stays in the world for {Seconds} seconds.",
                session.CharacterId,
                bodies.Seconds);
        }
    }

    // A join whose peer went before it was bound. A reconnect of a lingering body keeps the body
    // in the world under the session's new generation, since the session goes on; any other join
    // releases its session.
    private void EndUnboundJoin(ActiveSimulationSession session)
    {
        if (bodies.TryGet(session.CharacterId, out var body)
            && body.Session.SimulationSessionId == session.SimulationSessionId
            && IsCurrentSession(session))
        {
            body.Session = session;
            body.Entity.RefreshSession(session);
            combatAuthority?.RenewSession(session);
            return;
        }

        sessionOperations.ReleaseDetachedInBackground(session);
    }

    private bool IsCurrentSession(ActiveSimulationSession session)
    {
        return sessionStore.IsCurrent(session, DateTime.UtcNow);
    }
}
