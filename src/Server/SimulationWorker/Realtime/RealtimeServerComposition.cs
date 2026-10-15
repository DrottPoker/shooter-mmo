using Microsoft.Extensions.Logging;
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

// The inputs of RealtimeSimulationService before defaults are applied.
internal sealed record RealtimeServerDependencies(
    SimulationWorkerConfig Config,
    SimulationJoinService JoinService,
    SimulationSessionReleaseService ReleaseService,
    ActiveSimulationSessionStore SessionStore,
    CarryStateStore CarryStateStore,
    SimulationItemInteractionService ItemInteractionService,
    SimulationCorpseInteractionService CorpseInteractionService,
    DurableCorpseStore CorpseStore,
    CorpseViewerRegistry CorpseViewers,
    SimulationEntityRegistry EntityRegistry,
    ConnectionEntityBindingRegistry ConnectionBindings,
    RealtimeTransportReadiness TransportReadiness,
    ChunkedStaticCollisionWorld StaticCollisionWorld,
    ICollisionWorld CollisionWorld,
    WorldCollisionStreamingStore? CollisionStreamingStore,
    SimulationInterestManager? InterestManager,
    RealtimeNetworkMetrics? NetworkMetrics,
    SimulationWorkerRegistrationLease? RegistrationLease,
    RealtimePerformanceMetrics? PerformanceMetrics,
    WorldActorStore? WorldActorStore,
    WorldActorActivityScheduler? WorldActorActivityScheduler,
    WorldInteractionLeaseRegistry? WorldInteractionLeases,
    WorldInteractionAuthorityService? WorldInteractionAuthority,
    WorldActorMetrics? WorldActorMetrics,
    SimulationWorkerIdentity? WorkerIdentity,
    LiveMobCorpseStore? LiveMobCorpseStore,
    LiveMobCorpseInteractionService? LiveMobCorpseInteractionService,
    CorpseRuntimeMetrics? CorpseRuntimeMetrics);

// The only place realtime components are created. Components are not registered in
// dependency injection, which keeps loop-confined state out of reach of other services.
internal static class RealtimeServerComposition
{
    public static RealtimeServer Create(RealtimeServerDependencies dependencies, ILogger logger)
    {
        var config = dependencies.Config;
        var carryStateStore = dependencies.CarryStateStore;
        var sessionStore = dependencies.SessionStore;
        var corpseStore = dependencies.CorpseStore;
        var corpseViewers = dependencies.CorpseViewers;
        var entityRegistry = dependencies.EntityRegistry;
        var connectionBindings = dependencies.ConnectionBindings;
        var worldActorStore = dependencies.WorldActorStore;
        var worldInteractionLeases = dependencies.WorldInteractionLeases;
        var worldInteractionAuthority = dependencies.WorldInteractionAuthority;
        var liveMobCorpseStore = dependencies.LiveMobCorpseStore;
        var completions = new RealtimeCompletionQueue();
        var operationTracker = new RealtimeOperationTracker();
        var clock = new RealtimeSimulationClock();
        var interestManager = dependencies.InterestManager
            ?? new SimulationInterestManager(config.InterestManagement);
        var networkMetrics = dependencies.NetworkMetrics ?? new RealtimeNetworkMetrics();
        var performanceMetrics = dependencies.PerformanceMetrics
            ?? new RealtimePerformanceMetrics();
        var sender = new RealtimePeerSender(networkMetrics, logger);
        var admissionGuard = new PeerAdmissionGuard(config.Admission);
        var transport = new LiteNetLibRealtimeTransport(
            config,
            new RealtimeConnectionAdmission(
                config,
                dependencies.RegistrationLease,
                admissionGuard,
                networkMetrics),
            logger);
        var peers = new RealtimePeerRegistry(
            transport,
            connectionBindings,
            entityRegistry,
            networkMetrics);
        var presence = new EntityPresenceReplicator(
            peers,
            sender,
            entityRegistry,
            connectionBindings,
            worldActorStore,
            interestManager,
            networkMetrics,
            clock);
        var carrySync = new CarryStateSynchronizer(
            carryStateStore,
            connectionBindings,
            peers,
            sender);
        var sessionOperations = new SessionAuthorityOperations(
            dependencies.JoinService,
            dependencies.ReleaseService,
            completions,
            operationTracker,
            logger);
        var teardown = new RealtimeSessionTeardown(
            peers,
            sender,
            presence,
            sessionStore,
            carryStateStore,
            corpseViewers,
            worldInteractionLeases,
            connectionBindings,
            entityRegistry,
            networkMetrics);
        var authorityScheduler = new AuthorityOperationScheduler(peers);
        var itemOperations = new ItemOperationHandler(
            peers,
            sender,
            sessionStore,
            carryStateStore,
            entityRegistry,
            carrySync,
            teardown,
            authorityScheduler,
            operationTracker,
            new ItemAuthorityOperations(
                dependencies.ItemInteractionService,
                completions,
                logger));
        var corpseViews = new CorpseViewDistributor(
            corpseViewers,
            worldInteractionLeases,
            peers,
            sender,
            corpseStore,
            liveMobCorpseStore);
        var corpsePresence = new CorpsePresenceReplicator(
            config,
            corpseStore,
            liveMobCorpseStore,
            dependencies.CorpseRuntimeMetrics,
            corpseViews,
            peers,
            sender);
        var corpseAuthority = new CorpseAuthorityOperations(
            dependencies.CorpseInteractionService,
            dependencies.LiveMobCorpseInteractionService,
            completions,
            logger);
        var corpseFinalizer = new CorpseOperationFinalizer(
            peers,
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
        var corpseInteractions = new CorpseInteractionHandler(
            config,
            peers,
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
        var worldInteractions = new WorldInteractionHandler(
            worldInteractionAuthority,
            peers,
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
        var sessions = new RealtimeSessionHandler(
            config,
            peers,
            sender,
            sessionStore,
            carryStateStore,
            entityRegistry,
            connectionBindings,
            dependencies.StaticCollisionWorld,
            dependencies.CollisionWorld,
            admissionGuard,
            presence,
            teardown,
            sessionOperations,
            operationTracker,
            dependencies.RegistrationLease,
            networkMetrics,
            performanceMetrics,
            logger);
        var snapshots = new SnapshotReplicator(
            config,
            entityRegistry,
            connectionBindings,
            peers,
            sender,
            presence,
            networkMetrics,
            performanceMetrics,
            clock);
        var worldActors = new WorldActorRuntimeDriver(
            config,
            worldActorStore,
            dependencies.WorldActorActivityScheduler,
            dependencies.WorldActorMetrics,
            dependencies.WorkerIdentity,
            presence,
            networkMetrics,
            logger);
        var tickLoop = new SimulationTickLoop(
            config,
            clock,
            entityRegistry,
            carrySync,
            worldActors,
            worldInteractions,
            snapshots,
            corpsePresence,
            dependencies.CollisionStreamingStore,
            performanceMetrics,
            logger);
        var supervisor = new RealtimePeerSupervisor(
            config,
            peers,
            transport,
            sender,
            sessionStore,
            teardown,
            sessionOperations,
            networkMetrics,
            logger);
        var lifecycle = new RealtimePeerLifecycle(
            config,
            peers,
            admissionGuard,
            presence,
            corpseViewers,
            worldInteractionLeases,
            sessionStore,
            teardown,
            sessionOperations,
            networkMetrics,
            logger);
        var dispatcher = new RealtimeMessageDispatcher(
            peers,
            sender,
            networkMetrics,
            logger,
            RealtimeMessageRoutes.CreateUnauthenticated(sessions.HandleJoinRequest),
            RealtimeMessageRoutes.CreateAuthenticated(
                new MovementInputHandler(peers, sender).HandleInput,
                itemOperations.HandleIntent,
                corpseInteractions.HandleIntent,
                worldInteractions.HandleIntent,
                sessions.HandleLeaveRequest));
        var completionProcessor = new RealtimeCompletionProcessor(
            completions,
            peers,
            sender,
            logger);
        return new RealtimeServer(
            config,
            transport,
            lifecycle,
            dispatcher,
            completions,
            completionProcessor,
            operationTracker,
            corpseAuthority.MutationCoordinator,
            tickLoop,
            supervisor,
            worldActors,
            dependencies.TransportReadiness,
            peers,
            presence,
            connectionBindings,
            entityRegistry,
            carryStateStore,
            corpseViewers,
            worldInteractionLeases,
            worldActorStore,
            corpseViews,
            networkMetrics,
            performanceMetrics,
            logger);
    }
}
