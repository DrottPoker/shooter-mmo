using System.Collections.Concurrent;
using System.Diagnostics;
using LiteNetLib;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using WorldServer.Auth;
using WorldServer.Config;
using WorldServer.Sessions;

namespace WorldServer.Realtime;

public sealed class RealtimeServerService(
    WorldServerConfig config,
    WorldJoinService joinService,
    WorldSessionReleaseService releaseService,
    ActivePlayerSessionStore sessionStore,
    RealtimeTransportReadiness transportReadiness,
    ChunkedStaticCollisionWorld staticCollisionWorld,
    ICollisionWorld collisionWorld,
    ILogger<RealtimeServerService> logger) : BackgroundService
{
    private readonly ConcurrentQueue<RealtimeOperationResult> completedOperations = new();
    private readonly Dictionary<int, PeerContext> peers = [];
    private readonly HashSet<Task> activeOperations = [];
    private readonly object activeOperationsLock = new();
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
            if (server.ConnectedPeersCount >= config.MaxConnections)
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
                "[WORLDSERVER] UDP transport error from {EndPoint}: {SocketError}.",
                endPoint,
                socketError);
        };

        if (!server.Start(config.UdpPort))
        {
            throw new InvalidOperationException(
                $"WorldServer could not bind UDP port {config.UdpPort}.");
        }

        transportReadiness.MarkListening(config.UdpPort);

        logger.LogInformation(
            "[WORLDSERVER] Realtime transport listening on UDP port {UdpPort} with protocol version {ProtocolVersion}, {TickRate} simulation ticks per second, and {SnapshotRate} snapshots per second.",
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
            await AwaitActiveOperationsAsync();
            logger.LogInformation("[WORLDSERVER] Realtime transport stopped.");
        }
    }

    private void HandlePeerConnected(NetPeer peer)
    {
        peers[peer.Id] = new PeerContext(peer.Id, DateTime.UtcNow);
        logger.LogInformation(
            "[WORLDSERVER] UDP peer {PeerId} connected from {EndPoint} and must authenticate before joining.",
            peer.Id,
            peer.Address);
    }

    private void HandlePeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (!peers.Remove(peer.Id, out var context))
        {
            return;
        }

        if (context.Session is not null)
        {
            TrackOperation(ReleaseDisconnectedSessionAsync(context.Session));
        }

        logger.LogInformation(
            "[WORLDSERVER] UDP peer {PeerId} disconnected: {Reason}.",
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

            if (context.Movement is null)
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

            context.Movement.AcceptInputs(inputs);
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

        if (!RealtimeProtocol.TryDecodeLeaveRequest(packet, out var worldSessionId, out var error)
            || !Guid.TryParse(worldSessionId, out var parsedSessionId))
        {
            peer.Send(
                RealtimeProtocol.EncodeLeaveRejected("invalid_leave_request", error),
                DeliveryMethod.ReliableOrdered);
            return;
        }

        if (context.Session!.WorldSessionId != parsedSessionId)
        {
            peer.Send(
                RealtimeProtocol.EncodeLeaveRejected(
                    "world_session_changed",
                    "The active world session no longer matches the leave request."),
                DeliveryMethod.ReliableOrdered);
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
                "[WORLDSERVER] Unhandled error while authenticating UDP peer {PeerId}.",
                context.PeerId);
            completedOperations.Enqueue(new JoinCompleted(
                context,
                WorldJoinResult<ActivePlayerSession>.Failure(
                    500,
                    "join_failed",
                    "WorldServer could not complete the join request.")));
        }
    }

    private async Task CompleteLeaveAsync(PeerContext context, ActivePlayerSession session)
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
                "[WORLDSERVER] Unhandled error while releasing UDP peer {PeerId}.",
                context.PeerId);
            completedOperations.Enqueue(new LeaveCompleted(
                context,
                session,
                WorldSessionReleaseResult.Failure(
                    "leave_failed",
                    "WorldServer could not complete the leave request.")));
        }
    }

    private async Task ReleaseDisconnectedSessionAsync(ActivePlayerSession session)
    {
        try
        {
            var result = await releaseService.ReleaseAsync(session, CancellationToken.None);
            if (!result.Succeeded)
            {
                logger.LogWarning(
                    "[WORLDSERVER] Could not release disconnected world session {WorldSessionId}: {Code}.",
                    session.WorldSessionId,
                    result.Code);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "[WORLDSERVER] Could not release disconnected world session {WorldSessionId}.",
                session.WorldSessionId);
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
                "[WORLDSERVER] UDP peer {PeerId} was denied world access with code {Code}: {Message}",
                completed.Context.PeerId,
                completed.Result.Error!.Code,
                completed.Result.Error.Message);
            peer.Send(
                RealtimeProtocol.EncodeJoinRejected(
                    completed.Result.Error!.Code,
                    completed.Result.Error.Message),
                DeliveryMethod.ReliableOrdered);
            completed.Context.DisconnectAfterUtc = DateTime.UtcNow.AddMilliseconds(250);
            return;
        }

        var session = completed.Result.Value!;
        if (!IsCurrentSession(session))
        {
            RejectProtocol(peer, completed.Context, "session_missing", "The joined world session is no longer active.");
            return;
        }

        completed.Context.Session = session;
        var previousMovementState = FindPreviousCharacterMovementState(
            completed.Context,
            session.CharacterId);
        var initialMovementState = previousMovementState
            ?? PlayerMovementSimulation.CreateInitialState(
                config.MovementSimulation,
                collisionWorld,
                config.MovementSpawn.X,
                config.MovementSpawn.Y,
                config.MovementSpawn.Z,
                config.MovementSpawn.YawDegrees);
        completed.Context.Movement = new AuthoritativePlayerMovement(
            initialMovementState,
            config.MovementSimulation,
            collisionWorld,
            Math.Max(
                1,
                (int)Math.Ceiling(
                    config.MovementInputSilenceTimeout.TotalSeconds
                    * config.MovementSimulation.TickRateHz)));
        DisconnectPreviousCharacterPeer(completed.Context, session);
        var response = ActivePlayerSessionResponse.FromSession(session);

        peer.Send(
            RealtimeProtocol.EncodeJoinAccepted(new RealtimeJoinAccepted(
                response.WorldSessionId.ToString("D"),
                response.AccountId.ToString("D"),
                response.CharacterId.ToString("D"),
                response.CharacterName,
                response.WorldId,
                GameSimulationCompatibility.Revision,
                staticCollisionWorld.Revision,
                response.JoinedAt.ToString("O"),
                response.SessionExpiresAt.ToString("O"),
                response.IsReconnect,
                ToRealtimeMovementSettings(),
                ToRealtimePlayerState(initialMovementState))),
            DeliveryMethod.ReliableOrdered);

        logger.LogInformation(
            "[WORLDSERVER] Account {AccountId} with character {CharacterName} ({CharacterId}) connected to world {WorldId}. World session {WorldSessionId}, UDP peer {PeerId}.",
            response.AccountId,
            response.CharacterName,
            response.CharacterId,
            response.WorldId,
            response.WorldSessionId,
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
                "[WORLDSERVER] Leave failed for character {CharacterId} in world session {WorldSessionId} with code {Code}: {Message}",
                completed.Session.CharacterId,
                completed.Session.WorldSessionId,
                completed.Result.Code,
                completed.Result.Message);
            completed.Context.LeaveStarted = false;
            peer.Send(
                RealtimeProtocol.EncodeLeaveRejected(
                    completed.Result.Code!,
                    completed.Result.Message!),
                DeliveryMethod.ReliableOrdered);
            return;
        }

        logger.LogInformation(
            "[WORLDSERVER] Account {AccountId} with character {CharacterName} ({CharacterId}) left world {WorldId}. World session {WorldSessionId}, UDP peer {PeerId}.",
            completed.Session.AccountId,
            completed.Session.CharacterName,
            completed.Session.CharacterId,
            completed.Session.WorldId,
            completed.Session.WorldSessionId,
            completed.Context.PeerId);
        completed.Context.Session = null;
        completed.Context.Movement = null;
        peer.Send(RealtimeProtocol.EncodeLeaveAccepted(), DeliveryMethod.ReliableOrdered);
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

            foreach (var context in peers.Values)
            {
                context.Movement?.SimulateTick();
            }

            var snapshotIntervalTicks = config.MovementSimulation.TickRateHz / config.SnapshotRateHz;
            if (serverTick % snapshotIntervalTicks == 0)
            {
                BroadcastWorldSnapshots();
            }

            nextSimulationTick += simulationInterval;
            processedTicks++;
        }

        if (processedTicks == maximumCatchUpTicks && elapsed >= nextSimulationTick)
        {
            logger.LogWarning(
                "[WORLDSERVER] Movement simulation exceeded its catch-up budget at server tick {ServerTick}. Resynchronizing the fixed-tick clock.",
                serverTick);
            nextSimulationTick = elapsed + simulationInterval;
        }
    }

    private void BroadcastWorldSnapshots()
    {
        var playerSnapshots = peers.Values
            .Where(context => context.Session is not null && context.Movement is not null)
            .OrderBy(context => context.Session!.CharacterId)
            .Select(context => new RealtimePlayerSnapshot(
                context.Session!.CharacterId.ToString("D"),
                context.Movement!.LastProcessedInputSequence,
                ToRealtimePlayerState(context.Movement.State)))
            .ToArray();
        if (playerSnapshots.Length == 0)
        {
            return;
        }

        unchecked
        {
            snapshotSequence++;
        }

        var chunkCount = (ushort)Math.Ceiling(
            playerSnapshots.Length / (double)RealtimeProtocol.MaximumSnapshotPlayersPerChunk);
        for (ushort chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var players = playerSnapshots
                .Skip(chunkIndex * RealtimeProtocol.MaximumSnapshotPlayersPerChunk)
                .Take(RealtimeProtocol.MaximumSnapshotPlayersPerChunk)
                .ToArray();
            var packet = RealtimeProtocol.EncodeWorldSnapshot(new RealtimeWorldSnapshot(
                snapshotSequence,
                serverTick,
                chunkIndex,
                chunkCount,
                players));

            foreach (var context in peers.Values)
            {
                if (context.Session is not null && TryGetCurrentPeer(context, out var peer))
                {
                    peer.Send(packet, DeliveryMethod.Unreliable);
                }
            }
        }
    }

    private PlayerMovementState? FindPreviousCharacterMovementState(
        PeerContext currentContext,
        Guid characterId)
    {
        return peers.Values
            .Where(context => context != currentContext
                && context.Session?.CharacterId == characterId
                && context.Movement is not null)
            .Select(context => (PlayerMovementState?)context.Movement!.State)
            .FirstOrDefault();
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

    private void DisconnectPreviousCharacterPeer(PeerContext currentContext, ActivePlayerSession session)
    {
        foreach (var previousContext in peers.Values.Where(context =>
                     context != currentContext
                     && context.Session?.CharacterId == session.CharacterId))
        {
            if (TryGetCurrentPeer(previousContext, out var previousPeer))
            {
                previousPeer.Send(
                    RealtimeProtocol.EncodeServerDisconnect(
                        "session_reconnected",
                        "This character connected from another client."),
                    DeliveryMethod.ReliableOrdered);
                previousContext.Session = null;
                previousContext.Movement = null;
                previousContext.DisconnectAfterUtc = DateTime.UtcNow.AddMilliseconds(250);
            }
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
                    activeSession.WorldSessionId,
                    out var invalidation)
                    ? invalidation!
                    : new ActivePlayerSessionInvalidation(
                        "session_revoked",
                        "The world session is no longer active.");
                peer.Send(
                    RealtimeProtocol.EncodeServerDisconnect(
                        reason.Code,
                        reason.Message),
                    DeliveryMethod.ReliableOrdered);
                context.Session = null;
                context.Movement = null;
                context.DisconnectAfterUtc = now.AddMilliseconds(250);
            }
        }
    }

    private bool IsCurrentSession(ActivePlayerSession session)
    {
        return sessionStore.IsCurrent(session, DateTime.UtcNow);
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
            "[WORLDSERVER] Disconnecting UDP peer {PeerId} with code {Code}: {Message}",
            context.PeerId,
            code,
            message);
        peer.Send(
            RealtimeProtocol.EncodeServerDisconnect(code, message),
            DeliveryMethod.ReliableOrdered);
        context.DisconnectAfterUtc = DateTime.UtcNow.AddMilliseconds(250);
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

    private sealed class PeerContext(int peerId, DateTime connectedAtUtc)
    {
        public int PeerId { get; } = peerId;

        public DateTime ConnectedAtUtc { get; } = connectedAtUtc;

        public bool JoinStarted { get; set; }

        public bool LeaveStarted { get; set; }

        public ActivePlayerSession? Session { get; set; }

        public AuthoritativePlayerMovement? Movement { get; set; }

        public DateTime? DisconnectAfterUtc { get; set; }
    }

    private abstract record RealtimeOperationResult(PeerContext Context);

    private sealed record JoinCompleted(
        PeerContext PeerContext,
        WorldJoinResult<ActivePlayerSession> Result)
        : RealtimeOperationResult(PeerContext);

    private sealed record LeaveCompleted(
        PeerContext PeerContext,
        ActivePlayerSession Session,
        WorldSessionReleaseResult Result)
        : RealtimeOperationResult(PeerContext);
}
