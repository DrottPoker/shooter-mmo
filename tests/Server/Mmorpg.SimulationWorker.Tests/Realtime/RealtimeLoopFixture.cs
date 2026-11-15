using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing.Content;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Builds the loop-confined realtime components in memory around recording peers, so a
// test can drive one handler deterministically without sockets or loop timing.
internal sealed class RealtimeLoopFixture : IDisposable
{
    private readonly HttpClient httpClient;

    public RealtimeLoopFixture(
        bool enableWorldActors = false,
        IReadOnlyList<IWorldActorCapabilityHandler>? capabilityHandlers = null,
        int? lingeringBodySeconds = null)
    {
        Config = RealtimeServerHarness.CreateConfig(27015, new RealtimeServerHarnessOptions
        {
            LingeringBodySeconds = lingeringBodySeconds
        });
        StaticCollisionWorld = CollisionTestWorldFactory.Create();
        CollisionWorld = new CompositeCollisionWorld(
            StaticCollisionWorld,
            new DynamicCollisionWorld(StaticCollisionWorld.ChunkSize));
        if (enableWorldActors)
        {
            WorldActors = new WorldActorStore(WorldActorTestData.Compile(), Entities);
            WorldActors.ActivateAssignment(
                Config.WorldId,
                Config.ShardId,
                RealtimeServerHarness.RuntimeId);
            WorldActorMetrics = new WorldActorMetrics();
            WorldInteractionAuthority = new WorldInteractionAuthorityService(
                Config,
                SessionStore,
                WorldActors,
                Leases,
                new WorldActorCapabilityRegistry(
                    new DefaultWorldActorCapabilityAvailabilityPolicy(),
                    capabilityHandlers ?? []),
                new WorldActorLineOfSightService(CollisionWorld),
                WorldActorMetrics);
        }

        Bodies = new LingeringBodies(Config.LingeringBodySeconds, Config.MovementSimulation.TickRateHz, Clock);
        Registry = new RealtimePeerRegistry(Directory, Bindings, Entities, Metrics);
        Sender = new RealtimePeerSender(Metrics, NullLogger.Instance);
        Presence = new EntityPresenceReplicator(
            Registry,
            Sender,
            Entities,
            Bindings,
            WorldActors,
            new SimulationInterestManager(Config.InterestManagement),
            Metrics,
            Clock,
            Hooks);
        CarrySync = new CarryStateSynchronizer(CharacterItemStateStore, Bindings, Registry, Sender);
        Teardown = new RealtimeSessionTeardown(
            Registry,
            Sender,
            Presence,
            SessionStore,
            CharacterSessionStores.Create(CharacterItemStateStore, Looks, QuestLogs),
            CorpseViewers,
            Leases,
            Bindings,
            Entities,
            Metrics,
            Hooks,
            Bodies);
        Scheduler = new AuthorityOperationScheduler(Registry);
        LoopGuard = new RealtimeLoopGuard(
            SimulationWorkerConfig.DefaultTickPhaseFailureLimit,
            Clock,
            PerformanceMetrics,
            Logger);
        httpClient = new HttpClient(GameService) { BaseAddress = new Uri("http://game-service.test") };
        var client = new GameServiceClient(httpClient);
        ReleaseService = new SimulationSessionReleaseService(
            client,
            SessionStore,
            CharacterStateTestData.SessionStores(CharacterItemStateStore));
        SessionOperations = new SessionAuthorityOperations(
            new SimulationJoinService(
                client,
                SessionStore,
                CharacterItemStateStore,
                Config,
                new SimulationWorkerIdentity(RealtimeServerHarness.RuntimeId, DateTime.UtcNow)),
            ReleaseService,
            Completions,
            OperationTracker,
            NullLogger.Instance);
        FailureIsolation = new RealtimeFailureIsolation(
            SessionStore,
            Sender,
            LoopGuard,
            SessionOperations,
            ReleaseService.FinalStates);
        FailureIsolation.AddHooks(Hooks);
        CompletionTargets = new OperationCompletionTargets(Registry, Bindings, Entities, SessionStore);
    }

    public SimulationWorkerConfig Config { get; }

    public ChunkedStaticCollisionWorld StaticCollisionWorld { get; }

    public ICollisionWorld CollisionWorld { get; }

    public RecordingPeerDirectory Directory { get; } = new();

    public RealtimeNetworkMetrics Metrics { get; } = new();

    public RealtimePerformanceMetrics PerformanceMetrics { get; } = new();

    public CapturingLogger<RealtimeSimulationService> Logger { get; } = new();

    // Counts the loop failures that the fixture's components report, as the server's guard does.
    public RealtimeLoopGuard LoopGuard { get; }

    public RealtimeFailureIsolation FailureIsolation { get; }

    // Answers the releases that the fixture's session operations start.
    public ScriptedGameServiceHandler GameService { get; } = new();

    public SimulationSessionReleaseService ReleaseService { get; }

    public SessionAuthorityOperations SessionOperations { get; }

    public RealtimeSimulationClock Clock { get; } = new();

    public ActiveSimulationSessionStore SessionStore { get; } = new();

    public CharacterItemStateStore CharacterItemStateStore { get; } = new();

    public CharacterLookStore Looks { get; } = new();

    public CharacterQuestLogStore QuestLogs { get; } = new();

    public RealtimeLifecycleHooks Hooks { get; } = new();

    public SimulationEntityRegistry Entities { get; } = new();

    public ConnectionEntityBindingRegistry Bindings { get; } = new();

    public CorpseViewerRegistry CorpseViewers { get; } = new();

    public WorldInteractionLeaseRegistry Leases { get; } = new();

    public WorldActorTimerQueue Timers { get; } = new();

    public WorldActorStore? WorldActors { get; }

    public WorldActorMetrics? WorldActorMetrics { get; }

    public WorldInteractionAuthorityService? WorldInteractionAuthority { get; }

    public RealtimeCompletionQueue Completions { get; } = new();

    public RealtimeOperationTracker OperationTracker { get; } = new();

    public RealtimePeerRegistry Registry { get; }

    public RealtimePeerSender Sender { get; }

    public EntityPresenceReplicator Presence { get; }

    public CarryStateSynchronizer CarrySync { get; }

    public RealtimeSessionTeardown Teardown { get; }

    public LingeringBodies Bodies { get; }

    public AuthorityOperationScheduler Scheduler { get; }

    public OperationCompletionTargets CompletionTargets { get; }

    // Registers a joined player at the movement spawn, bound to a connected peer.
    // A player whose movement queries the given collision world instead of the fixture's.
    public ConnectedPlayer ConnectPlayer(int peerId, string name = "Loop Hero", ICollisionWorld? movementCollision = null)
    {
        var session = RealtimeTestSessions.Create(name);
        Assert.Equal(ActiveSimulationSessionRegistration.Joined, SessionStore.Register(session));
        CharacterItemStateStore.Register(
            session.CharacterId,
            session.SimulationSessionId,
            CharacterStateTestData.ItemState(session.CarryState),
            out _);
        var initialState = PlayerMovementSimulation.CreateInitialState(
            Config.MovementSimulation,
            CollisionWorld,
            Config.MovementSpawn.X,
            Config.MovementSpawn.Y,
            Config.MovementSpawn.Z,
            Config.MovementSpawn.YawDegrees);
        var entity = Entities.RegisterPlayer(
                session,
                () => new AuthoritativePlayerMovement(
                    initialState,
                    Config.MovementSimulation,
                    session.CarryState,
                    movementCollision ?? CollisionWorld))
            .Entity;
        Bindings.Bind(peerId, entity.NetworkEntityId);
        var peer = new RecordingPeerConnection(peerId);
        Directory.Peers[peerId] = peer;
        var context = new RealtimePeerContext(peerId, DateTime.UtcNow, Config.UdpQuotas)
        {
            Session = session
        };
        Registry.Register(context);
        return new ConnectedPlayer(context, peer, entity, session);
    }

    public ItemOperationHandler CreateItemHandler(
        ItemAuthorityOperations authority,
        DevelopmentItemInteractionOptions? developmentOptions = null)
    {
        return new ItemOperationHandler(
            Registry,
            Sender,
            SessionStore,
            CharacterItemStateStore,
            CarrySync,
            Teardown,
            CompletionTargets,
            Scheduler,
            OperationTracker,
            new ItemInteractionAccessService(
                developmentOptions ?? DevelopmentItemInteractionOptions.Disabled,
                Leases,
                WorldActors,
                new DefaultWorldActorCapabilityAvailabilityPolicy()),
            authority);
    }

    public WorldInteractionHandler CreateWorldInteractionHandler(GatheringHandler? gathering = null)
    {
        return new WorldInteractionHandler(
            WorldInteractionAuthority,
            Registry,
            Sender,
            CarrySync,
            Teardown,
            CompletionTargets,
            Scheduler,
            OperationTracker,
            CreateWorldInteractionOperations(),
            gathering ?? CreateGatheringHandler());
    }

    public GatheringHandler CreateGatheringHandler(
        SimulationGatheringService? gatheringService = null,
        CharacterQuestLogStore? questLogs = null)
    {
        return new GatheringHandler(
            Config,
            Clock,
            Registry,
            Sender,
            Leases,
            Timers,
            Presence,
            SessionStore,
            CharacterItemStateStore,
            CarrySync,
            Teardown,
            CompletionTargets,
            Scheduler,
            OperationTracker,
            new GatheringAuthorityOperations(gatheringService, Completions, NullLogger.Instance),
            WorldActorMetrics,
            questLogs is null ? null : new QuestProgressFilter(questLogs, RealtimeServerHarness.QuestCatalog));
    }

    public WorldInteractionAuthorityOperations CreateWorldInteractionOperations()
    {
        return new WorldInteractionAuthorityOperations(
            WorldInteractionAuthority,
            Completions,
            NullLogger.Instance);
    }

    public static RealtimeInboundMessage Message(
        ConnectedPlayer player,
        RealtimeMessageType type,
        byte[] packet)
    {
        return new RealtimeInboundMessage(
            player.Peer,
            player.Context,
            type,
            packet,
            RealtimeProtocol.ControlChannel,
            LiteNetLib.DeliveryMethod.ReliableOrdered);
    }

    public void Dispose()
    {
        httpClient.Dispose();
        Metrics.Dispose();
        PerformanceMetrics.Dispose();
        WorldActorMetrics?.Dispose();
    }
}

internal sealed record ConnectedPlayer(
    RealtimePeerContext Context,
    RecordingPeerConnection Peer,
    PlayerSimulationEntity Entity,
    ActiveSimulationSession Session);
