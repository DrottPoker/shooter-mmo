using Microsoft.Extensions.Logging;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.SimulationWorker.WorldCollision;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.SimulationWorker.Realtime;

// The inputs of RealtimeSimulationService before defaults are applied.
internal sealed record RealtimeServerDependencies(
    SimulationWorkerConfig Config,
    SimulationJoinService JoinService,
    SimulationSessionReleaseService ReleaseService,
    ActiveSimulationSessionStore SessionStore,
    CharacterItemStateStore CharacterItemStateStore,
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
    CorpseRuntimeMetrics? CorpseRuntimeMetrics,
    DevelopmentItemInteractionOptions? DevelopmentItemInteractions,
    IWorldActorCapabilityAvailabilityPolicy? CapabilityAvailability,
    SimulationGatheringService? GatheringService = null,
    CombatDependencies? Combat = null,
    AppearanceDependencies? Appearance = null,
    DevelopmentAdminOptions? DevelopmentAdmin = null,
    QuestDependencies? Quests = null);

// What quests need beyond the realtime services: the quest logs of the active characters, the
// executor that runs quest actions through GameService, and the quest catalog that decides which
// kills and yields count. Without an executor no quest intent runs, and without the catalog no
// kill or yield counts for a quest.
internal sealed record QuestDependencies(
    CharacterQuestLogStore QuestLogs,
    QuestOperationExecutor? Executor,
    QuestCatalogRuntimeDocument? Catalog = null);

// What looks need beyond the realtime services: the looks of the active characters, the
// appearance content's defaults, and GameService for changes. Without it no player has a look
// and no look changes.
internal sealed record AppearanceDependencies(
    CharacterLookStore Looks,
    AppearanceReplicationSettings Settings,
    GameServiceClient? GameServiceClient);

// What combat needs beyond the realtime services. Without it the worker runs combat with
// the default rules, an empty item catalog, and Mobs that leave no corpse.
internal sealed record CombatDependencies(
    CombatRules Rules,
    ItemCatalogRuntimeDocument ItemCatalog,
    WorldActorRuntime? WorldActors,
    SimulationShardRules ShardRules,
    GameServiceClient? GameServiceClient,
    MobCorpseLifecycleService? MobCorpses,
    CombatMetrics? Metrics,
    ICombatRandom? Random = null);

// The only place realtime components are created. Components are not registered in
// dependency injection, which keeps loop-confined state out of reach of other services.
// The features register their tick phases, message routes, lifecycle hooks, and shutdown
// steps here, in RealtimeFeatureRegistries; tests pin what they registered.
internal static class RealtimeServerComposition
{
    public static RealtimeServer Create(RealtimeServerDependencies dependencies, ILogger logger)
    {
        var registries = new RealtimeFeatureRegistries();
        var config = dependencies.Config;
        var itemStateStore = dependencies.CharacterItemStateStore;
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
        // Metrics the host did not provide are created here and owned by the server.
        var ownedResources = new List<IDisposable>();
        var networkMetrics = dependencies.NetworkMetrics
            ?? Own(new RealtimeNetworkMetrics(), ownedResources);
        var performanceMetrics = dependencies.PerformanceMetrics
            ?? Own(new RealtimePerformanceMetrics(), ownedResources);
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
            clock,
            registries.Hooks);
        var carrySync = new CarryStateSynchronizer(
            itemStateStore,
            connectionBindings,
            peers,
            sender);
        var sessionOperations = new SessionAuthorityOperations(
            dependencies.JoinService,
            dependencies.ReleaseService,
            completions,
            operationTracker,
            logger);
        var appearanceDependencies = dependencies.Appearance
            ?? new AppearanceDependencies(new CharacterLookStore(), AppearanceReplicationSettings.Default, null);
        var questDependencies = dependencies.Quests ?? new QuestDependencies(new CharacterQuestLogStore(), null);
        var questProgress = questDependencies.Catalog is null
            ? null
            : new QuestProgressFilter(questDependencies.QuestLogs, questDependencies.Catalog);
        // Teardown removes from the stores that a session release removes from.
        var characterStores = dependencies.ReleaseService.CharacterStores;
        var teardown = new RealtimeSessionTeardown(
            peers,
            sender,
            presence,
            sessionStore,
            characterStores,
            corpseViewers,
            worldInteractionLeases,
            connectionBindings,
            entityRegistry,
            networkMetrics,
            registries.Hooks);
        var appearances = new AppearanceReplicator(
            peers,
            sender,
            presence,
            entityRegistry,
            connectionBindings,
            itemStateStore,
            appearanceDependencies.Looks,
            appearanceDependencies.Settings);
        presence.Appearances = appearances;
        var appearanceIntents = new AppearanceIntentHandler(
            peers,
            sender,
            appearanceDependencies.Looks,
            operationTracker,
            completions,
            appearanceDependencies.GameServiceClient,
            logger);
        var authorityScheduler = new AuthorityOperationScheduler(peers);
        var combatDependencies = dependencies.Combat ?? new CombatDependencies(
            CombatRules.Default,
            new ItemCatalogRuntimeDocument(),
            null,
            new SimulationShardRules(),
            null,
            null,
            null);
        var combatAuthority = new PlayerCombatAuthority(
            config,
            combatDependencies.Rules,
            clock,
            peers,
            sender,
            itemStateStore,
            sessionStore,
            entityRegistry,
            carrySync,
            corpseStore,
            authorityScheduler,
            teardown,
            sessionOperations,
            operationTracker,
            new PlayerCombatAuthorityOperations(
                combatDependencies.GameServiceClient,
                completions,
                logger),
            combatDependencies.Metrics,
            logger,
            questDependencies.Executor,
            questProgress);
        var itemOperations = new ItemOperationHandler(
            peers,
            sender,
            sessionStore,
            itemStateStore,
            entityRegistry,
            carrySync,
            teardown,
            authorityScheduler,
            operationTracker,
            new ItemInteractionAccessService(
                dependencies.DevelopmentItemInteractions ?? DevelopmentItemInteractionOptions.Disabled,
                worldInteractionLeases,
                worldActorStore,
                dependencies.CapabilityAvailability
                    ?? new DefaultWorldActorCapabilityAvailabilityPolicy()),
            new ItemAuthorityOperations(
                dependencies.ItemInteractionService,
                completions,
                logger),
            combatAuthority);
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
            sender)
        {
            VisibleEquipmentSlots = appearanceDependencies.Settings.VisibleEquipmentSlots
        };
        var corpseAuthority = new CorpseAuthorityOperations(
            dependencies.CorpseInteractionService,
            dependencies.LiveMobCorpseInteractionService,
            completions,
            logger);
        var corpseFinalizer = new CorpseOperationFinalizer(
            peers,
            sender,
            sessionStore,
            itemStateStore,
            entityRegistry,
            carrySync,
            teardown,
            authorityScheduler,
            corpseViewers,
            worldInteractionLeases,
            corpseViews,
            corpseStore,
            liveMobCorpseStore,
            combatAuthority);
        var corpseInteractions = new CorpseInteractionHandler(
            config,
            peers,
            sender,
            itemStateStore,
            corpseStore,
            liveMobCorpseStore,
            corpseViewers,
            worldInteractionLeases,
            authorityScheduler,
            operationTracker,
            completions,
            corpseAuthority,
            corpseFinalizer,
            combatAuthority);
        var worldActorTimers = new WorldActorTimerQueue();
        var gathering = new GatheringHandler(
            config,
            clock,
            peers,
            sender,
            worldInteractionLeases,
            worldActorTimers,
            presence,
            sessionStore,
            itemStateStore,
            entityRegistry,
            carrySync,
            teardown,
            authorityScheduler,
            operationTracker,
            new GatheringAuthorityOperations(
                dependencies.GatheringService,
                completions,
                logger,
                questDependencies.Executor),
            dependencies.WorldActorMetrics,
            questProgress);
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
                logger),
            gathering,
            worldInteractionLeases,
            combatAuthority);
        var sessions = new RealtimeSessionHandler(
            config,
            peers,
            sender,
            sessionStore,
            itemStateStore,
            entityRegistry,
            connectionBindings,
            dependencies.StaticCollisionWorld,
            dependencies.CollisionWorld,
            admissionGuard,
            presence,
            registries.Hooks,
            teardown,
            sessionOperations,
            operationTracker,
            dependencies.RegistrationLease,
            networkMetrics,
            performanceMetrics,
            logger,
            combatDependencies.ShardRules,
            combatAuthority);
        combatAuthority.StartLeave = sessions.StartLeave;
        var combatants = new CombatantRegistry();
        var combatRandom = combatDependencies.Random
            ?? new SeededCombatRandom(dependencies.WorkerIdentity?.RuntimeId.GetHashCode() ?? Environment.TickCount);
        var combatReplicator = new CombatReplicator(
            peers,
            sender,
            connectionBindings,
            presence,
            combatants);
        var mobDeaths = new MobDeathProcessor(
            config,
            clock,
            worldActorStore,
            worldActorTimers,
            combatDependencies.WorldActors?.Catalog.LootProfiles ?? Array.Empty<WorldActorLootProfileDefinition>(),
            combatDependencies.WorldActors?.Catalog.RespawnProfiles ?? Array.Empty<WorldActorRespawnProfileDefinition>(),
            combatants,
            presence,
            combatDependencies.MobCorpses,
            completions,
            operationTracker,
            combatRandom,
            logger,
            questDependencies.Catalog is null
                ? null
                : new QuestDropRoller(questDependencies.QuestLogs, questDependencies.Catalog));
        var combatLineOfSight = new CombatLineOfSight(dependencies.CollisionWorld);
        var mobs = new MobSystem(
            config,
            clock,
            dependencies.CollisionWorld,
            combatants,
            combatLineOfSight,
            presence,
            combatDependencies.WorldActors,
            combatDependencies.Metrics);
        var combat = new CombatSystem(
            config,
            combatDependencies.Rules,
            clock,
            peers,
            sender,
            itemStateStore,
            entityRegistry,
            connectionBindings,
            presence,
            worldActorStore,
            combatDependencies.WorldActors?.Catalog.CombatProfiles ?? Array.Empty<WorldActorCombatProfileDefinition>(),
            combatants,
            combatReplicator,
            combatAuthority,
            mobDeaths,
            mobs,
            new CombatItemCatalog(combatDependencies.ItemCatalog, config.MovementSimulation.TickRateHz),
            combatLineOfSight,
            combatRandom,
            combatDependencies.ShardRules,
            corpseViewers,
            corpseViews,
            dependencies.CollisionWorld,
            combatDependencies.Metrics)
        {
            CloseWorldInteraction = worldInteractions.CloseForPeer
        };
        var questIntents = new QuestIntentHandler(
            peers,
            sender,
            entityRegistry,
            carrySync,
            teardown,
            authorityScheduler,
            operationTracker,
            completions,
            questDependencies.Executor,
            dependencies.DevelopmentAdmin ?? DevelopmentAdminOptions.Disabled,
            logger,
            combatAuthority);
        var admin = new AdminIntentHandler(
            config,
            dependencies.DevelopmentAdmin ?? DevelopmentAdminOptions.Disabled,
            peers,
            sender,
            presence,
            combat,
            dependencies.CollisionWorld,
            dependencies.CollisionStreamingStore);
        var snapshots = new SnapshotReplicator(
            config,
            mobs,
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
            worldActorTimers,
            logger)
        {
            Activated = combat.RegisterWorldActors,
            RespawnMob = mobDeaths.Respawn,
            KeepActive = mobs.IsBusy
        };
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
            logger,
            combatAuthority);

        var routes = registries.Routes;
        RealtimeSessionHandler.AddRoutes(routes, sessions.HandleJoinRequest, sessions.HandleLeaveRequest);
        MovementInputHandler.AddRoute(routes, new MovementInputHandler(peers, sender, combat.IsMovementBlocked).HandleInput);
        ItemOperationHandler.AddRoute(routes, itemOperations.HandleIntent);
        CorpseInteractionHandler.AddRoute(routes, corpseInteractions.HandleIntent);
        WorldInteractionHandler.AddRoute(routes, worldInteractions.HandleIntent);
        CombatSystem.AddRoute(routes, combat.HandleIntent);
        AppearanceIntentHandler.AddRoute(routes, appearanceIntents.HandleIntent);
        AdminIntentHandler.AddRoute(routes, admin.HandleIntent);
        QuestIntentHandler.AddRoute(routes, questIntents.HandleIntent);

        // Each phase takes its place from SimulationTickOrder, so the order of these calls
        // does not matter.
        var tickPhases = registries.TickPhases;
        new CollisionStreamingTickPhase(
            config,
            mobs,
            dependencies.CollisionStreamingStore,
            performanceMetrics).AddTickPhase(tickPhases);
        new PlayerMovementTickPhase(carrySync, performanceMetrics).AddTickPhase(tickPhases);
        tickPhases.Add(
            SimulationTickOrder.PlayerGrid,
            "player grid",
            static (in SimulationTick tick) => tick.PlayerGrid.Rebuild(tick.Players.AsReadOnlyList()));
        worldActors.AddTickPhase(tickPhases);
        mobs.AddTickPhase(tickPhases);
        combat.AddTickPhase(tickPhases);
        worldInteractions.AddTickPhase(tickPhases, config.MovementSimulation.TickRateHz);
        gathering.AddTickPhase(tickPhases);
        appearances.AddTickPhase(tickPhases, config);
        snapshots.AddTickPhase(tickPhases);
        corpsePresence.AddTickPhase(tickPhases);

        // The hooks of each list run in the order they are added here.
        var hooks = registries.Hooks;
        hooks.AddSpawnSent(combatReplicator.OnSpawnSent);
        hooks.AddPlayerRemoved(combat.OnPlayerEntityRemoved);
        hooks.AddPlayerJoined(combat.OnPlayerJoined);
        hooks.AddPlayerJoined(admin.OnPlayerJoined);

        // The shutdown runs these steps in the order they are added here, after the transport
        // stopped and every in-flight operation finished.
        var shutdown = registries.ShutdownSteps;
        shutdown.AddAsync(
            "combat flush",
            () => combatAuthority.FlushOnShutdownAsync(completions.TakeFor(combatAuthority)));
        shutdown.Add("combat", combat.Clear);
        shutdown.Add("peers", peers.Clear);
        shutdown.Add("presence", presence.Clear);
        shutdown.Add("connection bindings", connectionBindings.Clear);
        shutdown.Add("entities", entityRegistry.Clear);
        shutdown.Add("character state", () => characterStores.Clear(CharacterSessionRemovals.Shutdown));
        shutdown.Add("corpse viewers", corpseViewers.Clear);
        shutdown.Add("interaction leases", () => worldInteractionLeases?.Clear());
        shutdown.Add("world actors", () => worldActorStore?.Clear());
        shutdown.Add("corpse views", corpseViews.ClearSnapshots);
        shutdown.Add("orphan sweep", supervisor.ClearUnboundSessions);
        shutdown.Add("population gauges", () =>
        {
            networkMetrics.SetActivePeers(0);
            networkMetrics.SetActiveEntities(0);
            networkMetrics.SetPeerPopulation(0, 0, 0);
        });

        var tickLoop = new SimulationTickLoop(
            clock,
            entityRegistry,
            tickPhases,
            performanceMetrics,
            logger);
        var dispatcher = new RealtimeMessageDispatcher(
            peers,
            sender,
            networkMetrics,
            logger,
            routes);
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
            performanceMetrics,
            registries,
            ownedResources,
            logger);
    }

    private static T Own<T>(T resource, List<IDisposable> ownedResources)
        where T : IDisposable
    {
        ownedResources.Add(resource);
        return resource;
    }
}
