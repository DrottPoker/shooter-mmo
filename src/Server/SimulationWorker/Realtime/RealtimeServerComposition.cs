using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Registry;
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
    QuestDependencies Quests,
    PersistenceDependencies Persistence);

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
        var availability = host.GameServiceAvailability
            ?? new GameServiceAvailability(TimeProvider.System, NullLogger<GameServiceAvailability>.Instance);
        var clock = new RealtimeSimulationClock();
        var loopGuard = new RealtimeLoopGuard(config.TickPhaseFailureLimit, clock, performanceMetrics, logger);
        var transport = new LiteNetLibRealtimeTransport(
            config,
            new RealtimeConnectionAdmission(
                config,
                host.RegistrationLease,
                admissionGuard,
                networkMetrics),
            loopGuard,
            logger,
            networkMetrics);
        var core = CreateCore(host, transport, admissionGuard, networkMetrics, performanceMetrics, clock, loopGuard, availability, logger);

        // The features, each after the features it takes.
        var appearance = new AppearanceFeature(core, dependencies.Appearance);
        var quests = new QuestProgressFeature(dependencies.Quests);
        var playerCombat = new PlayerCombatFeature(core, dependencies.Combat, quests);
        ItemFeature.Add(core, dependencies.Items, playerCombat);
        var corpses = new CorpseFeature(core, dependencies.Corpses, appearance, playerCombat);
        var worldActors = new WorldActorFeature(core, dependencies.WorldActors, quests, playerCombat);
        SessionFeature.Add(core, playerCombat);
        var combat = new CombatFeature(core, dependencies.Combat, playerCombat, quests, corpses, worldActors);
        QuestFeature.Add(core, quests, playerCombat);
        AdminFeature.Add(core, combat);
        PersistenceFeature.Add(core, dependencies.Persistence, host.ReleaseService.FinalStates, combat);
        MovementFeature.Add(core, combat);
        SnapshotFeature.Add(core, combat);

        // The cycles that constructor arguments cannot close, each closed by a settable property.
        // Presence sends a spawned player's look, and the appearance replicator sends through presence.
        core.Presence.Appearances = appearance.Replicator;
        // The world actor driver activates and respawns the Mobs of combat, which takes its timers.
        worldActors.Driver.Activated = combat.Combat.RegisterWorldActors;
        worldActors.Driver.RespawnMob = combat.MobDeaths.Respawn;
        worldActors.Driver.KeepActive = combat.Mobs.IsBusy;
        // Presence spawns a walking Mob as one, and the Mob system sends its rest poses through presence.
        core.Presence.IsActorMoving = combat.Mobs.IsMoving;

        // The infrastructure that runs the peers, the loop, and the shutdown.
        var supervisor = new RealtimePeerSupervisor(
            config,
            core.Peers,
            transport,
            core.Sender,
            core.SessionStore,
            core.Teardown,
            core.Bodies,
            core.SessionOperations,
            networkMetrics,
            logger,
            availability,
            playerCombat.Authority.HoldsDurableWork);
        var lifecycle = new RealtimePeerLifecycle(
            config,
            core.Peers,
            admissionGuard,
            core.Presence,
            core.CorpseViewers,
            core.WorldInteractionLeases,
            core.SessionStore,
            core.Teardown,
            networkMetrics,
            logger);
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
            new SimulationTickLoop(
                core.Clock,
                core.EntityRegistry,
                registries.TickPhases,
                performanceMetrics,
                loopGuard,
                logger,
                networkMetrics,
                ThreadCpuClock.ForCurrentPlatform(),
                host.LoopLiveness),
            supervisor,
            worldActors.Driver,
            host.TransportReadiness,
            performanceMetrics,
            registries,
            loopGuard,
            core.Peers,
            core.Sender,
            availability,
            host.StopReason ?? new SimulationWorkerStopReason(),
            ownedResources,
            logger);
    }

    // The core services around a peer directory, which is the transport in production; tests
    // build features on it around recording peers.
    internal static RealtimeCoreServices CreateCore(
        RealtimeHostDependencies host,
        IRealtimePeerDirectory peerDirectory,
        PeerAdmissionGuard admissionGuard,
        RealtimeNetworkMetrics networkMetrics,
        RealtimePerformanceMetrics performanceMetrics,
        RealtimeSimulationClock clock,
        RealtimeLoopGuard loopGuard,
        GameServiceAvailability availability,
        ILogger logger)
    {
        var config = host.Config;
        var registries = new RealtimeFeatureRegistries(
            new SimulationTickCadence(config.MovementSimulation.TickRateHz, config.SnapshotRateHz));
        var completions = new RealtimeCompletionQueue();
        var operationTracker = new RealtimeOperationTracker();
        var peers = new RealtimePeerRegistry(
            peerDirectory,
            host.ConnectionBindings,
            host.EntityRegistry,
            networkMetrics);
        var sender = new RealtimePeerSender(networkMetrics, logger, peers, loopGuard);
        // Entries to a view follow the room of the connection's reliable queue below its high-water
        // mark, so a client that acknowledges slowly sees a crowd fill in slowly instead of being
        // disconnected for the queue.
        var interestManager = host.InterestManager ?? new SimulationInterestManager(config.InterestManagement);
        interestManager.EntriesAllowedSource ??= new ReliableQueueEntryBudget(
            peers,
            ReliableQueueEntryBudget.HighWaterOf(config.Admission.MaxReliableQueuedPackets)).EntriesAllowed;
        var presence = new EntityPresenceReplicator(
            peers,
            sender,
            host.EntityRegistry,
            host.ConnectionBindings,
            host.WorldActorStore,
            interestManager,
            networkMetrics,
            clock,
            registries.Hooks,
            logger);
        networkMetrics.AttachEntityIds(() => host.EntityRegistry.IssuedEntityIds);
        var bodies = new LingeringBodies(config.LingeringBodySeconds, config.MovementSimulation.TickRateHz, clock);
        host.PresenceMetrics?.ObserveBodies(() => bodies.Count);
        var sessionOperations = new SessionAuthorityOperations(
            host.JoinService,
            host.ReleaseService,
            completions,
            operationTracker,
            logger);
        var failureIsolation = new RealtimeFailureIsolation(
            host.SessionStore,
            sender,
            loopGuard,
            sessionOperations,
            host.ReleaseService.FinalStates);
        failureIsolation.AddHooks(registries.Hooks);
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
            CollisionChunks = new CollisionChunkGate(host.CollisionStreamingStore, completions, performanceMetrics),
            RegistrationLease = host.RegistrationLease,
            WorkerIdentity = host.WorkerIdentity,
            ShardRules = host.ShardRules ?? new SimulationShardRules(),
            DevelopmentAdmin = host.DevelopmentAdmin ?? DevelopmentAdminOptions.Disabled,
            GameServiceAvailability = availability,
            Clock = clock,
            Completions = completions,
            OperationTracker = operationTracker,
            NetworkMetrics = networkMetrics,
            PerformanceMetrics = performanceMetrics,
            LoopGuard = loopGuard,
            FailureIsolation = failureIsolation,
            AdmissionGuard = admissionGuard,
            Peers = peers,
            Sender = sender,
            Presence = presence,
            AuthorityScheduler = new AuthorityOperationScheduler(peers, availability),
            CarrySync = new CarryStateSynchronizer(
                host.ItemStateStore,
                host.ConnectionBindings,
                peers,
                sender),
            SessionOperations = sessionOperations,
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
                registries.Hooks,
                bodies),
            Bodies = bodies,
            PresenceMetrics = host.PresenceMetrics,
            CompletionTargets = new OperationCompletionTargets(
                peers,
                host.ConnectionBindings,
                host.EntityRegistry,
                host.SessionStore)
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
