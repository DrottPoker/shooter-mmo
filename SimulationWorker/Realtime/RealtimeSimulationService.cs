using System.Collections.Concurrent;
using System.Diagnostics;
using LiteNetLib;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using SimulationWorker.Auth;
using SimulationWorker.Config;
using SimulationWorker.Corpses;
using SimulationWorker.Entities;
using SimulationWorker.Items;
using SimulationWorker.Registry;
using SimulationWorker.Sessions;
using SimulationWorker.WorldActors;
using SimulationWorker.WorldCollision;

namespace SimulationWorker.Realtime;

public sealed class RealtimeSimulationService(
    SimulationWorkerConfig config,
    SimulationJoinService joinService,
    SimulationSessionReleaseService releaseService,
    ActiveSimulationSessionStore sessionStore,
    CarryStateStore carryStateStore,
    SimulationItemInteractionService itemInteractionService,
    SimulationCorpseInteractionService corpseInteractionService,
    DurableCorpseStore corpseStore,
    CorpseViewerRegistry corpseViewers,
    SimulationEntityRegistry entityRegistry,
    ConnectionEntityBindingRegistry connectionBindings,
    RealtimeTransportReadiness transportReadiness,
    ChunkedStaticCollisionWorld staticCollisionWorld,
    ICollisionWorld collisionWorld,
    ILogger<RealtimeSimulationService> logger,
    WorldCollisionStreamingStore? collisionStreamingStore = null,
    SimulationInterestManager? providedInterestManager = null,
    RealtimeNetworkMetrics? providedNetworkMetrics = null,
    SimulationWorkerRegistrationLease? registrationLease = null,
    RealtimePerformanceMetrics? providedPerformanceMetrics = null,
    WorldActorStore? providedWorldActorStore = null,
    WorldActorActivityScheduler? providedWorldActorActivityScheduler = null,
    WorldInteractionLeaseRegistry? providedWorldInteractionLeases = null,
    WorldInteractionAuthorityService? providedWorldInteractionAuthority = null,
    WorldActorMetrics? providedWorldActorMetrics = null,
    SimulationWorkerIdentity? workerIdentity = null,
    LiveMobCorpseStore? providedLiveMobCorpseStore = null,
    LiveMobCorpseInteractionService? providedLiveMobCorpseInteractionService = null,
    CorpseRuntimeMetrics? providedCorpseRuntimeMetrics = null)
    : BackgroundService
{
    private readonly ConcurrentQueue<RealtimeOperationResult> completedOperations = new();
    private readonly Dictionary<int, PeerContext> peers = [];
    private readonly HashSet<Task> activeOperations = [];
    private readonly object activeOperationsLock = new();
    private readonly List<PlayerSimulationEntity> playerBuffer = [];
    private readonly List<WorldActorRuntimeState> worldActorBuffer = [];
    private readonly List<SimulationInterestEntity> interestEntityBuffer = [];
    private readonly List<SimulationVector3> collisionAnchorBuffer = [];
    private readonly List<SnapshotRecipient> snapshotRecipientBuffer = [];
    private readonly SnapshotPacketCache snapshotPacketCache = new();
    private readonly SnapshotRecipientRotation snapshotRecipientRotation = new();
    private readonly Dictionary<Guid, CorpseViewSnapshotResponse> corpseSnapshots = [];
    private readonly TokenBucket aggregateSnapshotBytes = new(
        config.UdpQuotas.AggregateSnapshotBytesPerSecond,
        config.UdpQuotas.AggregateSnapshotByteBurst);
    private readonly SimulationInterestManager interestManager =
        providedInterestManager ?? new SimulationInterestManager(config.InterestManagement);
    private readonly RealtimeNetworkMetrics networkMetrics =
        providedNetworkMetrics ?? new RealtimeNetworkMetrics();
    private readonly RealtimePerformanceMetrics performanceMetrics =
        providedPerformanceMetrics ?? new RealtimePerformanceMetrics();
    private readonly WorldActorStore? worldActorStore = providedWorldActorStore;
    private readonly WorldActorActivityScheduler? worldActorActivityScheduler =
        providedWorldActorActivityScheduler;
    private readonly WorldInteractionLeaseRegistry? worldInteractionLeases =
        providedWorldInteractionLeases;
    private readonly WorldInteractionAuthorityService? worldInteractionAuthority =
        providedWorldInteractionAuthority;
    private readonly LiveMobCorpseStore? liveMobCorpseStore = providedLiveMobCorpseStore;
    private readonly LiveMobCorpseInteractionService? liveMobCorpseInteractionService =
        providedLiveMobCorpseInteractionService;
    private readonly CorpseRuntimeMetrics? corpseRuntimeMetrics = providedCorpseRuntimeMetrics;
    private readonly WorldActorMetrics? worldActorMetrics = providedWorldActorMetrics;
    private NetManager? server;
    private uint serverTick;
    private uint snapshotSequence;
    private uint corpsePresenceSequence;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (worldActorStore is not null)
        {
            if (workerIdentity is null)
            {
                throw new InvalidOperationException(
                    "World actor runtime requires the current SimulationWorker identity.");
            }

            var actors = worldActorStore.ActivateAssignment(
                config.WorldId,
                config.ShardId,
                workerIdentity.RuntimeId);
            worldActorMetrics?.ObservePopulation(actors);
            RebuildInterestIndex(Array.Empty<PlayerSimulationEntity>());
            networkMetrics.SetActiveEntities(GetActiveEntityCount());
            logger.LogInformation(
                "[SIMULATION] Activated {ActorCount} world actors from revision {WorldActorRevision} for worker runtime {RuntimeId}.",
                actors.Count,
                worldActorStore.ContentRevision,
                workerIdentity.RuntimeId);
        }

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
        var completedOperationBudget = TimeSpan.FromTicks(
            Math.Max(1, simulationInterval.Ticks / 8));
        var nextSimulationTick = simulationClock.Elapsed + simulationInterval;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var phaseStarted = Stopwatch.GetTimestamp();
                server.PollEvents();
                performanceMetrics.RecordNetworkPoll(
                    Stopwatch.GetElapsedTime(phaseStarted));

                phaseStarted = Stopwatch.GetTimestamp();
                performanceMetrics.ObserveCompletedOperationBacklog(
                    completedOperations.Count);
                ProcessCompletedOperations(completedOperationBudget);
                performanceMetrics.ObserveCompletedOperationBacklog(
                    completedOperations.Count);
                performanceMetrics.RecordCompletedOperations(
                    Stopwatch.GetElapsedTime(phaseStarted));
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
            carryStateStore.Clear();
            corpseViewers.Clear();
            worldInteractionLeases?.Clear();
            worldActorStore?.Clear();
            corpseSnapshots.Clear();
            networkMetrics.SetActivePeers(0);
            networkMetrics.SetActiveEntities(0);
            networkMetrics.SetPeerPopulation(0, 0, 0);
            await AwaitActiveOperationsAsync();
            logger.LogInformation("[SIMULATION] Realtime transport stopped.");
        }
    }

    private void HandlePeerConnected(NetPeer peer)
    {
        peers[peer.Id] = new PeerContext(peer.Id, DateTime.UtcNow, config.UdpQuotas);
        networkMetrics.SetActivePeers(peers.Count);
        RefreshPeerPopulationMetrics();
        logger.LogDebug(
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
        worldInteractionLeases?.RemovePeer(peer.Id);
        networkMetrics.SetActivePeers(peers.Count);
        RefreshPeerPopulationMetrics();

        if (context.Session is not null)
        {
            RemoveBoundEntity(context, context.Session, "connection_closed");
            TrackOperation(ReleaseDisconnectedSessionAsync(context.Session));
        }

        logger.LogDebug(
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

        if (messageType == RealtimeMessageType.ItemOperationIntent)
        {
            if (channel != RealtimeProtocol.ControlChannel
                || deliveryMethod != DeliveryMethod.ReliableOrdered)
            {
                RejectProtocol(
                    peer,
                    context,
                    "invalid_item_operation_delivery",
                    "Item operation intents require the reliable ordered control channel.");
                return;
            }

            if (!TryGetBoundPlayer(context, out var playerEntity))
            {
                RejectProtocol(
                    peer,
                    context,
                    "item_operation_unavailable",
                    "Item interaction is not active for this peer.");
                return;
            }

            if (!carryStateStore.TryGet(
                    playerEntity.Session.CharacterId,
                    playerEntity.Session.SimulationSessionId,
                    out _))
            {
                RejectProtocol(
                    peer,
                    context,
                    "carry_state_missing",
                    "Authoritative carry state is unavailable for item interaction.");
                return;
            }

            if (!RealtimeProtocol.TryDecodeItemOperationIntent(
                    packet,
                    out var intent,
                    out var itemError))
            {
                RejectProtocol(
                    peer,
                    context,
                    "invalid_item_operation",
                    itemError);
                return;
            }

            if (context.AuthorityOperationQueue.Count
                >= PeerContext.MaximumQueuedAuthorityOperations)
            {
                SendItemOperationRejection(
                    peer,
                    playerEntity.Session,
                    intent,
                    "item_operation_queue_full",
                    "Too many item operations are waiting for authority.");
                return;
            }

            context.AuthorityOperationQueue.Enqueue(new QueuedItemOperation(intent));
            StartNextAuthorityOperation(context);
            return;
        }

        if (messageType == RealtimeMessageType.CorpseInteractionIntent)
        {
            if (channel != RealtimeProtocol.ControlChannel
                || deliveryMethod != DeliveryMethod.ReliableOrdered)
            {
                RejectProtocol(
                    peer,
                    context,
                    "invalid_corpse_operation_delivery",
                    "Corpse interaction intents require the reliable ordered control channel.");
                return;
            }

            if (!TryGetBoundPlayer(context, out var playerEntity))
            {
                RejectProtocol(
                    peer,
                    context,
                    "corpse_interaction_unavailable",
                    "Corpse interaction is not active for this peer.");
                return;
            }

            if (!carryStateStore.TryGet(
                    playerEntity.Session.CharacterId,
                    playerEntity.Session.SimulationSessionId,
                    out _))
            {
                RejectProtocol(
                    peer,
                    context,
                    "carry_state_missing",
                    "Authoritative carry state is unavailable for corpse interaction.");
                return;
            }

            if (!RealtimeProtocol.TryDecodeCorpseInteractionIntent(
                    packet,
                    out var intent,
                    out var corpseError))
            {
                RejectProtocol(
                    peer,
                    context,
                    "invalid_corpse_operation",
                    corpseError);
                return;
            }

            if (context.AuthorityOperationQueue.Count
                >= PeerContext.MaximumQueuedAuthorityOperations)
            {
                SendCorpseOperationRejection(
                    peer,
                    playerEntity.Session,
                    intent,
                    "item_operation_queue_full",
                    "Too many item or corpse operations are waiting for authority.");
                return;
            }

            context.AuthorityOperationQueue.Enqueue(new QueuedCorpseOperation(intent));
            StartNextAuthorityOperation(context);
            return;
        }

        if (messageType == RealtimeMessageType.WorldInteractionIntent)
        {
            if (channel != RealtimeProtocol.ControlChannel
                || deliveryMethod != DeliveryMethod.ReliableOrdered)
            {
                RejectProtocol(
                    peer,
                    context,
                    "invalid_world_interaction_delivery",
                    "World interaction intents require the reliable ordered control channel.");
                return;
            }

            if (worldInteractionAuthority is null
                || !TryGetBoundPlayer(context, out _))
            {
                RejectProtocol(
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
                RejectProtocol(
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
                >= PeerContext.MaximumQueuedAuthorityOperations)
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

            context.AuthorityOperationQueue.Enqueue(
                new QueuedWorldInteractionOperation(intent));
            StartNextAuthorityOperation(context);
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
            completedOperations.Enqueue(new JoinCompleted(
                context,
                result,
                Stopwatch.GetTimestamp()));
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
                    "SimulationWorker could not complete the join request."),
                Stopwatch.GetTimestamp()));
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

    private async Task CompleteItemOperationAsync(
        PeerContext context,
        ActiveSimulationSession session,
        RealtimeItemOperationIntent intent,
        float positionX,
        float positionY,
        float positionZ)
    {
        try
        {
            var result = await itemInteractionService.ExecuteAsync(
                session,
                intent,
                positionX,
                positionY,
                positionZ,
                CancellationToken.None);
            completedOperations.Enqueue(new ItemOperationCompleted(
                context,
                session,
                result));
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while mutating items for simulation session {SimulationSessionId}.",
                session.SimulationSessionId);
            completedOperations.Enqueue(new ItemOperationCompleted(
                context,
                session,
                SimulationItemInteractionResult.Rejected(
                    intent,
                    "item_operation_failed",
                    "SimulationWorker could not complete the item operation.")));
        }
    }

    private async Task CompleteCorpseOperationAsync(
        PeerContext context,
        ActiveSimulationSession session,
        RealtimeCorpseInteractionIntent intent,
        bool removeOpenReservationOnFailure,
        bool isLiveMobCorpse)
    {
        try
        {
            var interactionService = isLiveMobCorpse
                ? liveMobCorpseInteractionService
                    ?? throw new InvalidOperationException(
                        "Live Mob corpse interaction is not configured.")
                : null;
            var result = intent.OperationKind switch
            {
                RealtimeCorpseInteractionKind.Open
                    or RealtimeCorpseInteractionKind.Refresh =>
                    isLiveMobCorpse
                        ? await interactionService!.OpenAsync(
                            session,
                            intent,
                            CancellationToken.None)
                        : await corpseInteractionService.OpenAsync(
                            session,
                            intent,
                            CancellationToken.None),
                RealtimeCorpseInteractionKind.LootItem
                    or RealtimeCorpseInteractionKind.LootPartialStack
                    or RealtimeCorpseInteractionKind.DepositItem
                    or RealtimeCorpseInteractionKind.DepositPartialStack
                    or RealtimeCorpseInteractionKind.MoveItem
                    or RealtimeCorpseInteractionKind.MovePartialStack
                    or RealtimeCorpseInteractionKind.SwapBag =>
                    isLiveMobCorpse
                        ? await interactionService!.MutateAsync(
                            session,
                            intent,
                            CancellationToken.None)
                        : await corpseInteractionService.MutateAsync(
                            session,
                            intent,
                            CancellationToken.None),
                _ => throw new InvalidOperationException(
                    "The queued corpse operation is not supported by AuthService.")
            };
            completedOperations.Enqueue(new CorpseOperationCompleted(
                context,
                session,
                result,
                removeOpenReservationOnFailure));
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while interacting with corpse {CorpseId} for simulation session {SimulationSessionId}.",
                intent.CorpseId,
                session.SimulationSessionId);
            completedOperations.Enqueue(new CorpseOperationCompleted(
                context,
                session,
                SimulationCorpseInteractionResult.Rejected(
                    intent,
                    "corpse_operation_failed",
                    "SimulationWorker could not complete the corpse operation.",
                    requiresCorpseRefresh: true),
                removeOpenReservationOnFailure));
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

    private void ProcessCompletedOperations(TimeSpan budget)
    {
        var started = Stopwatch.GetTimestamp();
        while (completedOperations.TryDequeue(out var operation))
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
                case ItemOperationCompleted itemCompleted:
                    ProcessItemOperationCompleted(itemCompleted);
                    break;
                case CorpseOperationCompleted corpseCompleted:
                    ProcessCorpseOperationCompleted(corpseCompleted);
                    break;
                case WorldInteractionOperationCompleted worldInteractionCompleted:
                    ProcessWorldInteractionOperationCompleted(worldInteractionCompleted);
                    break;
            }

            if (Stopwatch.GetElapsedTime(started) >= budget)
            {
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

        if (!carryStateStore.TryGet(
                session.CharacterId,
                session.SimulationSessionId,
                out var currentCarryState))
        {
            RejectProtocol(
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
        entity.Movement.ApplyCarryState(session.CarryState);
        var binding = connectionBindings.Bind(
            completed.Context.PeerId,
            entity.NetworkEntityId);
        completed.Context.Session = session;
        RefreshPeerPopulationMetrics();
        DisconnectReplacedConnection(binding.ReplacedConnectionId);
        var response = ActiveSimulationSessionResponse.FromSession(session);
        IReadOnlyList<int> connectionsEnteringNewEntity;
        var interestEntity = ToInterestEntity(entity);
        if (registration.IsNewEntity)
        {
            connectionsEnteringNewEntity = interestManager.AddEntity(interestEntity);
        }
        else
        {
            interestManager.UpdateEntity(interestEntity);
            connectionsEnteringNewEntity = Array.Empty<int>();
        }

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
                ToRealtimeCarryState(entity.Movement.CarryState),
                ToRealtimeMovementSettings(),
                ToRealtimePlayerState(entity.Movement.State))));
        SendEntityBaseline(peer, joiningInterest.Visible);
        SendEntitySpawnToConnections(entity, connectionsEnteringNewEntity);
        networkMetrics.RecordJoinAccepted();
        networkMetrics.SetActiveEntities(GetActiveEntityCount());

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
        RemoveBoundEntity(completed.Context, completed.Session, "left_shard");
        completed.Context.Session = null;
        RefreshPeerPopulationMetrics();
        SendControl(peer, RealtimeProtocol.EncodeLeaveAccepted());
        completed.Context.DisconnectAfterUtc = DateTime.UtcNow.AddMilliseconds(250);
    }

    private void ProcessItemOperationCompleted(ItemOperationCompleted completed)
    {
        completed.Context.AuthorityOperationInFlight = false;
        if (!TryGetCurrentPeer(completed.Context, out var peer)
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

        if (!carryStateStore.TryGet(
                completed.Session.CharacterId,
                completed.Session.SimulationSessionId,
                out _))
        {
            DisconnectForItemAuthorityFailure(
                peer,
                completed.Context,
                completed.Session,
                "carry_state_missing",
                "Authoritative carry state disappeared during the item operation.");
            return;
        }

        if (completed.Result.ShouldDisconnect)
        {
            SendItemOperationResult(
                peer,
                completed.Session,
                completed.Result,
                requiresInventoryRefresh: true);
            DisconnectForItemAuthorityFailure(
                peer,
                completed.Context,
                completed.Session,
                completed.Result.Error!.Code,
                completed.Result.Error.Message);
            return;
        }

        var requiresRefresh = completed.Result.RequiresInventoryRefresh;
        if (completed.Result.Succeeded)
        {
            var characterRevision = completed.Result.Transaction!.CharacterRevisions.Single();
            var committedCarry = new PlayerCarryState(
                characterRevision.Revision,
                characterRevision.CarriedWeight,
                characterRevision.CarryCapacity);
            var carryResult = carryStateStore.ApplyCommitted(
                completed.Session.CharacterId,
                completed.Session.SimulationSessionId,
                committedCarry,
                out var currentCarry);
            if (carryResult is CarryStateApplyResult.Conflict
                or CarryStateApplyResult.SessionMismatch)
            {
                DisconnectForItemAuthorityFailure(
                    peer,
                    completed.Context,
                    completed.Session,
                    "item_state_diverged",
                    "Committed item and carry revisions diverged from the active simulation state.");
                return;
            }

            requiresRefresh |= carryResult == CarryStateApplyResult.Stale;
            sessionStore.RefreshCarryState(
                completed.Session.CharacterId,
                completed.Session.SimulationSessionId,
                completed.Session.SimulationSessionToken,
                currentCarry);
            if (entityRegistry.TryGetPlayerByCharacterId(
                    completed.Session.CharacterId,
                    out var entity))
            {
                ApplyLatestCarryState(entity!);
            }
        }

        SendItemOperationResult(
            peer,
            completed.Session,
            completed.Result,
            requiresRefresh);
        StartNextAuthorityOperation(completed.Context);
    }

    private void StartNextAuthorityOperation(PeerContext context)
    {
        if (context.AuthorityOperationInFlight
            || context.AuthorityOperationQueue.Count == 0
            || context.Session is null
            || !TryGetBoundPlayer(context, out var entity))
        {
            return;
        }

        var operation = context.AuthorityOperationQueue.Dequeue();
        var state = entity.Movement.State;
        context.AuthorityOperationInFlight = true;
        switch (operation)
        {
            case QueuedItemOperation itemOperation:
                TrackOperation(CompleteItemOperationAsync(
                    context,
                    context.Session,
                    itemOperation.Intent,
                    state.PositionX,
                    state.PositionY,
                    state.PositionZ));
                break;
            case QueuedCorpseOperation corpseOperation:
                StartCorpseOperation(
                    context,
                    context.Session,
                    corpseOperation.Intent,
                    state.PositionX,
                    state.PositionY,
                    state.PositionZ);
                break;
            case QueuedWorldInteractionOperation worldInteractionOperation:
                ProcessWorldInteractionOperation(
                    context,
                    entity,
                    worldInteractionOperation.Intent);
                break;
            default:
                throw new InvalidOperationException(
                    "The queued authority operation type is not supported.");
        }
    }

    private void ProcessWorldInteractionOperation(
        PeerContext context,
        PlayerSimulationEntity entity,
        RealtimeWorldInteractionIntent intent)
    {
        if (worldInteractionAuthority is null
            || !TryGetCurrentPeer(context, out _))
        {
            context.AuthorityOperationInFlight = false;
            return;
        }

        TrackOperation(CompleteWorldInteractionOperationAsync(
            context,
            entity,
            intent));
    }

    private async Task CompleteWorldInteractionOperationAsync(
        PeerContext context,
        PlayerSimulationEntity entity,
        RealtimeWorldInteractionIntent intent)
    {
        WorldInteractionAuthorityResult result;
        try
        {
            result = await worldInteractionAuthority!.ProcessAsync(
                context.PeerId,
                entity,
                intent,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while processing world interaction {OperationId}.",
                intent.OperationId);
            result = new WorldInteractionAuthorityResult(
                null,
                new RealtimeWorldInteractionResult(
                    intent.OperationId,
                    intent.InteractionSessionId,
                    intent.OperationKind,
                    false,
                    0,
                    new RealtimeError(
                        "world_interaction_failed",
                        "SimulationWorker could not complete the world interaction.")),
                null);
        }

        completedOperations.Enqueue(new WorldInteractionOperationCompleted(
            context,
            entity.Session,
            result));
    }

    private void ProcessWorldInteractionOperationCompleted(
        WorldInteractionOperationCompleted completed)
    {
        completed.Context.AuthorityOperationInFlight = false;
        if (!TryGetCurrentPeer(completed.Context, out var peer)
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
            DisconnectForItemAuthorityFailure(
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
            ApplyLatestCarryState(entity!);
        }

        SendWorldInteractionResult(peer, completed.Result);
        StartNextAuthorityOperation(completed.Context);
    }

    private void SendWorldInteractionResult(
        NetPeer peer,
        WorldInteractionAuthorityResult result)
    {
        if (result.Opened is not null)
        {
            SendControl(
                peer,
                RealtimeProtocol.EncodeWorldInteractionOpened(result.Opened));
        }

        if (result.Result is not null)
        {
            SendControl(
                peer,
                RealtimeProtocol.EncodeWorldInteractionResult(result.Result));
        }

        if (result.Closed is not null)
        {
            SendControl(
                peer,
                RealtimeProtocol.EncodeWorldInteractionClosed(result.Closed));
        }
    }

    private void StartCorpseOperation(
        PeerContext context,
        ActiveSimulationSession session,
        RealtimeCorpseInteractionIntent intent,
        float positionX,
        float positionY,
        float positionZ)
    {
        if (intent.OperationKind == RealtimeCorpseInteractionKind.Close)
        {
            completedOperations.Enqueue(new CorpseOperationCompleted(
                context,
                session,
                SimulationCorpseInteractionResult.Closed(intent),
                false));
            return;
        }

        LiveMobCorpseState? liveCorpse = null;
        var isLiveMobCorpse = liveMobCorpseStore is not null
            && liveMobCorpseStore.TryGetActive(intent.CorpseId, out liveCorpse);
        ICorpseRuntimePresence? corpse = isLiveMobCorpse
            ? liveCorpse
            : corpseStore.TryGetActive(intent.CorpseId, out var durableCorpse)
                ? durableCorpse
                : null;
        if (corpse is null)
        {
            completedOperations.Enqueue(new CorpseOperationCompleted(
                context,
                session,
                SimulationCorpseInteractionResult.Rejected(
                    intent,
                    "corpse_not_found",
                    "The corpse is no longer active on this Shard.",
                    shouldCloseView: true),
                false));
            return;
        }

        if (!IsWithinCorpseInteractionRange(
                corpse!,
                positionX,
                positionY,
                positionZ))
        {
            completedOperations.Enqueue(new CorpseOperationCompleted(
                context,
                session,
                SimulationCorpseInteractionResult.Rejected(
                    intent,
                    "corpse_out_of_range",
                    "Move closer to the corpse before interacting with it."),
                false));
            return;
        }

        if (intent.OperationKind == RealtimeCorpseInteractionKind.Open)
        {
            var acquiredInteractionLease = false;
            if (worldInteractionLeases is not null
                && !worldInteractionLeases.TryAcquireCorpse(
                    context.PeerId,
                    intent.CorpseId,
                    out acquiredInteractionLease,
                    out var leaseCode,
                    out var leaseMessage))
            {
                completedOperations.Enqueue(new CorpseOperationCompleted(
                    context,
                    session,
                    SimulationCorpseInteractionResult.Rejected(
                        intent,
                        leaseCode,
                        leaseMessage),
                    false));
                return;
            }

            var alreadyViewing = corpseViewers.IsViewing(
                context.PeerId,
                intent.CorpseId);
            if (!corpseViewers.TryOpen(
                    context.PeerId,
                    intent.CorpseId,
                    out var code,
                    out var message))
            {
                if (acquiredInteractionLease)
                {
                    worldInteractionLeases?.ReleaseCorpse(
                        context.PeerId,
                        intent.CorpseId);
                }

                completedOperations.Enqueue(new CorpseOperationCompleted(
                    context,
                    session,
                    SimulationCorpseInteractionResult.Rejected(intent, code, message),
                    false));
                return;
            }

            TrackOperation(CompleteCorpseOperationAsync(
                context,
                session,
                intent,
                removeOpenReservationOnFailure: !alreadyViewing,
                isLiveMobCorpse));
            return;
        }

        if (!corpseViewers.IsViewing(context.PeerId, intent.CorpseId))
        {
            completedOperations.Enqueue(new CorpseOperationCompleted(
                context,
                session,
                SimulationCorpseInteractionResult.Rejected(
                    intent,
                    "corpse_view_not_open",
                    "Open the corpse before refreshing or looting it."),
                false));
            return;
        }

        TrackOperation(CompleteCorpseOperationAsync(
            context,
            session,
            intent,
            removeOpenReservationOnFailure: false,
            isLiveMobCorpse));
    }

    private void ProcessCorpseOperationCompleted(CorpseOperationCompleted completed)
    {
        completed.Context.AuthorityOperationInFlight = false;
        if (!TryGetCurrentPeer(completed.Context, out var peer)
            || completed.Context.Session is null
            || completed.Context.Session.SimulationSessionId
                != completed.Session.SimulationSessionId
            || !string.Equals(
                completed.Context.Session.SimulationSessionToken,
                completed.Session.SimulationSessionToken,
                StringComparison.Ordinal))
        {
            if (completed.RemoveOpenReservationOnFailure)
            {
                corpseViewers.RemovePeer(completed.Context.PeerId);
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
            DisconnectForItemAuthorityFailure(
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
            DisconnectForItemAuthorityFailure(
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
                if (ShouldCloseCorpseForAll(completed.Result.Error!.Code))
                {
                    CloseCorpseForAll(
                        completed.Result.Intent.CorpseId,
                        completed.Result.Error.Code,
                        completed.Result.Error.Message,
                        removeFromStore: true);
                }
                else
                {
                    CloseCorpseForPeer(
                        completed.Context,
                        peer,
                        completed.Result.Intent.CorpseId,
                        completed.Result.Error.Code,
                        completed.Result.Error.Message);
                }
            }

            StartNextAuthorityOperation(completed.Context);
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
            SendControl(
                peer,
                RealtimeProtocol.EncodeCorpseViewClosed(new RealtimeCorpseViewClosed(
                    completed.Result.Intent.CorpseId,
                    "corpse_view_closed",
                    "The corpse view was closed.")));
            StartNextAuthorityOperation(completed.Context);
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
        var snapshot = completed.Result.Snapshot;
        if (snapshot is null)
        {
            CloseCorpseForAll(
                completed.Result.Intent.CorpseId,
                "corpse_invalidated",
                "The corpse is no longer available.",
                removeFromStore: true);
            StartNextAuthorityOperation(completed.Context);
            return;
        }

        if (corpseSnapshots.TryGetValue(snapshot.CorpseId, out var newerSnapshot)
            && newerSnapshot.Revision > snapshot.Revision)
        {
            snapshot = newerSnapshot;
        }

        if (liveMobCorpseStore is null
            || !liveMobCorpseStore.TryGetActive(snapshot.CorpseId, out _))
        {
            corpseStore.ApplySnapshot(snapshot);
        }
        IReadOnlyList<RealtimeCorpseViewStateChunk> chunks;
        if (completed.Result.Mutation is not null
            && corpseSnapshots.TryGetValue(snapshot.CorpseId, out var previousSnapshot))
        {
            chunks = CorpseRealtimePacketBuilder.BuildDelta(previousSnapshot, snapshot);
        }
        else
        {
            chunks = CorpseRealtimePacketBuilder.BuildSnapshot(snapshot);
        }

        corpseSnapshots[snapshot.CorpseId] = snapshot;
        BroadcastCorpseState(snapshot.CorpseId, chunks);
        StartNextAuthorityOperation(completed.Context);
    }

    private bool TryApplyCorpseCarryState(
        NetPeer peer,
        PeerContext context,
        ActiveSimulationSession session,
        SimulationItemTransactionResponse transaction)
    {
        if (transaction.CharacterRevisions.Count != 1)
        {
            DisconnectForItemAuthorityFailure(
                peer,
                context,
                session,
                "item_state_diverged",
                "The corpse transaction returned an invalid character revision set.");
            return false;
        }

        var characterRevision = transaction.CharacterRevisions[0];
        var committedCarry = new PlayerCarryState(
            characterRevision.Revision,
            characterRevision.CarriedWeight,
            characterRevision.CarryCapacity);
        var carryResult = carryStateStore.ApplyCommitted(
            session.CharacterId,
            session.SimulationSessionId,
            committedCarry,
            out var currentCarry);
        if (carryResult is CarryStateApplyResult.Conflict
            or CarryStateApplyResult.SessionMismatch)
        {
            DisconnectForItemAuthorityFailure(
                peer,
                context,
                session,
                "item_state_diverged",
                "Committed corpse transfer and carry revisions diverged from the active simulation state.");
            return false;
        }

        sessionStore.RefreshCarryState(
            session.CharacterId,
            session.SimulationSessionId,
            session.SimulationSessionToken,
            currentCarry);
        if (entityRegistry.TryGetPlayerByCharacterId(
                session.CharacterId,
                out var entity))
        {
            ApplyLatestCarryState(entity!);
        }

        return true;
    }

    private void SendCorpseOperationResult(
        NetPeer peer,
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

        SendControl(
            peer,
            RealtimeProtocol.EncodeCorpseInteractionResult(
                new RealtimeCorpseInteractionResult(
                    result.Intent.OperationId,
                    result.Intent.OperationKind,
                    result.Intent.CorpseId,
                    result.Succeeded,
                    result.RequiresCorpseRefresh,
                    result.RequiresInventoryRefresh,
                    result.Succeeded
                        ? null!
                        : new RealtimeError(
                            result.Error!.Code,
                            result.Error.Message),
                    ToRealtimeCarryState(carryState!))));
    }

    private void SendCorpseOperationRejection(
        NetPeer peer,
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

    private void BroadcastCorpseState(
        Guid corpseId,
        IReadOnlyList<RealtimeCorpseViewStateChunk> chunks)
    {
        foreach (var viewerPeerId in corpseViewers.GetViewers(corpseId))
        {
            if (!peers.TryGetValue(viewerPeerId, out var context)
                || context.Session is null
                || !TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            foreach (var chunk in chunks)
            {
                SendControl(peer, RealtimeProtocol.EncodeCorpseViewStateChunk(chunk));
            }
        }
    }

    private void CloseCorpseForPeer(
        PeerContext context,
        NetPeer peer,
        Guid corpseId,
        string code,
        string message)
    {
        var closed = corpseViewers.Close(context.PeerId, corpseId);
        worldInteractionLeases?.ReleaseCorpse(context.PeerId, corpseId);
        if (!closed)
        {
            return;
        }

        SendControl(
            peer,
            RealtimeProtocol.EncodeCorpseViewClosed(
                new RealtimeCorpseViewClosed(corpseId, code, message)));
    }

    private void CloseCorpseForAll(
        Guid corpseId,
        string code,
        string message,
        bool removeFromStore)
    {
        foreach (var viewerPeerId in corpseViewers.CloseCorpse(corpseId))
        {
            worldInteractionLeases?.ReleaseCorpse(viewerPeerId, corpseId);
            if (peers.TryGetValue(viewerPeerId, out var context)
                && context.Session is not null
                && TryGetCurrentPeer(context, out var peer))
            {
                SendControl(
                    peer,
                    RealtimeProtocol.EncodeCorpseViewClosed(
                        new RealtimeCorpseViewClosed(corpseId, code, message)));
            }
        }

        corpseSnapshots.Remove(corpseId);
        if (removeFromStore)
        {
            corpseStore.Remove(corpseId);
            liveMobCorpseStore?.Remove(corpseId);
        }
    }

    private static bool ShouldCloseCorpseForAll(string code)
    {
        return code is "corpse_expired"
            or "corpse_invalidated"
            or "corpse_not_found";
    }

    private bool IsWithinCorpseInteractionRange(
        ICorpseRuntimePresence corpse,
        float playerX,
        float playerY,
        float playerZ)
    {
        var deltaX = corpse.PositionX - playerX;
        var deltaY = corpse.PositionY - playerY;
        var deltaZ = corpse.PositionZ - playerZ;
        var radius = config.ItemInteraction.CorpseInteractionRadius;
        return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ)
            <= radius * radius;
    }

    private void SendItemOperationResult(
        NetPeer peer,
        ActiveSimulationSession session,
        SimulationItemInteractionResult result,
        bool requiresInventoryRefresh)
    {
        if (!carryStateStore.TryGet(
                session.CharacterId,
                session.SimulationSessionId,
                out var carryState))
        {
            return;
        }

        var transaction = result.Transaction;
        var packetResult = new RealtimeItemOperationResult(
            result.Intent.OperationId,
            result.Intent.OperationKind,
            result.Succeeded,
            requiresInventoryRefresh,
            result.Succeeded
                ? null
                : new RealtimeError(result.Error!.Code, result.Error.Message),
            ToRealtimeCarryState(carryState!),
            result.Succeeded
                ? transaction!.ItemRevisions
                    .Select(revision => new RealtimeItemRevision(
                        revision.ItemInstanceId,
                        revision.Revision))
                    .ToArray()
                : Array.Empty<RealtimeItemRevision>(),
            result.Succeeded
                ? transaction!.ContainerRevisions
                    .Select(revision => new RealtimeContainerRevision(
                        revision.ContainerId,
                        revision.Revision))
                    .ToArray()
                : Array.Empty<RealtimeContainerRevision>(),
            result.Succeeded
                ? transaction!.RecoveryDeliveryIds.ToArray()
                : Array.Empty<Guid>());
        SendControl(peer, RealtimeProtocol.EncodeItemOperationResult(packetResult));
    }

    private void SendItemOperationRejection(
        NetPeer peer,
        ActiveSimulationSession session,
        RealtimeItemOperationIntent intent,
        string code,
        string message)
    {
        SendItemOperationResult(
            peer,
            session,
            SimulationItemInteractionResult.Rejected(intent, code, message),
            requiresInventoryRefresh: false);
    }

    private void DisconnectForItemAuthorityFailure(
        NetPeer peer,
        PeerContext context,
        ActiveSimulationSession session,
        string code,
        string message)
    {
        SendControl(peer, RealtimeProtocol.EncodeServerDisconnect(code, message));
        sessionStore.Invalidate(
            session.CharacterId,
            session.SimulationSessionId,
            session.SimulationSessionToken,
            code,
            message);
        RemoveBoundEntity(context, session, code);
        carryStateStore.Remove(session.CharacterId, session.SimulationSessionId);
        context.AuthorityOperationQueue.Clear();
        corpseViewers.RemovePeer(context.PeerId);
        worldInteractionLeases?.RemovePeer(context.PeerId);
        context.Session = null;
        RefreshPeerPopulationMetrics();
        context.DisconnectAfterUtc = DateTime.UtcNow.AddMilliseconds(250);
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
            var tickStarted = Stopwatch.GetTimestamp();
            performanceMetrics.RecordSimulationTickLag(elapsed - nextSimulationTick);
            unchecked
            {
                serverTick++;
            }

            var phaseStarted = Stopwatch.GetTimestamp();
            RefreshCollisionStreaming();
            performanceMetrics.RecordCollisionStreaming(
                Stopwatch.GetElapsedTime(phaseStarted));

            phaseStarted = Stopwatch.GetTimestamp();
            entityRegistry.CopyPlayersTo(playerBuffer);
            foreach (var entity in playerBuffer)
            {
                ApplyLatestCarryState(entity);
                entity.Movement.SimulateTick();
            }
            performanceMetrics.RecordMovementSimulation(
                Stopwatch.GetElapsedTime(phaseStarted));

            if (worldActorActivityScheduler is not null)
            {
                foreach (var changedActor in worldActorActivityScheduler.Evaluate(
                             playerBuffer,
                             serverTick,
                             config.MovementSimulation.TickRateHz))
                {
                    BroadcastWorldActorState(changedActor);
                }

                if (worldActorStore is not null)
                {
                    worldActorMetrics?.ObservePopulation(worldActorStore.ListActors());
                }
            }

            var interactionValidationIntervalTicks = Math.Max(
                1,
                config.MovementSimulation.TickRateHz / 5);
            if (worldInteractionAuthority is not null
                && serverTick % interactionValidationIntervalTicks == 0)
            {
                foreach (var closure in worldInteractionAuthority.Revalidate(
                             ResolvePlayerForInteraction))
                {
                    if (peers.TryGetValue(closure.PeerId, out var context)
                        && context.Session is not null
                        && TryGetCurrentPeer(context, out var peer))
                    {
                        SendControl(
                            peer,
                            RealtimeProtocol.EncodeWorldInteractionClosed(
                                closure.Closed));
                    }
                }
            }

            var snapshotIntervalTicks = config.MovementSimulation.TickRateHz / config.SnapshotRateHz;
            if (serverTick % snapshotIntervalTicks == 0)
            {
                BroadcastSimulationSnapshots();
            }

            var corpsePresenceIntervalTicks = Math.Max(
                1,
                config.MovementSimulation.TickRateHz / 2);
            if (serverTick % corpsePresenceIntervalTicks == 0)
            {
                BroadcastCorpsePresence();
            }

            performanceMetrics.RecordSimulationTick(
                Stopwatch.GetElapsedTime(tickStarted));
            nextSimulationTick += simulationInterval;
            processedTicks++;
        }

        if (processedTicks == maximumCatchUpTicks && elapsed >= nextSimulationTick)
        {
            logger.LogWarning(
                "[SIMULATION] Movement simulation exceeded its catch-up budget at server tick {ServerTick}. Resynchronizing the fixed-tick clock.",
                serverTick);
            performanceMetrics.RecordTickResynchronization();
            nextSimulationTick = elapsed + simulationInterval;
        }
    }

    private void BroadcastSimulationSnapshots()
    {
        var broadcastStarted = Stopwatch.GetTimestamp();
        var sentPacketCount = 0;
        try
        {
            snapshotPacketCache.Reset();
            entityRegistry.CopyPlayersTo(playerBuffer);
            foreach (var entity in playerBuffer)
            {
                if (!connectionBindings.TryGetConnectionId(entity.NetworkEntityId, out _))
                {
                    continue;
                }

                snapshotPacketCache.Add(new RealtimeEntitySnapshot(
                    entity.NetworkEntityId,
                    entity.Movement.LastProcessedInputSequence,
                    ToRealtimePlayerState(entity.Movement.State)));
            }

            if (snapshotPacketCache.EntitySnapshotCount == 0)
            {
                return;
            }

            var interestStarted = Stopwatch.GetTimestamp();
            RebuildInterestIndex(playerBuffer);
            RefreshInterests(null);
            performanceMetrics.RecordInterestRefresh(
                Stopwatch.GetElapsedTime(interestStarted));

            unchecked
            {
                snapshotSequence++;
            }

            snapshotRecipientBuffer.Clear();
            foreach (var context in peers.Values)
            {
                if (context.Session is null
                    || !connectionBindings.TryGetEntityId(context.PeerId, out _)
                    || !TryGetCurrentPeer(context, out var peer))
                {
                    continue;
                }

                snapshotRecipientBuffer.Add(new SnapshotRecipient(context, peer));
            }

            if (snapshotRecipientBuffer.Count == 0)
            {
                return;
            }

            var recipientStart = snapshotRecipientRotation.Begin(
                snapshotRecipientBuffer.Count);
            var lastAdmittedRecipientOffset = -1;
            for (var recipientIndex = 0;
                recipientIndex < snapshotRecipientBuffer.Count;
                recipientIndex++)
            {
                var recipient = snapshotRecipientBuffer[
                    (recipientStart + recipientIndex) % snapshotRecipientBuffer.Count];
                var packetBatch = snapshotPacketCache.GetOrCreate(
                    interestManager.GetVisibleOrdered(recipient.Context.PeerId),
                    snapshotSequence,
                    serverTick);
                if (packetBatch.Packets.Count == 0)
                {
                    continue;
                }

                if (!aggregateSnapshotBytes.TryConsume(packetBatch.TotalBytes))
                {
                    networkMetrics.RecordSnapshotBackpressureDropped(
                        packetBatch.Packets.Count);
                    continue;
                }

                lastAdmittedRecipientOffset = recipientIndex;

                if (!recipient.Context.Quota.TryConsumeSnapshot(packetBatch.TotalBytes))
                {
                    networkMetrics.RecordSnapshotDropped(packetBatch.Packets.Count);
                    continue;
                }

                for (var packetIndex = 0; packetIndex < packetBatch.Packets.Count; packetIndex++)
                {
                    var packet = packetBatch.Packets[packetIndex];
                    recipient.Peer.Send(packet, DeliveryMethod.Unreliable);
                    sentPacketCount++;
                    networkMetrics.RecordSent(packet.Length);
                    networkMetrics.RecordSnapshotEntities(
                        packetBatch.EntityCounts[packetIndex]);
                }
            }


            snapshotRecipientRotation.Complete(
                snapshotRecipientBuffer.Count,
                lastAdmittedRecipientOffset);
        }
        finally
        {
            performanceMetrics.RecordSnapshotPacketReuse(
                snapshotPacketCache.VisibilityGroupCount,
                snapshotPacketCache.EncodedPacketCount,
                sentPacketCount);
            performanceMetrics.RecordSnapshotBroadcast(
                Stopwatch.GetElapsedTime(broadcastStarted));
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

    private static RealtimeCarryState ToRealtimeCarryState(PlayerCarryState carryState)
    {
        return new RealtimeCarryState(
            carryState.ItemStateRevision,
            carryState.CarriedWeight,
            carryState.CarryCapacity);
    }

    private void ApplyLatestCarryState(PlayerSimulationEntity entity)
    {
        if (!carryStateStore.TryGet(
                entity.Session.CharacterId,
                entity.Session.SimulationSessionId,
                out var carryState)
            || !entity.Movement.ApplyCarryState(carryState!))
        {
            return;
        }

        if (!connectionBindings.TryGetConnectionId(
                entity.NetworkEntityId,
                out var connectionId)
            || !peers.TryGetValue(connectionId, out var context)
            || context.Session?.SimulationSessionId != entity.Session.SimulationSessionId
            || !TryGetCurrentPeer(context, out var peer))
        {
            return;
        }

        SendControl(
            peer,
            RealtimeProtocol.EncodeCarryStateChanged(
                ToRealtimeCarryState(entity.Movement.CarryState)));
    }

    private void BroadcastCorpsePresence()
    {
        var durableCorpses = corpseStore.ListActive();
        var liveCorpses = liveMobCorpseStore?.ListActive()
            ?? Array.Empty<LiveMobCorpseState>();
        corpseRuntimeMetrics?.Observe(durableCorpses.Count, liveCorpses.Count);
        var activeCorpses = durableCorpses
            .Cast<ICorpseRuntimePresence>()
            .Concat(liveCorpses)
            .OrderBy(corpse => corpse.CreatedAt)
            .ThenBy(corpse => corpse.CorpseId)
            .ToArray();
        var activeCorpseIds = activeCorpses
            .Select(corpse => corpse.CorpseId)
            .ToHashSet();
        foreach (var viewedCorpseId in corpseSnapshots.Keys.ToArray())
        {
            if (!activeCorpseIds.Contains(viewedCorpseId))
            {
                CloseCorpseForAll(
                    viewedCorpseId,
                    "corpse_expired",
                    "The corpse expired and is no longer available.",
                    removeFromStore: true);
            }
        }

        unchecked
        {
            corpsePresenceSequence++;
        }

        foreach (var context in peers.Values)
        {
            if (context.Session is null
                || !TryGetBoundPlayer(context, out var entity)
                || !TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            var state = entity.Movement.State;
            var nearbyCorpses = activeCorpses
                .Where(corpse => IsWithinCorpseDiscoveryRange(
                    corpse,
                    state.PositionX,
                    state.PositionY,
                    state.PositionZ))
                .OrderBy(corpse => corpse.CorpseId)
                .ToArray();
            var signature = CreateCorpsePresenceSignature(nearbyCorpses);
            if (string.Equals(
                    context.CorpsePresenceSignature,
                    signature,
                    StringComparison.Ordinal))
            {
                continue;
            }

            context.CorpsePresenceSignature = signature;
            foreach (var chunk in CorpseRealtimePacketBuilder.BuildPresence(
                         corpsePresenceSequence,
                         nearbyCorpses))
            {
                SendControl(
                    peer,
                    RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(chunk));
            }
        }
    }

    private bool IsWithinCorpseDiscoveryRange(
        ICorpseRuntimePresence corpse,
        float playerX,
        float playerY,
        float playerZ)
    {
        var deltaX = corpse.PositionX - playerX;
        var deltaY = corpse.PositionY - playerY;
        var deltaZ = corpse.PositionZ - playerZ;
        var radius = config.ItemInteraction.CorpseDiscoveryRadius;
        return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ)
            <= radius * radius;
    }

    private static string CreateCorpsePresenceSignature(
        IReadOnlyList<ICorpseRuntimePresence> corpses)
    {
        return string.Join(
            '|',
            corpses.Select(corpse =>
                $"{corpse.CorpseId:N}:{corpse.Revision}:{corpse.IsEmpty}"));
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
            previousContext.AuthorityOperationQueue.Clear();
            corpseViewers.RemovePeer(previousContext.PeerId);
            worldInteractionLeases?.RemovePeer(previousContext.PeerId);
            RefreshPeerPopulationMetrics();
            previousContext.DisconnectAfterUtc = DateTime.UtcNow.AddMilliseconds(250);
        }

        interestManager.RemoveConnection(replacedConnectionId.Value);
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

            var isCurrentSession = IsCurrentSession(activeSession);
            if (ShouldEnforceSessionInvalidation(context.LeaveStarted, isCurrentSession))
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
                RefreshPeerPopulationMetrics();
                context.DisconnectAfterUtc = now.AddMilliseconds(250);
            }
        }
    }

    internal static bool ShouldEnforceSessionInvalidation(
        bool leaveStarted,
        bool isCurrentSession)
    {
        // Leave completion owns cleanup after the release operation removes the local lease.
        return !leaveStarted && !isCurrentSession;
    }

    private bool IsCurrentSession(ActiveSimulationSession session)
    {
        return sessionStore.IsCurrent(session, DateTime.UtcNow);
    }

    private void RefreshPeerPopulationMetrics()
    {
        var activeRealPlayers = 0;
        var activeSyntheticBots = 0;
        var unauthenticatedPeers = 0;
        foreach (var context in peers.Values)
        {
            if (context.Session is null)
            {
                unauthenticatedPeers++;
            }
            else if (context.Session.IsSyntheticBot)
            {
                activeSyntheticBots++;
            }
            else
            {
                activeRealPlayers++;
            }
        }

        networkMetrics.SetPeerPopulation(
            activeRealPlayers,
            activeSyntheticBots,
            unauthenticatedPeers);
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
        entityRegistry.CopyPlayersTo(playerBuffer);
        foreach (var entity in playerBuffer)
        {
            if (visibleEntityIds.Contains(entity.NetworkEntityId))
            {
                SendSpawn(peer, entity);
            }
        }

        if (worldActorStore is null)
        {
            return;
        }

        worldActorStore.CopyActorsTo(worldActorBuffer);
        foreach (var actor in worldActorBuffer)
        {
            if (visibleEntityIds.Contains(actor.NetworkEntityId))
            {
                SendWorldActorSpawn(peer, actor);
            }
        }
    }

    private void SendEntitySpawnToConnections(
        PlayerSimulationEntity entity,
        IReadOnlyList<int> connectionIds)
    {
        foreach (var connectionId in connectionIds)
        {
            if (peers.TryGetValue(connectionId, out var context)
                && context.Session is not null
                && TryGetCurrentPeer(context, out var peer))
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
                else if (worldActorStore is not null
                    && worldActorStore.TryGetByEntityId(
                        enteredEntityId,
                        out var enteredActor))
                {
                    SendWorldActorSpawn(peer, enteredActor!);
                }
            }

            foreach (var exitedEntityId in update.Exited)
            {
                if (worldActorStore is not null
                    && worldActorStore.TryGetByEntityId(
                        exitedEntityId,
                        out var exitedActor))
                {
                    SendWorldActorDespawn(
                        peer,
                        exitedActor!,
                        "out_of_interest");
                }
                else if (worldActorStore is not null
                    && worldActorStore.TryGetDespawnedByEntityId(
                        exitedEntityId,
                        out var despawnedActor))
                {
                    SendWorldActorDespawn(
                        peer,
                        despawnedActor!,
                        "despawned");
                }
                else
                {
                    SendDespawn(peer, exitedEntityId, "out_of_interest");
                }
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

        carryStateStore.Remove(session.CharacterId, session.SimulationSessionId);
        connectionBindings.UnbindConnection(context.PeerId, out _);
        interestManager.RemoveConnection(context.PeerId);
        BroadcastEntityDespawn(entityId, reason, context.PeerId);
        networkMetrics.SetActiveEntities(GetActiveEntityCount());
    }

    private void RemoveReplacedEntity(PlayerSimulationEntity replacedEntity)
    {
        carryStateStore.Remove(
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

    private RealtimeWorldActorSpawn ToRealtimeWorldActorSpawn(
        WorldActorRuntimeState actor)
    {
        return WorldActorProtocolMapper.ToSpawn(actor);
    }

    private static RealtimeWorldActorState ToRealtimeWorldActorState(
        WorldActorRuntimeState actor)
    {
        return WorldActorProtocolMapper.ToState(actor);
    }

    private static SimulationInterestEntity ToInterestEntity(
        PlayerSimulationEntity entity)
    {
        return new SimulationInterestEntity(
            entity.NetworkEntityId,
            entity.Movement.State.PositionX,
            entity.Movement.State.PositionZ);
    }

    private PlayerSimulationEntity? ResolvePlayerForInteraction(int peerId)
    {
        return peers.TryGetValue(peerId, out var context)
            && context.Session is not null
            && TryGetBoundPlayer(context, out var player)
            ? player
            : null;
    }

    private int GetActiveEntityCount()
    {
        return entityRegistry.PlayerCount + (worldActorStore?.Count ?? 0);
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

        entityRegistry.CopyPlayersTo(playerBuffer);
        collisionAnchorBuffer.Clear();
        foreach (var entity in playerBuffer)
        {
            collisionAnchorBuffer.Add(new SimulationVector3(
                entity.Movement.State.PositionX,
                entity.Movement.State.PositionY,
                entity.Movement.State.PositionZ));
        }

        collisionAnchorBuffer.Add(new SimulationVector3(
            config.MovementSpawn.X,
            config.MovementSpawn.Y,
            config.MovementSpawn.Z));
        collisionStreamingStore.Refresh(collisionAnchorBuffer);
    }

    private void RebuildInterestIndex(IReadOnlyList<PlayerSimulationEntity> players)
    {
        interestEntityBuffer.Clear();
        for (var index = 0; index < players.Count; index++)
        {
            var entity = players[index];
            interestEntityBuffer.Add(new SimulationInterestEntity(
                entity.NetworkEntityId,
                entity.Movement.State.PositionX,
                entity.Movement.State.PositionZ));
        }

        if (worldActorStore is not null)
        {
            worldActorStore.CopyActorsTo(worldActorBuffer);
            for (var index = 0; index < worldActorBuffer.Count; index++)
            {
                var actor = worldActorBuffer[index];
                interestEntityBuffer.Add(new SimulationInterestEntity(
                    actor.NetworkEntityId,
                    actor.PositionX,
                    actor.PositionZ));
            }
        }

        interestManager.Rebuild(interestEntityBuffer);
    }

    private void SendSpawn(NetPeer peer, PlayerSimulationEntity entity)
    {
        SendControl(peer, RealtimeProtocol.EncodeEntitySpawn(ToRealtimeEntitySpawn(entity)));
        networkMetrics.RecordSpawnPacket();
    }

    private void SendWorldActorSpawn(
        NetPeer peer,
        WorldActorRuntimeState actor)
    {
        SendControl(
            peer,
            RealtimeProtocol.EncodeWorldActorSpawn(
                ToRealtimeWorldActorSpawn(actor)));
        networkMetrics.RecordSpawnPacket();
    }

    private void SendWorldActorDespawn(
        NetPeer peer,
        WorldActorRuntimeState actor,
        string reason)
    {
        SendControl(
            peer,
            RealtimeProtocol.EncodeWorldActorDespawn(
                new RealtimeWorldActorDespawn(
                    actor.NetworkEntityId,
                    actor.RuntimeActorId,
                    reason)));
        networkMetrics.RecordDespawnPacket();
    }

    private void BroadcastWorldActorState(WorldActorRuntimeState actor)
    {
        var packet = RealtimeProtocol.EncodeWorldActorState(
            ToRealtimeWorldActorState(actor));
        foreach (var context in peers.Values)
        {
            if (context.Session is null
                || !interestManager.GetVisible(context.PeerId)
                    .Contains(actor.NetworkEntityId)
                || !TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            SendControl(peer, packet);
        }
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
        public const int MaximumQueuedAuthorityOperations = 8;

        public int PeerId { get; } = peerId;

        public DateTime ConnectedAtUtc { get; } = connectedAtUtc;

        public UdpPeerQuota Quota { get; } = new(quotaConfig);

        public WorldInteractionIntentLimiter WorldInteractionIntentQuota { get; } = new();

        public bool JoinStarted { get; set; }

        public bool LeaveStarted { get; set; }

        public ActiveSimulationSession? Session { get; set; }

        public Queue<QueuedAuthorityOperation> AuthorityOperationQueue { get; } = new();

        public bool AuthorityOperationInFlight { get; set; }

        public string? CorpsePresenceSignature { get; set; }

        public DateTime? DisconnectAfterUtc { get; set; }
    }

    private readonly record struct SnapshotRecipient(PeerContext Context, NetPeer Peer);

    private abstract record RealtimeOperationResult(PeerContext Context);

    private sealed record JoinCompleted(
        PeerContext PeerContext,
        SimulationJoinResult<ActiveSimulationSession> Result,
        long EnqueuedTimestamp)
        : RealtimeOperationResult(PeerContext);

    private sealed record LeaveCompleted(
        PeerContext PeerContext,
        ActiveSimulationSession Session,
        SimulationSessionReleaseResult Result)
        : RealtimeOperationResult(PeerContext);

    private sealed record ItemOperationCompleted(
        PeerContext PeerContext,
        ActiveSimulationSession Session,
        SimulationItemInteractionResult Result)
        : RealtimeOperationResult(PeerContext);

    private sealed record CorpseOperationCompleted(
        PeerContext PeerContext,
        ActiveSimulationSession Session,
        SimulationCorpseInteractionResult Result,
        bool RemoveOpenReservationOnFailure)
        : RealtimeOperationResult(PeerContext);

    private sealed record WorldInteractionOperationCompleted(
        PeerContext PeerContext,
        ActiveSimulationSession Session,
        WorldInteractionAuthorityResult Result)
        : RealtimeOperationResult(PeerContext);

    private abstract record QueuedAuthorityOperation;

    private sealed record QueuedItemOperation(RealtimeItemOperationIntent Intent)
        : QueuedAuthorityOperation;

    private sealed record QueuedCorpseOperation(RealtimeCorpseInteractionIntent Intent)
        : QueuedAuthorityOperation;

    private sealed record QueuedWorldInteractionOperation(
        RealtimeWorldInteractionIntent Intent)
        : QueuedAuthorityOperation;
}
