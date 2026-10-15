using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using LiteNetLib;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.SimulationWorker.WorldCollision;

namespace Mmorpg.SimulationWorker.Realtime;

public sealed class RealtimeSimulationService : BackgroundService, IRealtimeCompletionHandler
{
    private readonly SimulationWorkerConfig config;
    private readonly SimulationJoinService joinService;
    private readonly SimulationSessionReleaseService releaseService;
    private readonly ActiveSimulationSessionStore sessionStore;
    private readonly CarryStateStore carryStateStore;
    private readonly SimulationItemInteractionService itemInteractionService;
    private readonly SimulationCorpseInteractionService corpseInteractionService;
    private readonly DurableCorpseStore corpseStore;
    private readonly CorpseViewerRegistry corpseViewers;
    private readonly SimulationEntityRegistry entityRegistry;
    private readonly ConnectionEntityBindingRegistry connectionBindings;
    private readonly RealtimeTransportReadiness transportReadiness;
    private readonly ChunkedStaticCollisionWorld staticCollisionWorld;
    private readonly ICollisionWorld collisionWorld;
    private readonly ILogger<RealtimeSimulationService> logger;
    private readonly WorldCollisionStreamingStore? collisionStreamingStore;
    private readonly SimulationWorkerRegistrationLease? registrationLease;
    private readonly SimulationWorkerIdentity? workerIdentity;
    private readonly RealtimeCompletionQueue completions = new();
    private readonly RealtimeOperationTracker operationTracker = new();
    private readonly Dictionary<Guid, DateTime> unboundSessionsSinceUtc = [];
    private PeerAdmissionGuard admissionGuard = null!;
    private DateTime nextOrphanSweepUtc = DateTime.MinValue;
    private readonly List<PlayerSimulationEntity> playerBuffer = [];
    private readonly List<SimulationVector3> collisionAnchorBuffer = [];
    private readonly List<SnapshotRecipient> snapshotRecipientBuffer = [];
    private readonly SnapshotPacketCache snapshotPacketCache = new();
    private readonly SnapshotRecipientRotation snapshotRecipientRotation = new();
    private readonly Dictionary<Guid, CorpseViewSnapshotResponse> corpseSnapshots = [];
    private readonly CorpseMutationCoordinator corpseMutationCoordinator = new();
    private readonly TokenBucket aggregateSnapshotBytes;
    private readonly RealtimeNetworkMetrics networkMetrics;
    private readonly RealtimePerformanceMetrics performanceMetrics;
    private readonly WorldActorStore? worldActorStore;
    private readonly WorldActorActivityScheduler? worldActorActivityScheduler;
    private readonly WorldInteractionLeaseRegistry? worldInteractionLeases;
    private readonly WorldInteractionAuthorityService? worldInteractionAuthority;
    private readonly LiveMobCorpseStore? liveMobCorpseStore;
    private readonly LiveMobCorpseInteractionService? liveMobCorpseInteractionService;
    private readonly CorpseRuntimeMetrics? corpseRuntimeMetrics;
    private readonly WorldActorMetrics? worldActorMetrics;
    private readonly RealtimePeerSender sender;
    private readonly PeerDirectory peerDirectory;
    private readonly RealtimePeerRegistry peerRegistry;
    private readonly RealtimeMessageDispatcher dispatcher;
    private readonly RealtimeCompletionProcessor completionProcessor;
    private readonly RealtimeSimulationClock clock = new();
    private readonly EntityPresenceReplicator presence;
    private readonly CarryStateSynchronizer carrySync;
    private readonly SessionAuthorityOperations sessionOperations;
    private readonly RealtimeSessionTeardown teardown;
    private NetManager? server;
    private uint snapshotSequence;
    private uint corpsePresenceSequence;

    public RealtimeSimulationService(
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
    {
        this.config = config;
        this.joinService = joinService;
        this.releaseService = releaseService;
        this.sessionStore = sessionStore;
        this.carryStateStore = carryStateStore;
        this.itemInteractionService = itemInteractionService;
        this.corpseInteractionService = corpseInteractionService;
        this.corpseStore = corpseStore;
        this.corpseViewers = corpseViewers;
        this.entityRegistry = entityRegistry;
        this.connectionBindings = connectionBindings;
        this.transportReadiness = transportReadiness;
        this.staticCollisionWorld = staticCollisionWorld;
        this.collisionWorld = collisionWorld;
        this.logger = logger;
        this.collisionStreamingStore = collisionStreamingStore;
        this.registrationLease = registrationLease;
        this.workerIdentity = workerIdentity;
        aggregateSnapshotBytes = new(
            config.UdpQuotas.AggregateSnapshotBytesPerSecond,
            config.UdpQuotas.AggregateSnapshotByteBurst);
        var interestManager =
            providedInterestManager ?? new SimulationInterestManager(config.InterestManagement);
        networkMetrics = providedNetworkMetrics ?? new RealtimeNetworkMetrics();
        performanceMetrics = providedPerformanceMetrics ?? new RealtimePerformanceMetrics();
        worldActorStore = providedWorldActorStore;
        worldActorActivityScheduler = providedWorldActorActivityScheduler;
        worldInteractionLeases = providedWorldInteractionLeases;
        worldInteractionAuthority = providedWorldInteractionAuthority;
        liveMobCorpseStore = providedLiveMobCorpseStore;
        liveMobCorpseInteractionService = providedLiveMobCorpseInteractionService;
        corpseRuntimeMetrics = providedCorpseRuntimeMetrics;
        worldActorMetrics = providedWorldActorMetrics;
        sender = new RealtimePeerSender(networkMetrics, logger);
        peerDirectory = new PeerDirectory(this);
        peerRegistry = new RealtimePeerRegistry(
            peerDirectory,
            connectionBindings,
            entityRegistry,
            networkMetrics);
        presence = new EntityPresenceReplicator(
            peerRegistry,
            sender,
            entityRegistry,
            connectionBindings,
            worldActorStore,
            interestManager,
            networkMetrics,
            clock);
        carrySync = new CarryStateSynchronizer(
            carryStateStore,
            connectionBindings,
            peerRegistry,
            sender);
        sessionOperations = new SessionAuthorityOperations(
            releaseService,
            operationTracker,
            logger);
        teardown = new RealtimeSessionTeardown(
            peerRegistry,
            sender,
            presence,
            sessionStore,
            carryStateStore,
            corpseViewers,
            worldInteractionLeases,
            connectionBindings,
            entityRegistry,
            networkMetrics);
        dispatcher = new RealtimeMessageDispatcher(
            peerRegistry,
            sender,
            networkMetrics,
            logger,
            RealtimeMessageRoutes.CreateUnauthenticated(HandleJoinRequest),
            RealtimeMessageRoutes.CreateAuthenticated(
                HandleMovementInput,
                HandleItemOperationIntent,
                HandleCorpseInteractionIntent,
                HandleWorldInteractionIntent,
                HandleLeaveRequest));
        completionProcessor = new RealtimeCompletionProcessor(
            completions,
            peerRegistry,
            sender,
            logger);
    }

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
            presence.RebuildInterestIndex(Array.Empty<PlayerSimulationEntity>());
            networkMetrics.SetActiveEntities(presence.ActiveEntityCount);
            logger.LogInformation(
                "[SIMULATION] Activated {ActorCount} world actors from revision {WorldActorRevision} for worker runtime {RuntimeId}.",
                actors.Count,
                worldActorStore.ContentRevision,
                workerIdentity.RuntimeId);
        }

        admissionGuard = new PeerAdmissionGuard(config.Admission);
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

            var admissionRejection = admissionGuard.TryAdmit(request.RemoteEndPoint.Address);
            if (admissionRejection is not null)
            {
                networkMetrics.RecordConnectionRejected();
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
                    completions.Count);
                performanceMetrics.ObserveCorpseMutationCoordinator(
                    corpseMutationCoordinator.WaitingMutationCount,
                    corpseMutationCoordinator.ActiveCorpseCount);
                completionProcessor.ProcessCompleted(completedOperationBudget);
                performanceMetrics.ObserveCompletedOperationBacklog(
                    completions.Count);
                performanceMetrics.ObserveCorpseMutationCoordinator(
                    corpseMutationCoordinator.WaitingMutationCount,
                    corpseMutationCoordinator.ActiveCorpseCount);
                performanceMetrics.RecordCompletedOperations(
                    Stopwatch.GetElapsedTime(phaseStarted));
                ProcessSimulationTicks(
                    simulationClock.Elapsed,
                    simulationInterval,
                    ref nextSimulationTick);
                EnforcePeerState();
                ReleaseOrphanedSessions();
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
            peerRegistry.Clear();
            presence.Clear();
            connectionBindings.Clear();
            entityRegistry.Clear();
            carryStateStore.Clear();
            corpseViewers.Clear();
            worldInteractionLeases?.Clear();
            worldActorStore?.Clear();
            corpseSnapshots.Clear();
            unboundSessionsSinceUtc.Clear();
            networkMetrics.SetActivePeers(0);
            networkMetrics.SetActiveEntities(0);
            networkMetrics.SetPeerPopulation(0, 0, 0);
            await operationTracker.WaitForAllAsync();
            logger.LogInformation("[SIMULATION] Realtime transport stopped.");
        }
    }

    private void HandlePeerConnected(NetPeer netPeer)
    {
        peerRegistry.Register(new RealtimePeerContext(netPeer.Id, DateTime.UtcNow, config.UdpQuotas));
        admissionGuard.OnPeerConnected(netPeer.Id, netPeer.Address);
        networkMetrics.SetActivePeers(peerRegistry.Count);
        peerRegistry.RefreshPopulationMetrics();
        logger.LogDebug(
            "[SIMULATION] UDP peer {PeerId} connected from {EndPoint} and must authenticate before joining.",
            netPeer.Id,
            netPeer.Address);
    }

    private void HandlePeerDisconnected(NetPeer netPeer, DisconnectInfo disconnectInfo)
    {
        admissionGuard.OnPeerDisconnected(netPeer.Id);
        if (!peerRegistry.Remove(netPeer.Id, out var context))
        {
            return;
        }

        presence.ForgetConnection(netPeer.Id);
        corpseViewers.RemovePeer(netPeer.Id);
        worldInteractionLeases?.RemovePeer(netPeer.Id);
        networkMetrics.SetActivePeers(peerRegistry.Count);
        peerRegistry.RefreshPopulationMetrics();

        if (context.Session is not null)
        {
            if (sessionStore.IsSupersededByReconnect(context.Session))
            {
                teardown.DetachSupersededPeer(context);
            }
            else
            {
                teardown.RemoveBoundEntity(context, context.Session, "connection_closed");
                sessionOperations.ReleaseDetachedInBackground(context.Session);
            }
        }

        logger.LogDebug(
            "[SIMULATION] UDP peer {PeerId} disconnected: {Reason}.",
            netPeer.Id,
            disconnectInfo.Reason);
    }

    private void HandleNetworkReceive(
        NetPeer netPeer,
        NetPacketReader reader,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        try
        {
            dispatcher.Dispatch(
                LiteNetLibPeerConnection.For(netPeer),
                reader.GetRemainingBytes(),
                channel,
                deliveryMethod);
        }
        finally
        {
            reader.Recycle();
        }
    }

    private void HandleJoinRequest(in RealtimeInboundMessage message)
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
        operationTracker.Track(CompleteJoinAsync(context, joinTicket));
    }

    private void HandleMovementInput(in RealtimeInboundMessage message)
    {
        var peer = message.Peer;
        var context = message.Context;
        var packet = message.Packet;
        if (!peerRegistry.TryGetBoundPlayer(context, out var playerEntity))
        {
            sender.RejectProtocol(
                peer,
                context,
                "movement_unavailable",
                "Movement simulation is not active for this peer.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeMovementInputBatch(packet, out var inputs, out var movementError))
        {
            sender.RejectProtocol(
                peer,
                context,
                "invalid_movement_input",
                movementError);
            return;
        }

        playerEntity.Movement.AcceptInputs(inputs);
    }

    private void HandleItemOperationIntent(in RealtimeInboundMessage message)
    {
        var peer = message.Peer;
        var context = message.Context;
        var packet = message.Packet;
        if (!peerRegistry.TryGetBoundPlayer(context, out var playerEntity))
        {
            sender.RejectProtocol(
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
            sender.RejectProtocol(
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
            sender.RejectProtocol(
                peer,
                context,
                "invalid_item_operation",
                itemError);
            return;
        }

        if (context.AuthorityOperationQueue.Count
            >= RealtimePeerContext.MaximumQueuedAuthorityOperations)
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
    }

    private void HandleCorpseInteractionIntent(in RealtimeInboundMessage message)
    {
        var peer = message.Peer;
        var context = message.Context;
        var packet = message.Packet;
        if (!peerRegistry.TryGetBoundPlayer(context, out var playerEntity))
        {
            sender.RejectProtocol(
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
            sender.RejectProtocol(
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
            sender.RejectProtocol(
                peer,
                context,
                "invalid_corpse_operation",
                corpseError);
            return;
        }

        if (context.AuthorityOperationQueue.Count
            >= RealtimePeerContext.MaximumQueuedAuthorityOperations)
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
    }

    private void HandleWorldInteractionIntent(in RealtimeInboundMessage message)
    {
        var peer = message.Peer;
        var context = message.Context;
        var packet = message.Packet;
        if (worldInteractionAuthority is null
            || !peerRegistry.TryGetBoundPlayer(context, out _))
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

        context.AuthorityOperationQueue.Enqueue(
            new QueuedWorldInteractionOperation(intent));
        StartNextAuthorityOperation(context);
    }

    private void HandleLeaveRequest(in RealtimeInboundMessage message)
    {
        var peer = message.Peer;
        var context = message.Context;
        var packet = message.Packet;
        if (!RealtimeProtocol.TryDecodeLeaveRequest(packet, out var simulationSessionId, out var error)
            || !Guid.TryParse(simulationSessionId, out var parsedSessionId))
        {
            sender.SendControl(peer, RealtimeProtocol.EncodeLeaveRejected("invalid_leave_request", error));
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
        operationTracker.Track(CompleteLeaveAsync(context, context.Session));
    }

    private async Task CompleteJoinAsync(RealtimePeerContext context, string joinTicket)
    {
        try
        {
            var result = await joinService.JoinAsync(joinTicket, CancellationToken.None);
            completions.Post(this, new JoinCompleted(
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
            completions.Post(this, new JoinCompleted(
                context,
                SimulationJoinResult<ActiveSimulationSession>.Failure(
                    500,
                    "join_failed",
                    "SimulationWorker could not complete the join request."),
                Stopwatch.GetTimestamp()));
        }
    }

    private async Task CompleteLeaveAsync(RealtimePeerContext context, ActiveSimulationSession session)
    {
        try
        {
            var result = await releaseService.ReleaseAsync(session, CancellationToken.None);
            completions.Post(this, new LeaveCompleted(context, session, result));
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while releasing UDP peer {PeerId}.",
                context.PeerId);
            completions.Post(this, new LeaveCompleted(
                context,
                session,
                SimulationSessionReleaseResult.Failure(
                    "leave_failed",
                    "SimulationWorker could not complete the leave request.")));
        }
    }

    private async Task CompleteItemOperationAsync(
        RealtimePeerContext context,
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
            completions.Post(this, new ItemOperationCompleted(
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
            completions.Post(this, new ItemOperationCompleted(
                context,
                session,
                SimulationItemInteractionResult.Rejected(
                    intent,
                    "item_operation_failed",
                    "SimulationWorker could not complete the item operation.")));
        }
    }

    private async Task CompleteCorpseOperationAsync(
        RealtimePeerContext context,
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
                    await CompleteCorpseMutationAsync(
                        interactionService,
                        session,
                        intent,
                        isLiveMobCorpse),
                _ => throw new InvalidOperationException(
                    "The queued corpse operation is not supported by GameService.")
            };
            completions.Post(this, new CorpseOperationCompleted(
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
            completions.Post(this, new CorpseOperationCompleted(
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

    private async Task<SimulationCorpseInteractionResult> CompleteCorpseMutationAsync(
        LiveMobCorpseInteractionService? interactionService,
        ActiveSimulationSession session,
        RealtimeCorpseInteractionIntent intent,
        bool isLiveMobCorpse)
    {
        using var mutationLease = await corpseMutationCoordinator.AcquireAsync(
            intent.CorpseId,
            CancellationToken.None);
        return isLiveMobCorpse
            ? await interactionService!.MutateAsync(
                session,
                intent,
                CancellationToken.None)
            : await corpseInteractionService.MutateAsync(
                session,
                intent,
                CancellationToken.None);
    }

    void IRealtimeCompletionHandler.Complete(RealtimeOperationResult operation)
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
    }

    void IRealtimeCompletionHandler.OnCompletionFailed(RealtimeOperationResult operation)
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
        if (!peerRegistry.TryGetCurrentPeer(completed.Context, out var peer))
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

        if (!carryStateStore.TryGet(
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
                collisionWorld,
                Math.Max(
                    1,
                    (int)Math.Ceiling(
                        config.MovementInputSilenceTimeout.TotalSeconds
                        * config.MovementSimulation.TickRateHz))));
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
        peerRegistry.RefreshPopulationMetrics();
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
                RealtimeStateMapper.ToRealtimePlayerState(entity.Movement.State))));
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
        if (!peerRegistry.TryGetCurrentPeer(completed.Context, out var peer))
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
        peerRegistry.RefreshPopulationMetrics();
        sender.SendControl(peer, RealtimeProtocol.EncodeLeaveAccepted());
        completed.Context.ScheduleDisconnect(DateTime.UtcNow);
    }

    private void ProcessItemOperationCompleted(ItemOperationCompleted completed)
    {
        completed.Context.AuthorityOperationInFlight = false;
        if (!peerRegistry.TryGetCurrentPeer(completed.Context, out var peer)
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
            teardown.TerminateForAuthorityFailure(
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
            teardown.TerminateForAuthorityFailure(
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
                teardown.TerminateForAuthorityFailure(
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
                carrySync.ApplyLatest(entity!);
            }
        }

        SendItemOperationResult(
            peer,
            completed.Session,
            completed.Result,
            requiresRefresh);
        StartNextAuthorityOperation(completed.Context);
    }

    private void StartNextAuthorityOperation(RealtimePeerContext context)
    {
        if (context.AuthorityOperationInFlight
            || context.AuthorityOperationQueue.Count == 0
            || context.Session is null
            || !peerRegistry.TryGetBoundPlayer(context, out var entity))
        {
            return;
        }

        var operation = context.AuthorityOperationQueue.Dequeue();
        var state = entity.Movement.State;
        context.AuthorityOperationInFlight = true;
        switch (operation)
        {
            case QueuedItemOperation itemOperation:
                operationTracker.Track(CompleteItemOperationAsync(
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
        RealtimePeerContext context,
        PlayerSimulationEntity entity,
        RealtimeWorldInteractionIntent intent)
    {
        if (worldInteractionAuthority is null
            || !peerRegistry.TryGetCurrentPeer(context, out _))
        {
            context.AuthorityOperationInFlight = false;
            return;
        }

        operationTracker.Track(CompleteWorldInteractionOperationAsync(
            context,
            entity,
            intent));
    }

    private async Task CompleteWorldInteractionOperationAsync(
        RealtimePeerContext context,
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

        completions.Post(this, new WorldInteractionOperationCompleted(
            context,
            entity.Session,
            result));
    }

    private void ProcessWorldInteractionOperationCompleted(
        WorldInteractionOperationCompleted completed)
    {
        completed.Context.AuthorityOperationInFlight = false;
        if (!peerRegistry.TryGetCurrentPeer(completed.Context, out var peer)
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
        StartNextAuthorityOperation(completed.Context);
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

    private void StartCorpseOperation(
        RealtimePeerContext context,
        ActiveSimulationSession session,
        RealtimeCorpseInteractionIntent intent,
        float positionX,
        float positionY,
        float positionZ)
    {
        if (intent.OperationKind == RealtimeCorpseInteractionKind.Close)
        {
            completions.Post(this, new CorpseOperationCompleted(
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
            completions.Post(this, new CorpseOperationCompleted(
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
            completions.Post(this, new CorpseOperationCompleted(
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
                completions.Post(this, new CorpseOperationCompleted(
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

                completions.Post(this, new CorpseOperationCompleted(
                    context,
                    session,
                    SimulationCorpseInteractionResult.Rejected(intent, code, message),
                    false));
                return;
            }

            operationTracker.Track(CompleteCorpseOperationAsync(
                context,
                session,
                intent,
                removeOpenReservationOnFailure: !alreadyViewing,
                isLiveMobCorpse));
            return;
        }

        if (!corpseViewers.IsViewing(context.PeerId, intent.CorpseId))
        {
            completions.Post(this, new CorpseOperationCompleted(
                context,
                session,
                SimulationCorpseInteractionResult.Rejected(
                    intent,
                    "corpse_view_not_open",
                    "Open the corpse before refreshing or looting it."),
                false));
            return;
        }

        operationTracker.Track(CompleteCorpseOperationAsync(
            context,
            session,
            intent,
            removeOpenReservationOnFailure: false,
            isLiveMobCorpse));
    }

    private void ProcessCorpseOperationCompleted(CorpseOperationCompleted completed)
    {
        completed.Context.AuthorityOperationInFlight = false;
        if (!peerRegistry.TryGetCurrentPeer(completed.Context, out var peer)
            || completed.Context.Session is null
            || completed.Context.Session.SimulationSessionId
                != completed.Session.SimulationSessionId
            || !string.Equals(
                completed.Context.Session.SimulationSessionToken,
                completed.Session.SimulationSessionToken,
                StringComparison.Ordinal))
        {
            // LiteNetLib reuses peer ids, so only a context that still owns its id may
            // release this open reservation. Every path that disconnects or detaches a
            // context has already released the reservations it held.
            if (completed.RemoveOpenReservationOnFailure && peerRegistry.OwnsPeerId(completed.Context))
            {
                corpseViewers.Close(
                    completed.Context.PeerId,
                    completed.Result.Intent.CorpseId);
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
            teardown.TerminateForAuthorityFailure(
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
            teardown.TerminateForAuthorityFailure(
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
            sender.SendControl(
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
        var resultSnapshot = completed.Result.Snapshot;
        if (resultSnapshot is null)
        {
            CloseCorpseForAll(
                completed.Result.Intent.CorpseId,
                "corpse_invalidated",
                "The corpse is no longer available.",
                removeFromStore: true);
            StartNextAuthorityOperation(completed.Context);
            return;
        }

        corpseSnapshots.TryGetValue(resultSnapshot.CorpseId, out var cachedSnapshot);
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

            corpseSnapshots[resultSnapshot.CorpseId] = resultSnapshot;
        }

        if (distribution.RequesterChunks.Count > 0)
        {
            SendCorpseState(peer, EncodeCorpseStatePackets(distribution.RequesterChunks));
        }

        if (distribution.ViewerChunks.Count > 0)
        {
            BroadcastCorpseState(
                resultSnapshot.CorpseId,
                EncodeCorpseStatePackets(distribution.ViewerChunks),
                distribution.RequesterReceivesViewerChunks ? null : completed.Context.PeerId);
        }

        StartNextAuthorityOperation(completed.Context);
    }

    private bool TryApplyCorpseCarryState(
        IRealtimePeerConnection peer,
        RealtimePeerContext context,
        ActiveSimulationSession session,
        SimulationItemTransactionResponse transaction)
    {
        if (transaction.CharacterRevisions.Count != 1)
        {
            teardown.TerminateForAuthorityFailure(
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
            teardown.TerminateForAuthorityFailure(
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
            carrySync.ApplyLatest(entity!);
        }

        return true;
    }

    private void SendCorpseOperationResult(
        IRealtimePeerConnection peer,
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
                    result.Succeeded
                        ? null!
                        : new RealtimeError(
                            result.Error!.Code,
                            result.Error.Message),
                    RealtimeStateMapper.ToRealtimeCarryState(carryState!))));
    }

    private void SendCorpseOperationRejection(
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

    private static IReadOnlyList<byte[]> EncodeCorpseStatePackets(
        IReadOnlyList<RealtimeCorpseViewStateChunk> chunks)
    {
        return chunks
            .Select(RealtimeProtocol.EncodeCorpseViewStateChunk)
            .ToArray();
    }

    private void SendCorpseState(IRealtimePeerConnection peer, IReadOnlyList<byte[]> packets)
    {
        foreach (var packet in packets)
        {
            sender.SendControl(peer, packet);
        }
    }

    private void BroadcastCorpseState(
        Guid corpseId,
        IReadOnlyList<byte[]> packets,
        int? excludedPeerId = null)
    {
        foreach (var viewerPeerId in corpseViewers.GetViewers(corpseId))
        {
            if (viewerPeerId == excludedPeerId
                || !peerRegistry.TryGetAuthenticatedPeer(viewerPeerId, out _, out var peer))
            {
                continue;
            }

            SendCorpseState(peer, packets);
        }
    }

    private void CloseCorpseForPeer(
        RealtimePeerContext context,
        IRealtimePeerConnection peer,
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

        sender.SendControl(
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
            if (peerRegistry.TryGetAuthenticatedPeer(viewerPeerId, out _, out var peer))
            {
                sender.SendControl(
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
        IRealtimePeerConnection peer,
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
            RealtimeStateMapper.ToRealtimeCarryState(carryState!),
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
        sender.SendControl(peer, RealtimeProtocol.EncodeItemOperationResult(packetResult));
    }

    private void SendItemOperationRejection(
        IRealtimePeerConnection peer,
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
            clock.Advance();

            var phaseStarted = Stopwatch.GetTimestamp();
            RefreshCollisionStreaming();
            performanceMetrics.RecordCollisionStreaming(
                Stopwatch.GetElapsedTime(phaseStarted));

            phaseStarted = Stopwatch.GetTimestamp();
            entityRegistry.CopyPlayersTo(playerBuffer);
            foreach (var entity in playerBuffer)
            {
                carrySync.ApplyLatest(entity);
                entity.Movement.SimulateTick();
            }
            performanceMetrics.RecordMovementSimulation(
                Stopwatch.GetElapsedTime(phaseStarted));

            if (worldActorActivityScheduler is not null)
            {
                foreach (var changedActor in worldActorActivityScheduler.Evaluate(
                             playerBuffer,
                             clock.ServerTick,
                             config.MovementSimulation.TickRateHz))
                {
                    presence.BroadcastWorldActorState(changedActor);
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
                && clock.ServerTick % interactionValidationIntervalTicks == 0)
            {
                foreach (var closure in worldInteractionAuthority.Revalidate(
                             peerRegistry.ResolvePlayerForInteraction))
                {
                    if (peerRegistry.TryGetAuthenticatedPeer(
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

            var snapshotIntervalTicks = config.MovementSimulation.TickRateHz / config.SnapshotRateHz;
            if (clock.ServerTick % snapshotIntervalTicks == 0)
            {
                BroadcastSimulationSnapshots();
            }

            var corpsePresenceIntervalTicks = Math.Max(
                1,
                config.MovementSimulation.TickRateHz / 2);
            if (clock.ServerTick % corpsePresenceIntervalTicks == 0)
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
                clock.ServerTick);
            performanceMetrics.RecordTickResynchronization();
            nextSimulationTick = elapsed + simulationInterval;
        }
    }

    private void BroadcastSimulationSnapshots()
    {
        var broadcastStarted = Stopwatch.GetTimestamp();
        var sentPacketCount = 0;
        var encodedOwnerPacketCount = 0;
        long deferredEntityUpdateCount = 0;
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
                    RealtimeStateMapper.ToRealtimePlayerState(entity.Movement.State)));
            }

            if (snapshotPacketCache.EntitySnapshotCount == 0)
            {
                return;
            }

            var interestStarted = Stopwatch.GetTimestamp();
            presence.RebuildInterestIndex(playerBuffer);
            presence.RefreshInterests(null);
            performanceMetrics.RecordInterestRefresh(
                Stopwatch.GetElapsedTime(interestStarted));

            unchecked
            {
                snapshotSequence++;
            }

            snapshotRecipientBuffer.Clear();
            foreach (var context in peerRegistry.Contexts)
            {
                if (context.Session is null
                    || !connectionBindings.TryGetEntityId(
                        context.PeerId,
                        out var controlledEntityId)
                    || !peerRegistry.TryGetCurrentPeer(context, out var peer))
                {
                    continue;
                }

                snapshotRecipientBuffer.Add(new SnapshotRecipient(
                    context,
                    peer,
                    controlledEntityId));
            }

            if (snapshotRecipientBuffer.Count == 0)
            {
                return;
            }

            var bytesPerRecipient = Math.Max(
                RealtimeProtocol.OwnerSimulationSnapshotPacketSize,
                config.UdpQuotas.AggregateSnapshotBytesPerSecond
                    / config.SnapshotRateHz
                    / snapshotRecipientBuffer.Count);

            foreach (var recipient in snapshotRecipientBuffer)
            {
                if (!snapshotPacketCache.TryGetSnapshot(
                        recipient.ControlledEntityId,
                        out var controlledSnapshot))
                {
                    continue;
                }

                var ownerPacket = RealtimeProtocol.EncodeOwnerSimulationSnapshot(
                    new RealtimeOwnerSimulationSnapshot(
                        snapshotSequence,
                        clock.ServerTick,
                        controlledSnapshot!.LastProcessedInputSequence,
                        controlledSnapshot.State));
                encodedOwnerPacketCount++;
                if (!aggregateSnapshotBytes.TryConsume(ownerPacket.Length))
                {
                    networkMetrics.RecordSnapshotBackpressureDropped(1);
                    continue;
                }

                if (!recipient.Context.Quota.TryConsumeSnapshot(ownerPacket.Length))
                {
                    networkMetrics.RecordSnapshotDropped(1);
                    continue;
                }

                sender.SendUnreliable(recipient.Peer, ownerPacket);
                sentPacketCount++;
                networkMetrics.RecordSnapshotEntities(1);
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
                    presence.GetVisibleOrdered(recipient.Context.PeerId),
                    snapshotSequence,
                    clock.ServerTick,
                    bytesPerRecipient,
                    config.SnapshotReplication.OverloadTargetUtilizationBasisPoints);
                deferredEntityUpdateCount += packetBatch.DeferredEntityCount;
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
                    sender.SendUnreliable(recipient.Peer, packet);
                    sentPacketCount++;
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
                snapshotPacketCache.EncodedPacketCount + encodedOwnerPacketCount,
                sentPacketCount,
                deferredEntityUpdateCount);
            performanceMetrics.RecordSnapshotBroadcast(
                Stopwatch.GetElapsedTime(broadcastStarted));
        }
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

        foreach (var context in peerRegistry.Contexts)
        {
            if (context.Session is null
                || !peerRegistry.TryGetBoundPlayer(context, out var entity)
                || !peerRegistry.TryGetCurrentPeer(context, out var peer))
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
                sender.SendControl(
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

    private void EnforcePeerState()
    {
        var now = DateTime.UtcNow;
        foreach (var context in peerRegistry.Contexts.ToArray())
        {
            if (!peerRegistry.TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            if (context.DisconnectAfterUtc is not null && context.DisconnectAfterUtc <= now)
            {
                peerDirectory.DisconnectPeer(peer);
                continue;
            }

            // A client that never acknowledges reliable packets would grow this queue
            // without bound, so it is disconnected without another reliable message.
            var queuedReliablePackets = peer.GetPacketsCountInReliableQueue(
                RealtimeProtocol.ControlChannel,
                true);
            if (queuedReliablePackets > config.Admission.MaxReliableQueuedPackets)
            {
                logger.LogWarning(
                    "[SIMULATION] Disconnecting UDP peer {PeerId} with {QueuedPackets} unacknowledged reliable packets.",
                    context.PeerId,
                    queuedReliablePackets);
                networkMetrics.RecordConnectionRejected();
                peerDirectory.DisconnectPeer(peer);
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
                    sender.RejectProtocol(peer, context, "join_timeout", "The join handshake timed out.");
                }

                continue;
            }

            var isCurrentSession = IsCurrentSession(activeSession);
            if (ShouldEnforceSessionInvalidation(context.LeaveStarted, isCurrentSession))
            {
                if (sessionStore.IsSupersededByReconnect(activeSession))
                {
                    // The reconnecting client owns the entity and carry state now.
                    sender.SendControl(
                        peer,
                        RealtimeProtocol.EncodeServerDisconnect(
                            "session_reconnected",
                            "This character connected from another client."));
                    teardown.DetachSupersededPeer(context);
                    context.ScheduleDisconnect(now);
                    continue;
                }

                var reason = sessionStore.TryTakeInvalidation(
                    activeSession.SimulationSessionId,
                    out var invalidation)
                    ? invalidation!
                    : new ActiveSimulationSessionInvalidation(
                        "session_revoked",
                        "The simulation session is no longer active.");
                sender.SendControl(
                    peer,
                    RealtimeProtocol.EncodeServerDisconnect(
                        reason.Code,
                        reason.Message));
                teardown.RemoveBoundEntity(context, activeSession, reason.Code);
                context.Session = null;
                peerRegistry.RefreshPopulationMetrics();
                context.ScheduleDisconnect(now);
            }
        }
    }

    // Safety net: a local session that no peer has owned for a while is released, so a
    // missed cleanup path can never keep renewing a lease for a player who is gone.
    private void ReleaseOrphanedSessions()
    {
        var now = DateTime.UtcNow;
        if (now < nextOrphanSweepUtc)
        {
            return;
        }

        nextOrphanSweepUtc = now + TimeSpan.FromSeconds(1);
        var boundSessionIds = peerRegistry.Contexts
            .Select(context => context.Session?.SimulationSessionId)
            .OfType<Guid>()
            .ToHashSet();
        var orphanThreshold = config.JoinHandshakeTimeout * 2;
        var activeSessionIds = new HashSet<Guid>();
        foreach (var session in sessionStore.ListActiveSessions())
        {
            activeSessionIds.Add(session.SimulationSessionId);
            if (boundSessionIds.Contains(session.SimulationSessionId))
            {
                unboundSessionsSinceUtc.Remove(session.SimulationSessionId);
                continue;
            }

            if (!unboundSessionsSinceUtc.TryGetValue(session.SimulationSessionId, out var since))
            {
                unboundSessionsSinceUtc[session.SimulationSessionId] = now;
                continue;
            }

            if (now - since < orphanThreshold)
            {
                continue;
            }

            unboundSessionsSinceUtc.Remove(session.SimulationSessionId);
            logger.LogWarning(
                "[SIMULATION] Releasing simulation session {SimulationSessionId} because no peer has owned it for {Seconds:F0} seconds.",
                session.SimulationSessionId,
                (now - since).TotalSeconds);
            sessionOperations.ReleaseDetachedInBackground(session);
        }

        foreach (var sessionId in unboundSessionsSinceUtc.Keys.ToArray())
        {
            if (!activeSessionIds.Contains(sessionId))
            {
                unboundSessionsSinceUtc.Remove(sessionId);
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

    private sealed class PeerDirectory(RealtimeSimulationService owner) : IRealtimePeerDirectory
    {
        public bool TryGetConnectedPeer(
            int peerId,
            [NotNullWhen(true)] out IRealtimePeerConnection? peer)
        {
            peer = null;
            if (owner.server is null)
            {
                return false;
            }

            var candidate = owner.server.GetPeerById(peerId) as NetPeer;
            if (candidate is null || candidate.ConnectionState != ConnectionState.Connected)
            {
                return false;
            }

            peer = LiteNetLibPeerConnection.For(candidate);
            return true;
        }

        public void DisconnectPeer(IRealtimePeerConnection peer)
        {
            owner.server!.DisconnectPeer(((LiteNetLibPeerConnection)peer).Peer);
        }
    }
}
