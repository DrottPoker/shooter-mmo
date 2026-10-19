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

// Accepts join and leave requests and finalizes them: a join registers the entity,
// binds the peer and sends the accept, the baseline and the spawns in that order.
// Loop-confined; GameService calls run in SessionAuthorityOperations.
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
    RealtimeSessionTeardown teardown,
    SessionAuthorityOperations sessionOperations,
    RealtimeOperationTracker operationTracker,
    SimulationWorkerRegistrationLease? registrationLease,
    RealtimeNetworkMetrics networkMetrics,
    RealtimePerformanceMetrics performanceMetrics,
    ILogger logger,
    SimulationShardRules? shardRules = null,
    PlayerCombatAuthority? combatAuthority = null) : IRealtimeCompletionHandler
{
    // Hears every character that joined, after its accept was sent; set by the composition.
    public Action<RealtimePeerContext, PlayerSimulationEntity>? PlayerJoined { get; set; }

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

        if (context.Session!.SimulationSessionId != parsedSessionId)
        {
            sender.SendControl(
                peer,
                RealtimeProtocol.EncodeLeaveRejected(
                    "simulation_session_changed",
                    "The active simulation session no longer matches the leave request."));
            return;
        }

        context.LeaveStarted = true;
        // Spent arrows and a death that did not commit yet commit before the session ends.
        if (combatAuthority?.TryDeferLeave(context, context.Session) == true)
        {
            return;
        }

        StartLeave(context, context.Session);
    }

    public void StartLeave(RealtimePeerContext context, ActiveSimulationSession session)
    {
        operationTracker.Track(sessionOperations.RunLeaveAsync(this, context, session));
    }

    public void Complete(RealtimeOperationResult operation)
    {
        switch (operation)
        {
            case JoinCompleted joinCompleted:
                {
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

                    break;
                }
            case LeaveCompleted leaveCompleted:
                ProcessLeaveCompleted(leaveCompleted);
                break;
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
            sessionOperations.ReleaseDetachedInBackground(joinCompleted.Result.Value);
        }
    }

    private void ProcessJoinCompleted(JoinCompleted completed)
    {
        if (!peers.TryGetCurrentPeer(completed.Context, out var peer))
        {
            if (completed.Result.Succeeded
                && completed.Result.Value is not null)
            {
                sessionOperations.ReleaseDetachedInBackground(completed.Result.Value);
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

        var initialMovementState = PlayerMovementSimulation.CreateInitialState(
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
        PlayerJoined?.Invoke(completed.Context, entity);
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

    private void ProcessLeaveCompleted(LeaveCompleted completed)
    {
        if (!peers.TryGetCurrentPeer(completed.Context, out var peer))
        {
            return;
        }

        if (completed.Context.Session != completed.Session)
        {
            return;
        }

        if (!completed.Result.Succeeded)
        {
            logger.LogWarning(
                "[SIMULATION] Leave failed for character {CharacterId} in simulation session {SimulationSessionId} with code {Code}: {Message}",
                completed.Session.CharacterId,
                completed.Session.SimulationSessionId,
                completed.Result.Code,
                completed.Result.Message);
            completed.Context.LeaveStarted = false;
            sender.SendControl(
                peer,
                RealtimeProtocol.EncodeLeaveRejected(
                    completed.Result.Code!,
                    completed.Result.Message!));
            return;
        }

        if (completed.Session.IsSyntheticBot)
        {
            logger.LogDebug(
                "[SIMULATION] Synthetic bot {CharacterName} left shard {ShardId}. Simulation session {SimulationSessionId}, UDP peer {PeerId}.",
                completed.Session.CharacterName,
                completed.Session.ShardId,
                completed.Session.SimulationSessionId,
                completed.Context.PeerId);
        }
        else
        {
            logger.LogInformation(
                "[SIMULATION] Account {AccountId} with character {CharacterName} ({CharacterId}) left shard {ShardId}. Simulation session {SimulationSessionId}, UDP peer {PeerId}.",
                completed.Session.AccountId,
                completed.Session.CharacterName,
                completed.Session.CharacterId,
                completed.Session.ShardId,
                completed.Session.SimulationSessionId,
                completed.Context.PeerId);
        }
        teardown.RemoveBoundEntity(completed.Context, completed.Session, "left_shard");
        completed.Context.Session = null;
        peers.RefreshPopulationMetrics();
        sender.SendControl(peer, RealtimeProtocol.EncodeLeaveAccepted());
        completed.Context.ScheduleDisconnect(DateTime.UtcNow);
    }

    private bool IsCurrentSession(ActiveSimulationSession session)
    {
        return sessionStore.IsCurrent(session, DateTime.UtcNow);
    }
}
