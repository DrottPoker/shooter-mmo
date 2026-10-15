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
    private readonly CarryStateStore carryStateStore;
    private readonly CorpseViewerRegistry corpseViewers;
    private readonly SimulationEntityRegistry entityRegistry;
    private readonly ConnectionEntityBindingRegistry connectionBindings;
    private readonly RealtimeTransportReadiness transportReadiness;
    private readonly ILogger<RealtimeSimulationService> logger;
    private readonly SimulationWorkerRegistrationLease? registrationLease;
    private readonly RealtimeCompletionQueue completions = new();
    private readonly RealtimeOperationTracker operationTracker = new();
    private readonly PeerAdmissionGuard admissionGuard;
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
    private readonly SnapshotReplicator snapshots;
    private readonly WorldActorRuntimeDriver worldActors;
    private readonly SimulationTickLoop tickLoop;
    private readonly RealtimePeerSupervisor supervisor;
    private readonly RealtimePeerLifecycle lifecycle;
    private NetManager? server;

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
        this.carryStateStore = carryStateStore;
        this.corpseViewers = corpseViewers;
        this.entityRegistry = entityRegistry;
        this.connectionBindings = connectionBindings;
        this.transportReadiness = transportReadiness;
        this.logger = logger;
        this.registrationLease = registrationLease;
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
        snapshots = new SnapshotReplicator(
            config,
            entityRegistry,
            connectionBindings,
            peerRegistry,
            sender,
            presence,
            networkMetrics,
            performanceMetrics,
            clock);
        worldActors = new WorldActorRuntimeDriver(
            config,
            worldActorStore,
            worldActorActivityScheduler,
            worldActorMetrics,
            workerIdentity,
            presence,
            networkMetrics,
            logger);
        tickLoop = new SimulationTickLoop(
            config,
            clock,
            entityRegistry,
            carrySync,
            worldActors,
            worldInteractions,
            snapshots,
            corpsePresence,
            collisionStreamingStore,
            performanceMetrics,
            logger);
        supervisor = new RealtimePeerSupervisor(
            config,
            peerRegistry,
            peerDirectory,
            sender,
            sessionStore,
            teardown,
            sessionOperations,
            networkMetrics,
            logger);
        lifecycle = new RealtimePeerLifecycle(
            config,
            peerRegistry,
            admissionGuard,
            presence,
            corpseViewers,
            worldInteractionLeases,
            sessionStore,
            teardown,
            sessionOperations,
            networkMetrics,
            logger);
        dispatcher = new RealtimeMessageDispatcher(
            peerRegistry,
            sender,
            networkMetrics,
            logger,
            RealtimeMessageRoutes.CreateUnauthenticated(sessions.HandleJoinRequest),
            RealtimeMessageRoutes.CreateAuthenticated(
                new MovementInputHandler(peerRegistry, sender).HandleInput,
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
        worldActors.Activate();

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
        listener.PeerConnectedEvent += peer => lifecycle.OnPeerConnected(peer.Id, peer.Address);
        listener.PeerDisconnectedEvent += (peer, disconnectInfo) =>
            lifecycle.OnPeerDisconnected(peer.Id, disconnectInfo.Reason);
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
                tickLoop.ProcessDueTicks(
                    simulationClock.Elapsed,
                    simulationInterval,
                    ref nextSimulationTick);
                supervisor.EnforcePeerState();
                supervisor.ReleaseOrphanedSessions();
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
            supervisor.ClearUnboundSessions();
            networkMetrics.SetActivePeers(0);
            networkMetrics.SetActiveEntities(0);
            networkMetrics.SetPeerPopulation(0, 0, 0);
            await operationTracker.WaitForAllAsync();
            logger.LogInformation("[SIMULATION] Realtime transport stopped.");
        }
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
