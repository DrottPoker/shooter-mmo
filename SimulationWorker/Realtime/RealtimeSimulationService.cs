using System.Collections.Concurrent;
using System.Diagnostics;
using LiteNetLib;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using SimulationWorker.Auth;
using SimulationWorker.Config;
using SimulationWorker.Entities;
using SimulationWorker.Registry;
using SimulationWorker.Sessions;
using SimulationWorker.WorldCollision;

namespace SimulationWorker.Realtime;

public sealed class RealtimeSimulationService(
    SimulationWorkerConfig config,
    SimulationJoinService joinService,
    SimulationSessionReleaseService releaseService,
    ActiveSimulationSessionStore sessionStore,
    SimulationEntityRegistry entityRegistry,
    ConnectionEntityBindingRegistry connectionBindings,
    RealtimeTransportReadiness transportReadiness,
    ChunkedStaticCollisionWorld staticCollisionWorld,
    ICollisionWorld collisionWorld,
    ILogger<RealtimeSimulationService> logger,
    WorldCollisionStreamingStore? collisionStreamingStore = null,
    SimulationInterestManager? providedInterestManager = null,
    RealtimeNetworkMetrics? providedNetworkMetrics = null,
    SimulationWorkerRegistrationLease? registrationLease = null) : BackgroundService
{
    private readonly ConcurrentQueue<RealtimeOperationResult> completedOperations = new();
    private readonly Dictionary<int, PeerContext> peers = [];
    private readonly HashSet<Task> activeOperations = [];
    private readonly object activeOperationsLock = new();
    private readonly SimulationInterestManager interestManager =
        providedInterestManager ?? new SimulationInterestManager(config.InterestManagement);
    private readonly RealtimeNetworkMetrics networkMetrics =
        providedNetworkMetrics ?? new RealtimeNetworkMetrics();
    private NetManager? server;
    private uint serverTick;
    private uint snapshotSequence;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new EventBasedNetListener();
        server = new NetManager(listener)
        {
            DisconnectTimeout = (int)config.JoinHandshakeTimeout.TotalMilliseconds,
            ChannelsCount = RealtimeProtocol.ChannelCount
        };

        listener.ConnectionRequestEvent += request =>
        {
            if ((registrationLease is not null && !registrationLease.IsValid)
                || server.ConnectedPeersCount >= config.MaxConnections)
            {
                request.Reject();
                return;
            }

            request.AcceptIfKey(RealtimeProtocol.ConnectionKey);
        };
        listener.PeerConnectedEvent += HandlePeerConnected;
        listener.PeerDisconnectedEvent += HandlePeerDisconnected;
        listener.NetworkReceiveEvent += HandleNetworkReceive;
        listener.NetworkErrorEvent += (endPoint, socketError) =>
        {
            logger.LogWarning(
                "[SIMULATION] UDP transport error from {EndPoint}: {SocketError}.",
                endPoint,
                socketError);
        };

        if (!server.Start(config.UdpPort))
        {
            throw new InvalidOperationException(
                $"SimulationWorker could not bind UDP port {config.UdpPort}.");
        }

        transportReadiness.MarkListening(config.UdpPort);

        logger.LogInformation(
            "[SIMULATION] Realtime transport listening on UDP port {UdpPort} with protocol version {ProtocolVersion}, {TickRate} simulation ticks per second, and {SnapshotRate} snapshots per second.",
            config.UdpPort,
            RealtimeProtocol.Version,
            config.MovementSimulation.TickRateHz,
            config.SnapshotRateHz);

        var simulationClock = Stopwatch.StartNew();
        var simulationInterval = TimeSpan.FromSeconds(
            1d / config.MovementSimulation.TickRateHz);
        var nextSimulationTick = simulationClock.Elapsed + simulationInterval;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                server.PollEvents();
                ProcessCompletedOperations();
                ProcessSimulationTicks(
                    simulationClock.Elapsed,
                    simulationInterval,
                    ref nextSimulationTick);
                EnforcePeerState();
                await Task.Delay(config.NetworkPollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            server.Stop();
            server = null;
            peers.Clear();
            interestManager.Clear();
            connectionBindings.Clear();
            entityRegistry.Clear();
            networkMetrics.SetActivePeers(0);
            networkMetrics.SetActiveEntities(0);
            await AwaitActiveOperationsAsync();
            logger.LogInformation("[SIMULATION] Realtime transport stopped.");
        }
    }

    private void HandlePeerConnected(NetPeer peer)
    {
        peers[peer.Id] = new PeerContext(peer.Id, DateTime.UtcNow, config.UdpQuotas);
        networkMetrics.SetActivePeers(peers.Count);
        logger.LogInformation(
            "[SIMULATION] UDP peer {PeerId} connected from {EndPoint} and must authenticate before joining.",
            peer.Id,
            peer.Address);
    }

    private void HandlePeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (!peers.Remove(peer.Id, out var context))
        {
            return;
        }

        interestManager.RemoveConnection(peer.Id);
        networkMetrics.SetActivePeers(peers.Count);

        if (context.Session is not null)
        {
            RemoveBoundEntity(context, context.Session, "connection_closed");
            TrackOperation(ReleaseDisconnectedSessionAsync(context.Session));
        }

        logger.LogInformation(
            "[SIMULATION] UDP peer {PeerId} disconnected: {Reason}.",
            peer.Id,
            disconnectInfo.Reason);
    }

    private void HandleNetworkReceive(
        NetPeer peer,
        NetPacketReader reader,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        try
        {
            if (!peers.TryGetValue(peer.Id, out var context))
            {
                return;
            }

            var packet = reader.GetRemainingBytes();
            networkMetrics.RecordReceived(packet.Length);
            if (context.DisconnectAfterUtc is not null)
            {
                return;
            }

            if (!context.Quota.TryConsumeInbound(packet.Length))
            {
                networkMetrics.RecordQuotaRejected();
                RejectProtocol(
                    peer,
                    context,
                    "udp_rate_limited",
                    "The realtime connection exceeded its inbound UDP quota.");
                return;
            }
            if (!RealtimeProtocol.TryReadMessageType(packet, out var messageType))
            {
                RejectProtocol(peer, context, "invalid_packet", "Realtime packet header is invalid.");
                return;
            }

            var session = context.Session;
            if (session is null)
            {
                HandleUnauthenticatedMessage(
                    peer,
                    context,
                    messageType,
                    packet,
                    channel,
                    deliveryMethod);
                return;
            }

            HandleAuthenticatedMessage(
                peer,
                context,
                messageType,
                packet,
                channel,
                deliveryMethod);
        }
        finally
        {
            reader.Recycle();
        }
    }

    private void HandleUnauthenticatedMessage(
        NetPeer peer,
        PeerContext context,
        RealtimeMessageType messageType,
        byte[] packet,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        if (messageType != RealtimeMessageType.JoinRequest
            || channel != RealtimeProtocol.ControlChannel
            || deliveryMethod != DeliveryMethod.ReliableOrdered
            || context.JoinStarted)
        {
            RejectProtocol(peer, context, "join_required", "A single join request must complete before other messages are accepted.");
            return;
        }

        if (registrationLease is not null && !registrationLease.IsValid)
        {
            RejectProtocol(
                peer,
                context,
                "worker_registry_lease_unavailable",
                "SimulationWorker does not currently hold a valid shard registration lease.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeJoinRequest(packet, out var joinTicket, out var error))
        {
            RejectProtocol(peer, context, "invalid_join_request", error);
            return;
        }

        context.JoinStarted = true;
        TrackOperation(CompleteJoinAsync(context, joinTicket));
    }

    private void HandleAuthenticatedMessage(
        NetPeer peer,
        PeerContext context,
        RealtimeMessageType messageType,
        byte[] packet,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        if (messageType == RealtimeMessageType.MovementInputBatch)
        {
            if (channel != RealtimeProtocol.MovementInputChannel
                || deliveryMethod != DeliveryMethod.Sequenced)
            {
                RejectProtocol(
                    peer,
                    context,
                    "invalid_movement_delivery",
                    "Movement inputs require the sequenced movement channel.");
                return;
            }

            if (!TryGetBoundPlayer(context, out var playerEntity))
            {
                RejectProtocol(
                    peer,
                    context,
                    "movement_unavailable",
                    "Movement simulation is not active for this peer.");
                return;
            }

            if (!RealtimeProtocol.TryDecodeMovementInputBatch(packet, out var inputs, out var movementError))
            {
                RejectProtocol(
                    peer,
                    context,
                    "invalid_movement_input",
                    movementError);
                return;
            }

            playerEntity.Movement.AcceptInputs(inputs);
            return;
        }

        if (messageType != RealtimeMessageType.LeaveRequest
            || channel != RealtimeProtocol.ControlChannel
            || deliveryMethod != DeliveryMethod.ReliableOrdered
            || context.LeaveStarted)
        {
            RejectProtocol(peer, context, "unexpected_message", "The message is not valid for the current connection state.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeLeaveRequest(packet, out var simulationSessionId, out var error)
            || !Guid.TryParse(simulationSessionId, out var parsedSessionId))
        {
            SendControl(peer, RealtimeProtocol.EncodeLeaveRejected("invalid_leave_request", error));
            return;
        }

        if (context.Session!.SimulationSessionId != parsedSessionId)
        {
            SendControl(
                peer,
                RealtimeProtocol.EncodeLeaveRejected(
                    "simulation_session_changed",
                    "The active simulation session no longer matches the leave request."));
            return;
        }

        context.LeaveStarted = true;
        TrackOperation(CompleteLeaveAsync(context, context.Session));
    }

    private async Task CompleteJoinAsync(PeerContext context, string joinTicket)
    {
        try
        {
            var result = await joinService.JoinAsync(joinTicket, CancellationToken.None);
            completedOperations.Enqueue(new JoinCompleted(context, result));
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while authenticating UDP peer {PeerId}.",
                context.PeerId);
            completedOperations.Enqueue(new JoinCompleted(
                context,
                SimulationJoinResult<ActiveSimulationSession>.Failure(
                    500,
                    "join_failed",
                    "SimulationWorker could not complete the join request.")));
        }
    }

    private async Task CompleteLeaveAsync(PeerContext context, ActiveSimulationSession session)
    {
        try
        {
            var result = await releaseService.ReleaseAsync(session, CancellationToken.None);
            completedOperations.Enqueue(new LeaveCompleted(context, session, result));
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while releasing UDP peer {PeerId}.",
                context.PeerId);
            completedOperations.Enqueue(new LeaveCompleted(
                context,
                session,
                SimulationSessionReleaseResult.Failure(
                    "leave_failed",
                    "SimulationWorker could not complete the leave request.")));
        }
    }

    private async Task ReleaseDisconnectedSessionAsync(ActiveSimulationSession session)
    {
        try
        {
            var result = await releaseService.ReleaseAsync(session, CancellationToken.None);
            if (!result.Succeeded)
            {
                logger.LogWarning(
                    "[SIMULATION] Could not release disconnected simulation session {SimulationSessionId}: {Code}.",
                    session.SimulationSessionId,
                    result.Code);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "[SIMULATION] Could not release disconnected simulation session {SimulationSessionId}.",
                session.SimulationSessionId);
        }
    }

    private void ProcessCompletedOperations()
    {
        while (completedOperations.TryDequeue(out var operation))
        {
            switch (operation)
            {
                case JoinCompleted joinCompleted:
                    ProcessJoinCompleted(joinCompleted);
                    break;
                case LeaveCompleted leaveCompleted:
                    ProcessLeaveCompleted(leaveCompleted);
                    break;
            }
        }
    }

    private void ProcessJoinCompleted(JoinCompleted completed)
    {
        if (!TryGetCurrentPeer(completed.Context, out var peer))
        {
            if (completed.Result.Succeeded
                && completed.Result.Value is not null)
            {
                TrackOperation(ReleaseDisconnectedSessionAsync(completed.Result.Value));
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
            SendControl(
                peer,
                RealtimeProtocol.EncodeJoinRejected(
                    completed.Result.Error!.Code,
                    completed.Result.Error.Message));
            networkMetrics.RecordJoinRejected();
            completed.Context.DisconnectAfterUtc = DateTime.UtcNow.AddMilliseconds(250);
            return;
        }

        var session = completed.Result.Value!;
        if (!IsCurrentSession(session))
        {
            RejectProtocol(peer, completed.Context, "session_missing", "The joined simulation session is no longer active.");
            return;
        }

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
                collisionWorld,
                Math.Max(
                    1,
                    (int)Math.Ceiling(
                        config.MovementInputSilenceTimeout.TotalSeconds
                        * config.MovementSimulation.TickRateHz))));
        if (registration.ReplacedEntity is not null)
        {
            RemoveReplacedEntity(registration.ReplacedEntity);
        }

        var entity = registration.Entity;
        var binding = connectionBindings.Bind(
            completed.Context.PeerId,
            entity.NetworkEntityId);
        completed.Context.Session = session;
        DisconnectReplacedConnection(binding.ReplacedConnectionId);
        var response = ActiveSimulationSessionResponse.FromSession(session);
        RebuildInterestIndex();
        var joiningInterest = interestManager.Refresh(
            completed.Context.PeerId,
            entity.NetworkEntityId);

        SendControl(
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
                ToRealtimeMovementSettings(),
                ToRealtimePlayerState(entity.Movement.State))));
        SendEntityBaseline(peer, joiningInterest.Visible);
        RefreshInterests(completed.Context.PeerId);
        networkMetrics.RecordJoinAccepted();
        networkMetrics.SetActiveEntities(entityRegistry.ListPlayers().Count);

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

    private void ProcessLeaveCompleted(LeaveCompleted completed)
    {
        if (!TryGetCurrentPeer(completed.Context, out var peer))
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
            SendControl(
                peer,
                RealtimeProtocol.EncodeLeaveRejected(
                    completed.Result.Code!,
                    completed.Result.Message!));
            return;
        }

        logger.LogInformation(
            "[SIMULATION] Account {AccountId} with character {CharacterName} ({CharacterId}) left shard {ShardId}. Simulation session {SimulationSessionId}, UDP peer {PeerId}.",
            completed.Session.AccountId,
            completed.Session.CharacterName,
            completed.Session.CharacterId,
            completed.Session.ShardId,
            completed.Session.SimulationSessionId,
            completed.Context.PeerId);
        RemoveBoundEntity(completed.Context, completed.Session, "left_shard");
        completed.Context.Session = null;
        SendControl(peer, RealtimeProtocol.EncodeLeaveAccepted());
        completed.Context.DisconnectAfterUtc = DateTime.UtcNow.AddMilliseconds(250);
    }

    private void ProcessSimulationTicks(
        TimeSpan elapsed,
        TimeSpan simulationInterval,
        ref TimeSpan nextSimulationTick)
    {
        const int maximumCatchUpTicks = 5;
        var processedTicks = 0;

        while (elapsed >= nextSimulationTick && processedTicks < maximumCatchUpTicks)
        {
            unchecked
            {
                serverTick++;
            }

            RefreshCollisionStreaming();

            foreach (var entity in entityRegistry.ListPlayers())
            {
                entity.Movement.SimulateTick();
            }

            var snapshotIntervalTicks = config.MovementSimulation.TickRateHz / config.SnapshotRateHz;
            if (serverTick % snapshotIntervalTicks == 0)
            {
                BroadcastSimulationSnapshots();
            }

            nextSimulationTick += simulationInterval;
            processedTicks++;
        }

        if (processedTicks == maximumCatchUpTicks && elapsed >= nextSimulationTick)
        {
            logger.LogWarning(
                "[SIMULATION] Movement simulation exceeded its catch-up budget at server tick {ServerTick}. Resynchronizing the fixed-tick clock.",
                serverTick);
            nextSimulationTick = elapsed + simulationInterval;
        }
    }

    private void BroadcastSimulationSnapshots()
    {
        var entitySnapshotsById = entityRegistry.ListPlayers()
            .Where(entity => connectionBindings.TryGetConnectionId(
                entity.NetworkEntityId,
                out _))
            .Select(entity => new RealtimeEntitySnapshot(
                entity.NetworkEntityId,
                entity.Movement.LastProcessedInputSequence,
                ToRealtimePlayerState(entity.Movement.State)))
            .ToDictionary(snapshot => snapshot.EntityId);
        if (entitySnapshotsById.Count == 0)
        {
            return;
        }

        RebuildInterestIndex();
        RefreshInterests(null);

        unchecked
        {
            snapshotSequence++;
        }

        foreach (var context in peers.Values)
        {
            if (context.Session is null
                || !connectionBindings.TryGetEntityId(context.PeerId, out _)
                || !TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            var entitySnapshots = interestManager.GetVisible(context.PeerId)
                .Where(entitySnapshotsById.ContainsKey)
                .Order()
                .Select(entityId => entitySnapshotsById[entityId])
                .ToArray();
            if (entitySnapshots.Length == 0)
            {
                continue;
            }

            var chunkCount = (ushort)Math.Ceiling(
                entitySnapshots.Length / (double)RealtimeProtocol.MaximumSnapshotEntitiesPerChunk);
            for (ushort chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                var entities = entitySnapshots
                    .Skip(chunkIndex * RealtimeProtocol.MaximumSnapshotEntitiesPerChunk)
                    .Take(RealtimeProtocol.MaximumSnapshotEntitiesPerChunk)
                    .ToArray();
                var packet = RealtimeProtocol.EncodeSimulationSnapshot(new RealtimeSimulationSnapshot(
                    snapshotSequence,
                    serverTick,
                    chunkIndex,
                    chunkCount,
                    entities));
                if (context.Quota.TryConsumeSnapshot(packet.Length))
                {
                    peer.Send(packet, DeliveryMethod.Unreliable);
                    networkMetrics.RecordSent(packet.Length);
                    networkMetrics.RecordSnapshotEntities(entities.Length);
                }
                else
                {
                    networkMetrics.RecordSnapshotDropped();
                }
            }
        }
    }

    private RealtimeMovementSettings ToRealtimeMovementSettings()
    {
        var settings = config.MovementSimulation;
        var collision = settings.CharacterCollision;
        return new RealtimeMovementSettings(
            (ushort)settings.TickRateHz,
            (ushort)config.SnapshotRateHz,
            settings.WalkSpeed,
            settings.SprintSpeed,
            settings.RotationSpeedDegrees,
            settings.Gravity,
            settings.MaximumFallSpeed,
            settings.JumpVelocity,
            settings.GroundedVerticalVelocity,
            settings.GroundHeight,
            settings.MinimumX,
            settings.MaximumX,
            settings.MinimumZ,
            settings.MaximumZ,
            collision.Radius,
            collision.Height,
            collision.StepHeight,
            collision.MaximumSlopeDegrees,
            collision.GroundSnapDistance,
            collision.MaximumSubstepDistance,
            (byte)collision.MaximumPenetrationIterations);
    }

    private static RealtimePlayerState ToRealtimePlayerState(PlayerMovementState state)
    {
        return new RealtimePlayerState(
            state.PositionX,
            state.PositionY,
            state.PositionZ,
            state.VelocityX,
            state.VelocityY,
            state.VelocityZ,
            state.YawDegrees,
            state.IsGrounded,
            state.IsSprinting);
    }

    private void DisconnectReplacedConnection(int? replacedConnectionId)
    {
        if (replacedConnectionId is null
            || !peers.TryGetValue(replacedConnectionId.Value, out var previousContext))
        {
            return;
        }

        if (TryGetCurrentPeer(previousContext, out var previousPeer))
        {
            SendControl(
                previousPeer,
                RealtimeProtocol.EncodeServerDisconnect(
                    "session_reconnected",
                    "This character connected from another client."));
            previousContext.Session = null;
            previousContext.DisconnectAfterUtc = DateTime.UtcNow.AddMilliseconds(250);
        }
    }

    private void EnforcePeerState()
    {
        var now = DateTime.UtcNow;
        foreach (var context in peers.Values.ToArray())
        {
            if (!TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            if (context.DisconnectAfterUtc is not null && context.DisconnectAfterUtc <= now)
            {
                server!.DisconnectPeer(peer);
                continue;
            }

            if (context.DisconnectAfterUtc is not null)
            {
                continue;
            }

            var activeSession = context.Session;
            if (activeSession is null)
            {
                if (now - context.ConnectedAtUtc > config.JoinHandshakeTimeout)
                {
                    RejectProtocol(peer, context, "join_timeout", "The join handshake timed out.");
                }

                continue;
            }

            if (!IsCurrentSession(activeSession))
            {
                var reason = sessionStore.TryTakeInvalidation(
                    activeSession.SimulationSessionId,
                    out var invalidation)
                    ? invalidation!
                    : new ActiveSimulationSessionInvalidation(
                        "session_revoked",
                        "The simulation session is no longer active.");
                SendControl(
                    peer,
                    RealtimeProtocol.EncodeServerDisconnect(
                        reason.Code,
                        reason.Message));
                RemoveBoundEntity(context, activeSession, reason.Code);
                context.Session = null;
                context.DisconnectAfterUtc = now.AddMilliseconds(250);
            }
        }
    }

    private bool IsCurrentSession(ActiveSimulationSession session)
    {
        return sessionStore.IsCurrent(session, DateTime.UtcNow);
    }

    private bool TryGetBoundPlayer(PeerContext context, out PlayerSimulationEntity entity)
    {
        if (connectionBindings.TryGetEntityId(context.PeerId, out var entityId)
            && entityRegistry.TryGetPlayer(entityId, out var player))
        {
            entity = player!;
            return true;
        }

        entity = null!;
        return false;
    }

    private void SendEntityBaseline(NetPeer peer, IReadOnlySet<ulong> visibleEntityIds)
    {
        foreach (var entity in entityRegistry.ListPlayers())
        {
            if (visibleEntityIds.Contains(entity.NetworkEntityId))
            {
                SendSpawn(peer, entity);
            }
        }
    }

    private void RefreshInterests(int? excludedConnectionId)
    {
        foreach (var context in peers.Values)
        {
            if (context.PeerId == excludedConnectionId
                || context.Session is null
                || !connectionBindings.TryGetEntityId(context.PeerId, out var controlledEntityId)
                || !TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            var update = interestManager.Refresh(context.PeerId, controlledEntityId);
            foreach (var enteredEntityId in update.Entered)
            {
                if (entityRegistry.TryGetPlayer(enteredEntityId, out var enteredEntity))
                {
                    SendSpawn(peer, enteredEntity!);
                }
            }

            foreach (var exitedEntityId in update.Exited)
            {
                SendDespawn(peer, exitedEntityId, "out_of_interest");
            }
        }
    }

    private void BroadcastEntityDespawn(
        ulong entityId,
        string reason,
        int? excludedConnectionId)
    {
        var affectedConnections = interestManager.ForgetEntity(entityId);
        foreach (var connectionId in affectedConnections)
        {
            if (connectionId != excludedConnectionId
                && peers.TryGetValue(connectionId, out var context)
                && context.Session is not null
                && TryGetCurrentPeer(context, out var peer))
            {
                SendDespawn(peer, entityId, reason);
            }
        }
    }

    private void RemoveBoundEntity(
        PeerContext context,
        ActiveSimulationSession session,
        string reason)
    {
        if (!connectionBindings.TryGetEntityId(context.PeerId, out var entityId)
            || !entityRegistry.RemovePlayer(
                entityId,
                session.SimulationSessionId,
                out _))
        {
            return;
        }

        connectionBindings.UnbindConnection(context.PeerId, out _);
        BroadcastEntityDespawn(entityId, reason, context.PeerId);
        networkMetrics.SetActiveEntities(entityRegistry.ListPlayers().Count);
    }

    private void RemoveReplacedEntity(PlayerSimulationEntity replacedEntity)
    {
        int? replacedConnectionId = null;
        if (connectionBindings.UnbindEntity(
                replacedEntity.NetworkEntityId,
                out var connectionId))
        {
            replacedConnectionId = connectionId;
            DisconnectReplacedConnection(connectionId);
        }

        BroadcastEntityDespawn(
            replacedEntity.NetworkEntityId,
            "entity_replaced",
            replacedConnectionId);
    }

    private RealtimeEntitySpawn ToRealtimeEntitySpawn(PlayerSimulationEntity entity)
    {
        return new RealtimeEntitySpawn(
            entity.NetworkEntityId,
            RealtimeEntityKind.Player,
            entity.Session.CharacterId.ToString("D"),
            entity.Session.CharacterName,
            PlayerSimulationEntity.DefaultArchetypeId,
            serverTick,
            ToRealtimePlayerState(entity.Movement.State));
    }

    private bool TryGetCurrentPeer(PeerContext context, out NetPeer peer)
    {
        peer = null!;
        if (server is null
            || !peers.TryGetValue(context.PeerId, out var currentContext)
            || !ReferenceEquals(context, currentContext))
        {
            return false;
        }

        var candidate = server.GetPeerById(context.PeerId) as NetPeer;
        if (candidate is null || candidate.ConnectionState != ConnectionState.Connected)
        {
            return false;
        }

        peer = candidate;
        return true;
    }

    private void RejectProtocol(
        NetPeer peer,
        PeerContext context,
        string code,
        string message)
    {
        logger.LogWarning(
            "[SIMULATION] Disconnecting UDP peer {PeerId} with code {Code}: {Message}",
            context.PeerId,
            code,
            message);
        SendControl(peer, RealtimeProtocol.EncodeServerDisconnect(code, message));
        context.DisconnectAfterUtc = DateTime.UtcNow.AddMilliseconds(250);
    }

    private void RefreshCollisionStreaming()
    {
        if (collisionStreamingStore is null)
        {
            return;
        }

        var anchors = entityRegistry.ListPlayers()
            .Select(entity => new SimulationVector3(
                entity.Movement.State.PositionX,
                entity.Movement.State.PositionY,
                entity.Movement.State.PositionZ))
            .Append(new SimulationVector3(
                config.MovementSpawn.X,
                config.MovementSpawn.Y,
                config.MovementSpawn.Z));
        collisionStreamingStore.Refresh(anchors);
    }

    private void RebuildInterestIndex()
    {
        interestManager.Rebuild(entityRegistry.ListPlayers().Select(entity =>
            new SimulationInterestEntity(
                entity.NetworkEntityId,
                entity.Movement.State.PositionX,
                entity.Movement.State.PositionZ)));
    }

    private void SendSpawn(NetPeer peer, PlayerSimulationEntity entity)
    {
        SendControl(peer, RealtimeProtocol.EncodeEntitySpawn(ToRealtimeEntitySpawn(entity)));
        networkMetrics.RecordSpawnPacket();
    }

    private void SendDespawn(NetPeer peer, ulong entityId, string reason)
    {
        SendControl(
            peer,
            RealtimeProtocol.EncodeEntityDespawn(new RealtimeEntityDespawn(entityId, reason)));
        networkMetrics.RecordDespawnPacket();
    }

    private void SendControl(NetPeer peer, byte[] packet)
    {
        peer.Send(packet, DeliveryMethod.ReliableOrdered);
        networkMetrics.RecordSent(packet.Length);
    }

    private void TrackOperation(Task operation)
    {
        lock (activeOperationsLock)
        {
            activeOperations.Add(operation);
        }

        _ = operation.ContinueWith(
            completedOperation =>
            {
                lock (activeOperationsLock)
                {
                    activeOperations.Remove(completedOperation);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task AwaitActiveOperationsAsync()
    {
        Task[] pendingOperations;
        lock (activeOperationsLock)
        {
            pendingOperations = activeOperations.ToArray();
        }

        if (pendingOperations.Length > 0)
        {
            await Task.WhenAll(pendingOperations);
        }
    }

    private sealed class PeerContext(
        int peerId,
        DateTime connectedAtUtc,
        UdpQuotaConfig quotaConfig)
    {
        public int PeerId { get; } = peerId;

        public DateTime ConnectedAtUtc { get; } = connectedAtUtc;

        public UdpPeerQuota Quota { get; } = new(quotaConfig);

        public bool JoinStarted { get; set; }

        public bool LeaveStarted { get; set; }

        public ActiveSimulationSession? Session { get; set; }

        public DateTime? DisconnectAfterUtc { get; set; }
    }

    private abstract record RealtimeOperationResult(PeerContext Context);

    private sealed record JoinCompleted(
        PeerContext PeerContext,
        SimulationJoinResult<ActiveSimulationSession> Result)
        : RealtimeOperationResult(PeerContext);

    private sealed record LeaveCompleted(
        PeerContext PeerContext,
        ActiveSimulationSession Session,
        SimulationSessionReleaseResult Result)
        : RealtimeOperationResult(PeerContext);
}
