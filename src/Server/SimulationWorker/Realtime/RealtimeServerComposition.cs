using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// The inputs of RealtimeSimulationService: the host services that the infrastructure and more
// than one feature take, and one record per feature that needs host services of its own.
internal sealed record RealtimeServerDependencies(
    RealtimeHostDependencies Host,
    ItemDependencies Items,
    CorpseDependencies Corpses,
    WorldActorDependencies WorldActors,
    CombatDependencies Combat,
    AppearanceDependencies Appearance,
    QuestDependencies Quests);

// The only place realtime components are created. Components are not registered in
// dependency injection, which keeps loop-confined state out of reach of other services. Each
// feature unit creates its components and registers its tick phases, message routes, lifecycle
// hooks, and shutdown steps in RealtimeFeatureRegistries; tests pin what they registered.
internal static class RealtimeServerComposition
{
    public static RealtimeServer Create(RealtimeServerDependencies dependencies, ILogger logger)
    {
        var host = dependencies.Host;
        var config = host.Config;

        // Infrastructure. Metrics the host did not provide are created here and owned by the server.
        var ownedResources = new List<IDisposable>();
        var networkMetrics = host.NetworkMetrics
            ?? Own(new RealtimeNetworkMetrics(), ownedResources);
        var performanceMetrics = host.PerformanceMetrics
            ?? Own(new RealtimePerformanceMetrics(), ownedResources);
        var admissionGuard = new PeerAdmissionGuard(config.Admission);
        var transport = new LiteNetLibRealtimeTransport(
            config,
            new RealtimeConnectionAdmission(
                config,
                host.RegistrationLease,
                admissionGuard,
                networkMetrics),
            logger);
        var core = CreateCore(host, transport, admissionGuard, networkMetrics, performanceMetrics, logger);

        // The features, each after the features it takes.
        var appearance = new AppearanceFeature(core, dependencies.Appearance);
        var quests = new QuestProgressFeature(dependencies.Quests);
        var playerCombat = new PlayerCombatFeature(core, dependencies.Combat, quests);
        ItemFeature.Add(core, dependencies.Items, playerCombat);
        var corpses = new CorpseFeature(core, dependencies.Corpses, appearance, playerCombat);
        var worldActors = new WorldActorFeature(core, dependencies.WorldActors, quests, playerCombat);
        var session = new SessionFeature(core, playerCombat);
        var combat = new CombatFeature(core, dependencies.Combat, playerCombat, quests, corpses, worldActors);
        QuestFeature.Add(core, quests, playerCombat);
        AdminFeature.Add(core, combat);
        MovementFeature.Add(core, combat);
        SnapshotFeature.Add(core, combat);

        // The cycles that constructor arguments cannot close, each closed by a settable property.
        // Presence sends a spawned player's look, and the appearance replicator sends through presence.
        core.Presence.Appearances = appearance.Replicator;
        // Combat starts a leave it deferred, and the session handler defers leaves to combat.
        playerCombat.Authority.StartLeave = session.Handler.StartLeave;
        // The world actor driver activates and respawns the Mobs of combat, which takes its timers.
        worldActors.Driver.Activated = combat.Combat.RegisterWorldActors;
        worldActors.Driver.RespawnMob = combat.MobDeaths.Respawn;
        worldActors.Driver.KeepActive = combat.Mobs.IsBusy;

        // The infrastructure that runs the peers, the loop, and the shutdown.
        var supervisor = new RealtimePeerSupervisor(
            config,
            core.Peers,
            transport,
            core.Sender,
            core.SessionStore,
            core.Teardown,
            core.SessionOperations,
            networkMetrics,
            logger);
        var lifecycle = new RealtimePeerLifecycle(
            config,
            core.Peers,
            admissionGuard,
            core.Presence,
            core.CorpseViewers,
            core.WorldInteractionLeases,
            core.SessionStore,
            core.Teardown,
            core.SessionOperations,
            networkMetrics,
            logger,
            playerCombat.Authority);
        AddShutdownSteps(core, host.ReleaseService.CharacterStores, supervisor);
        var registries = core.Registries;
        return new RealtimeServer(
            config,
            transport,
            lifecycle,
            new RealtimeMessageDispatcher(core.Peers, core.Sender, networkMetrics, logger, registries.Routes),
            core.Completions,
            new RealtimeCompletionProcessor(core.Completions, core.Peers, core.Sender, logger),
            core.OperationTracker,
            corpses.MutationCoordinator,
            new SimulationTickLoop(core.Clock, core.EntityRegistry, registries.TickPhases, performanceMetrics, logger),
            supervisor,
            worldActors.Driver,
            host.TransportReadiness,
            performanceMetrics,
            registries,
            ownedResources,
            logger);
    }

    private static RealtimeCoreServices CreateCore(
        RealtimeHostDependencies host,
        LiteNetLibRealtimeTransport transport,
        PeerAdmissionGuard admissionGuard,
        RealtimeNetworkMetrics networkMetrics,
        RealtimePerformanceMetrics performanceMetrics,
        ILogger logger)
    {
        var config = host.Config;
        var registries = new RealtimeFeatureRegistries();
        var completions = new RealtimeCompletionQueue();
        var operationTracker = new RealtimeOperationTracker();
        var clock = new RealtimeSimulationClock();
        var sender = new RealtimePeerSender(networkMetrics, logger);
        var peers = new RealtimePeerRegistry(
            transport,
            host.ConnectionBindings,
            host.EntityRegistry,
            networkMetrics);
        var presence = new EntityPresenceReplicator(
            peers,
            sender,
            host.EntityRegistry,
            host.ConnectionBindings,
            host.WorldActorStore,
            host.InterestManager ?? new SimulationInterestManager(config.InterestManagement),
            networkMetrics,
            clock,
            registries.Hooks);
        return new RealtimeCoreServices
        {
            Config = config,
            Logger = logger,
            Registries = registries,
            SessionStore = host.SessionStore,
            ItemStateStore = host.ItemStateStore,
            EntityRegistry = host.EntityRegistry,
            ConnectionBindings = host.ConnectionBindings,
            CorpseStore = host.CorpseStore,
            CorpseViewers = host.CorpseViewers,
            WorldActorStore = host.WorldActorStore,
            WorldInteractionLeases = host.WorldInteractionLeases,
            StaticCollisionWorld = host.StaticCollisionWorld,
            CollisionWorld = host.CollisionWorld,
            CollisionStreamingStore = host.CollisionStreamingStore,
            RegistrationLease = host.RegistrationLease,
            WorkerIdentity = host.WorkerIdentity,
            ShardRules = host.ShardRules ?? new SimulationShardRules(),
            DevelopmentAdmin = host.DevelopmentAdmin ?? DevelopmentAdminOptions.Disabled,
            Clock = clock,
            Completions = completions,
            OperationTracker = operationTracker,
            NetworkMetrics = networkMetrics,
            PerformanceMetrics = performanceMetrics,
            AdmissionGuard = admissionGuard,
            Peers = peers,
            Sender = sender,
            Presence = presence,
            AuthorityScheduler = new AuthorityOperationScheduler(peers),
            CarrySync = new CarryStateSynchronizer(
                host.ItemStateStore,
                host.ConnectionBindings,
                peers,
                sender),
            SessionOperations = new SessionAuthorityOperations(
                host.JoinService,
                host.ReleaseService,
                completions,
                operationTracker,
                logger),
            // Teardown removes from the stores that a session release removes from.
            Teardown = new RealtimeSessionTeardown(
                peers,
                sender,
                presence,
                host.SessionStore,
                host.ReleaseService.CharacterStores,
                host.CorpseViewers,
                host.WorldInteractionLeases,
                host.ConnectionBindings,
                host.EntityRegistry,
                networkMetrics,
                registries.Hooks)
        };
    }

    // The steps of the shared state that no feature owns.
    private static void AddShutdownSteps(
        RealtimeCoreServices core,
        CharacterSessionStores characterStores,
        RealtimePeerSupervisor supervisor)
    {
        var shutdown = core.Registries.ShutdownSteps;
        var networkMetrics = core.NetworkMetrics;
        shutdown.Add(RealtimeShutdownOrder.Peers, "peers", core.Peers.Clear);
        shutdown.Add(RealtimeShutdownOrder.Presence, "presence", core.Presence.Clear);
        shutdown.Add(RealtimeShutdownOrder.ConnectionBindings, "connection bindings", core.ConnectionBindings.Clear);
        shutdown.Add(RealtimeShutdownOrder.Entities, "entities", core.EntityRegistry.Clear);
        shutdown.Add(
            RealtimeShutdownOrder.CharacterState,
            "character state",
            () => characterStores.Clear(CharacterSessionRemovals.Shutdown));
        shutdown.Add(RealtimeShutdownOrder.OrphanSweep, "orphan sweep", supervisor.ClearUnboundSessions);
        shutdown.Add(RealtimeShutdownOrder.PopulationGauges, "population gauges", () =>
        {
            networkMetrics.SetActivePeers(0);
            networkMetrics.SetActiveEntities(0);
            networkMetrics.SetPeerPopulation(0, 0, 0);
        });
    }

    private static T Own<T>(T resource, List<IDisposable> ownedResources)
        where T : IDisposable
    {
        ownedResources.Add(resource);
        return resource;
    }
}
