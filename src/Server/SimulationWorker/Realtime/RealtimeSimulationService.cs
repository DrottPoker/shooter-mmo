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

public sealed class RealtimeSimulationService : BackgroundService
{
    private readonly SimulationWorkerConfig config;
    private readonly ActiveSimulationSessionStore sessionStore;
    private readonly CarryStateStore carryStateStore;
    private readonly CorpseViewerRegistry corpseViewers;
    private readonly SimulationEntityRegistry entityRegistry;
    private readonly ConnectionEntityBindingRegistry connectionBindings;
    private readonly RealtimeTransportReadiness transportReadiness;
    private readonly ILogger<RealtimeSimulationService> logger;
    private readonly WorldCollisionStreamingStore? collisionStreamingStore;
    private readonly SimulationWorkerRegistrationLease? registrationLease;
    private readonly SimulationWorkerIdentity? workerIdentity;
    private readonly RealtimeCompletionQueue completions = new();
    private readonly RealtimeOperationTracker operationTracker = new();
    private readonly Dictionary<Guid, DateTime> unboundSessionsSinceUtc = [];
    private readonly PeerAdmissionGuard admissionGuard;
    private DateTime nextOrphanSweepUtc = DateTime.MinValue;
    private readonly List<PlayerSimulationEntity> playerBuffer = [];
    private readonly List<SimulationVector3> collisionAnchorBuffer = [];
    private readonly List<SnapshotRecipient> snapshotRecipientBuffer = [];
    private readonly SnapshotPacketCache snapshotPacketCache = new();
    private readonly SnapshotRecipientRotation snapshotRecipientRotation = new();
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
    private readonly AuthorityOperationScheduler authorityScheduler;
    private readonly ItemOperationHandler itemOperations;
    private readonly CorpseViewDistributor corpseViews;
    private readonly CorpsePresenceReplicator corpsePresence;
    private readonly CorpseAuthorityOperations corpseAuthority;
    private readonly CorpseInteractionHandler corpseInteractions;
    private readonly WorldInteractionHandler worldInteractions;
    private readonly RealtimeSessionHandler sessions;
    private NetManager? server;
    private uint snapshotSequence;

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
        this.sessionStore = sessionStore;
        this.carryStateStore = carryStateStore;
        this.corpseViewers = corpseViewers;
        this.entityRegistry = entityRegistry;
        this.connectionBindings = connectionBindings;
        this.transportReadiness = transportReadiness;
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
        admissionGuard = new PeerAdmissionGuard(config.Admission);
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
            joinService,
            releaseService,
            completions,
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
        authorityScheduler = new AuthorityOperationScheduler(peerRegistry);
        itemOperations = new ItemOperationHandler(
            peerRegistry,
            sender,
            sessionStore,
            carryStateStore,
            entityRegistry,
            carrySync,
            teardown,
            authorityScheduler,
            operationTracker,
            new ItemAuthorityOperations(itemInteractionService, completions, logger));
        corpseViews = new CorpseViewDistributor(
            corpseViewers,
            worldInteractionLeases,
            peerRegistry,
            sender,
            corpseStore,
            liveMobCorpseStore);
        corpsePresence = new CorpsePresenceReplicator(
            config,
            corpseStore,
            liveMobCorpseStore,
            corpseRuntimeMetrics,
            corpseViews,
            peerRegistry,
            sender);
        corpseAuthority = new CorpseAuthorityOperations(
            corpseInteractionService,
            liveMobCorpseInteractionService,
            completions,
            logger);
        var corpseFinalizer = new CorpseOperationFinalizer(
            peerRegistry,
            sender,
            sessionStore,
            carryStateStore,
            entityRegistry,
            carrySync,
            teardown,
            authorityScheduler,
            corpseViewers,
            worldInteractionLeases,
            corpseViews,
            corpseStore,
            liveMobCorpseStore);
        corpseInteractions = new CorpseInteractionHandler(
            config,
            peerRegistry,
            sender,
            carryStateStore,
            corpseStore,
            liveMobCorpseStore,
            corpseViewers,
            worldInteractionLeases,
            authorityScheduler,
            operationTracker,
            completions,
            corpseAuthority,
            corpseFinalizer);
        worldInteractions = new WorldInteractionHandler(
            worldInteractionAuthority,
            peerRegistry,
            sender,
            entityRegistry,
            carrySync,
            teardown,
            authorityScheduler,
            operationTracker,
            new WorldInteractionAuthorityOperations(
                worldInteractionAuthority,
                completions,
                logger));
        sessions = new RealtimeSessionHandler(
            config,
            peerRegistry,
            sender,
            sessionStore,
            carryStateStore,
            entityRegistry,
            connectionBindings,
            staticCollisionWorld,
            collisionWorld,
            admissionGuard,
            presence,
            teardown,
            sessionOperations,
            operationTracker,
            registrationLease,
            networkMetrics,
            performanceMetrics,
            logger);
        dispatcher = new RealtimeMessageDispatcher(
            peerRegistry,
            sender,
            networkMetrics,
            logger,
            RealtimeMessageRoutes.CreateUnauthenticated(sessions.HandleJoinRequest),
            RealtimeMessageRoutes.CreateAuthenticated(
                HandleMovementInput,
                itemOperations.HandleIntent,
                corpseInteractions.HandleIntent,
                worldInteractions.HandleIntent,
                sessions.HandleLeaveRequest));
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
                    corpseAuthority.MutationCoordinator.WaitingMutationCount,
                    corpseAuthority.MutationCoordinator.ActiveCorpseCount);
                completionProcessor.ProcessCompleted(completedOperationBudget);
                performanceMetrics.ObserveCompletedOperationBacklog(
                    completions.Count);
                performanceMetrics.ObserveCorpseMutationCoordinator(
                    corpseAuthority.MutationCoordinator.WaitingMutationCount,
                    corpseAuthority.MutationCoordinator.ActiveCorpseCount);
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
            corpseViews.ClearSnapshots();
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
            if (worldInteractions.IsActive
                && clock.ServerTick % interactionValidationIntervalTicks == 0)
            {
                worldInteractions.RevalidateLeases();
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
                corpsePresence.Broadcast();
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
