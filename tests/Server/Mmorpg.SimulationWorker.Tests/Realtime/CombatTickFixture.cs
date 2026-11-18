using LiteNetLib;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The production core and combat features, composed as RealtimeServerComposition composes them,
// around recording peers and driven one tick at a time, so a test reads every combatant and
// every message after every tick without sockets or loop timing. With snapshots, the interest
// refresh, the far states, and the snapshots run in the ticks too. Players join directly, without
// the join handshake, and see each other once ShowEveryoneToEveryone sent their spawns. With
// world actors, their runtime is activated and the Mobs think and walk in the ticks. With
// sessions, the join handler, the lingering bodies, the item operations, and the persistence of
// place and vitals take part too: a peer joins through the join handshake against the scripted
// GameService, and the test decides when the loop processes the completed operations.
internal sealed class CombatTickFixture : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly RealtimeNetworkMetrics networkMetrics = new();
    private readonly RealtimePerformanceMetrics performanceMetrics = new();
    private readonly SimulationTickLoop loop;
    private readonly RealtimeCompletionProcessor completionProcessor;
    private readonly RealtimePeerSupervisor supervisor;
    private readonly List<CombatTickPlayer> players = [];
    private TimeSpan nextTick = TimeSpan.Zero;

    public CombatTickFixture(
        CombatRules rules,
        ItemCatalogRuntimeDocument itemCatalog,
        WorldActorRuntime? worldActors = null,
        bool sessions = false,
        TimeProvider? availabilityClock = null,
        TimeSpan? joinHandshakeTimeout = null,
        bool checkpoints = false,
        TimeSpan? checkpointInterval = null,
        bool snapshots = false,
        SimulationInterestManager? interestManager = null)
    {
        SessionStore = new ActiveSimulationSessionStore(metrics: Presence);
        Config = RealtimeServerHarness.CreateConfig(27015, new RealtimeServerHarnessOptions
        {
            JoinHandshakeTimeout = joinHandshakeTimeout ?? TimeSpan.FromSeconds(2),
            CharacterCheckpointInterval = checkpointInterval
        });
        var staticCollision = CollisionTestWorldFactory.Create();
        CollisionWorld = new CompositeCollisionWorld(
            staticCollision,
            new DynamicCollisionWorld(staticCollision.ChunkSize));
        httpClient = new HttpClient(GameService) { BaseAddress = new Uri("http://game-service.test") };
        var availability = new GameServiceAvailability(
            availabilityClock ?? TimeProvider.System,
            NullLogger<GameServiceAvailability>.Instance);
        Availability = availability;
        var client = new GameServiceClient(httpClient, availability);
        var shardRules = new SimulationShardRules();
        shardRules.Set(new ShardRuleSet("test", PlayerVersusPlayer: true));
        var identity = new SimulationWorkerIdentity(RealtimeServerHarness.RuntimeId, DateTime.UtcNow);
        WorldActors = worldActors is null ? null : new WorldActorStore(worldActors, Entities);
        ReleaseService = new SimulationSessionReleaseService(
            client,
            SessionStore,
            CharacterStateTestData.SessionStores(ItemStates),
            metrics: Presence);
        var host = new RealtimeHostDependencies(
            Config,
            new SimulationJoinService(
                client,
                SessionStore,
                ItemStates,
                Config,
                identity,
                releaseService: ReleaseService,
                presenceMetrics: Presence),
            ReleaseService,
            SessionStore,
            ItemStates,
            Entities,
            Bindings,
            new RealtimeTransportReadiness(),
            new DurableCorpseStore(TimeProvider.System),
            new CorpseViewerRegistry(),
            staticCollision,
            CollisionWorld,
            InterestManager: interestManager,
            WorkerIdentity: WorldActors is null ? null : identity,
            WorldActorStore: WorldActors,
            ShardRules: shardRules,
            GameServiceAvailability: availability,
            PresenceMetrics: Presence);
        var clock = new RealtimeSimulationClock();
        LoopGuard = new RealtimeLoopGuard(
            Config.TickPhaseFailureLimit,
            clock,
            performanceMetrics,
            NullLogger.Instance);
        Core = RealtimeServerComposition.CreateCore(
            host,
            Directory,
            new PeerAdmissionGuard(Config.Admission),
            networkMetrics,
            performanceMetrics,
            clock,
            LoopGuard,
            availability,
            NullLogger.Instance);

        var dependencies = new CombatDependencies(
            rules,
            itemCatalog,
            worldActors,
            client,
            null,
            null,
            new RealtimeCombatTests.LowestRoll());
        var appearance = new AppearanceFeature(Core, AppearanceDependencies.CreateDefault());
        var quests = new QuestProgressFeature(QuestDependencies.CreateDefault());
        var playerCombat = new PlayerCombatFeature(Core, dependencies, quests);
        PlayerCombat = playerCombat.Authority;
        if (sessions)
        {
            ItemFeature.Add(Core, new ItemDependencies(new SimulationItemInteractionService(client)), playerCombat);
        }

        var corpses = new CorpseFeature(
            Core,
            new CorpseDependencies(new SimulationCorpseInteractionService(client)),
            appearance,
            playerCombat);
        var worldActorFeature = new WorldActorFeature(
            Core,
            new WorldActorDependencies(WorldActors is null ? null : new WorldActorActivityScheduler(WorldActors)),
            quests,
            playerCombat);
        if (sessions)
        {
            SessionFeature.Add(Core, playerCombat);
        }

        var combat = new CombatFeature(Core, dependencies, playerCombat, quests, corpses, worldActorFeature);
        if (sessions)
        {
            PersistenceFeature.Add(
                Core,
                checkpoints ? new PersistenceDependencies(client) : PersistenceDependencies.CreateDefault(),
                ReleaseService.FinalStates,
                combat);
        }

        Combat = combat.Combat;
        Mobs = combat.Mobs;
        Core.Presence.Appearances = appearance.Replicator;
        Core.Presence.IsActorMoving = Mobs.IsMoving;
        if (WorldActors is not null)
        {
            // The Mobs find the players in the grid that the movement feature builds every tick.
            MovementFeature.Add(Core, combat);
            worldActorFeature.Driver.Activated = Combat.RegisterWorldActors;
            worldActorFeature.Driver.RespawnMob = combat.MobDeaths.Respawn;
            worldActorFeature.Driver.KeepActive = Mobs.IsBusy;
            worldActorFeature.Driver.Activate();
        }

        if (snapshots)
        {
            // The interest refresh, the far states, and the snapshots, as the worker runs them.
            SnapshotFeature.Add(Core, combat);
        }

        loop = new SimulationTickLoop(
            Core.Clock,
            Entities,
            Core.Registries.TickPhases,
            performanceMetrics,
            LoopGuard,
            NullLogger.Instance);
        completionProcessor = new RealtimeCompletionProcessor(
            Core.Completions,
            Core.Peers,
            Core.Sender,
            NullLogger.Instance);
        supervisor = new RealtimePeerSupervisor(
            Config,
            Core.Peers,
            Directory,
            Core.Sender,
            SessionStore,
            Core.Teardown,
            Core.Bodies,
            Core.SessionOperations,
            networkMetrics,
            NullLogger.Instance,
            availability,
            playerCombat.Authority.HoldsDurableWork);
    }

    public SimulationWorkerConfig Config { get; }

    public GameServiceAvailability Availability { get; }

    public PlayerCombatAuthority PlayerCombat { get; }

    public ICollisionWorld CollisionWorld { get; }

    public ScriptedGameServiceHandler GameService { get; } = new();

    public RecordingPeerDirectory Directory { get; } = new();

    public SimulationSessionReleaseService ReleaseService { get; }

    public ActiveSimulationSessionStore SessionStore { get; }

    // What the presence features count.
    public PresenceMetrics Presence { get; } = new();

    public CharacterItemStateStore ItemStates { get; } = new();

    public SimulationEntityRegistry Entities { get; } = new();

    public ConnectionEntityBindingRegistry Bindings { get; } = new();

    public RealtimeLoopGuard LoopGuard { get; }

    public RealtimeCoreServices Core { get; }

    public CombatSystem Combat { get; }

    public MobSystem Mobs { get; }

    // The world actors, or null without them.
    public WorldActorStore? WorldActors { get; }

    public uint ServerTick => Core.Clock.ServerTick;

    public int TickRateHz => Config.MovementSimulation.TickRateHz;

    // A player that joined at the movement spawn with the given equipment, bound to a peer and
    // known to combat, as the join leaves it.
    public CombatTickPlayer Join(int peerId, string name, params SimulationEquippedItem[] equipment)
    {
        var session = RealtimeTestSessions.Create(name);
        Assert.Equal(ActiveSimulationSessionRegistration.Joined, SessionStore.Register(session));
        ItemStates.Register(
            session.CharacterId,
            session.SimulationSessionId,
            CharacterStateTestData.ItemState(session.CarryState, equipment),
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
                    CollisionWorld))
            .Entity;
        Bindings.Bind(peerId, entity.NetworkEntityId);
        var peer = new RecordingPeerConnection(peerId, isSealed: true);
        Directory.Peers[peerId] = peer;
        var context = new RealtimePeerContext(peerId, DateTime.UtcNow, Config.UdpQuotas)
        {
            Session = session
        };
        Core.Peers.Register(context);
        Core.Presence.TrackJoiningEntity(entity, true);
        Core.Registries.Hooks.RaisePlayerJoined(context, entity);
        var player = new CombatTickPlayer(context, peer, entity);
        players.Add(player);
        return player;
    }

    // Every player sees every other and receives their spawns, each followed by its combat state.
    public void ShowEveryoneToEveryone()
    {
        foreach (var player in players)
        {
            ShowWorldTo(player);
        }
    }

    // The player's interest is computed again and it receives the spawns of everything it sees,
    // as a join sends them: what its refresh brought in nearest first, then what it saw already.
    public void ShowWorldTo(CombatTickPlayer player)
    {
        var update = Core.Presence.RefreshConnection(player.Context.PeerId, player.EntityId);
        var entered = update.EnteredNearestFirst.ToArray();
        Core.Presence.SendEntityBaseline(
            player.Peer,
            [.. entered, .. Core.Presence.GetVisibleOrdered(player.Context.PeerId).Except(entered)]);
    }

    public void SendCombatIntent(CombatTickPlayer player, RealtimeCombatIntent intent)
    {
        Assert.True(RealtimeProtocol.TryGetDescriptor(RealtimeMessageType.CombatIntent, out var descriptor));
        Combat.HandleIntent(new RealtimeInboundMessage(
            player.Peer,
            player.Context,
            RealtimeMessageType.CombatIntent,
            RealtimeProtocol.EncodeCombatIntent(intent),
            descriptor.Channel,
            DeliveryMethod.ReliableOrdered));
    }

    // One due tick per call, so the loop never resynchronizes.
    public void RunTick()
    {
        loop.ProcessDueTicks(nextTick, TimeSpan.FromSeconds(1d / TickRateHz), ref nextTick);
    }

    // Puts the player where it stands still at the given place on the ground.
    public void Teleport(CombatTickPlayer player, float x, float z, float yawDegrees)
    {
        player.Entity.Movement.Teleport(PlayerMovementSimulation.CreateInitialState(
            Config.MovementSimulation,
            CollisionWorld,
            x,
            0f,
            z,
            yawDegrees));
    }

    // The player's connection ends while its session goes on, so it stays as a lingering body.
    public void Leave(CombatTickPlayer player)
    {
        Assert.True(Core.Teardown.LeaveBody(player.Context, player.Context.Session!));
    }

    // A new peer that has not joined yet.
    public (RealtimePeerContext Context, RecordingPeerConnection Peer) Connect(int peerId)
    {
        var peer = new RecordingPeerConnection(peerId);
        Directory.Peers[peerId] = peer;
        var context = new RealtimePeerContext(peerId, DateTime.UtcNow, Config.UdpQuotas);
        Core.Peers.Register(context);
        return (context, peer);
    }

    // The peer sends its join request; GameService answers the ticket off the loop, and the loop
    // finalizes the join when the test processes the completions.
    public void RequestJoin(RealtimePeerContext context, RecordingPeerConnection peer, string ticket)
    {
        var route = Core.Registries.Routes.Unauthenticated.Single(value => value.Type == RealtimeMessageType.JoinRequest);
        route.Handler(new RealtimeInboundMessage(
            peer,
            context,
            RealtimeMessageType.JoinRequest,
            RealtimeTestTickets.EncodeJoin(ticket),
            route.Channel,
            route.Delivery));
    }

    // A joined player sends an item operation through its route.
    public void SendItemIntent(CombatTickPlayer player, RealtimeItemOperationIntent intent)
    {
        var route = Core.Registries.Routes.Authenticated.Single(value => value.Type == RealtimeMessageType.ItemOperationIntent);
        route.Handler(new RealtimeInboundMessage(
            player.Peer,
            player.Context,
            RealtimeMessageType.ItemOperationIntent,
            RealtimeProtocol.EncodeItemOperationIntent(intent),
            route.Channel,
            route.Delivery));
    }

    // Waits for every GameService call that the loop started so far.
    public Task WaitForOperationsAsync()
    {
        return Core.OperationTracker.WaitForAllAsync();
    }

    // Finalizes the completed operations on the loop, in the order they completed.
    public void ProcessCompletions()
    {
        completionProcessor.ProcessCompleted(TimeSpan.FromSeconds(10));
    }

    // Runs the orphan sweep of the peer supervisor, which looks at most once a second.
    public void SweepOrphans()
    {
        supervisor.ReleaseOrphanedSessions();
    }

    // Runs the supervision that ends the loop iteration: it ends the connection of a peer whose
    // session was replaced or ended, or whose reliable queues hold too many packets.
    public void EnforcePeerState()
    {
        supervisor.EnforcePeerState();
    }

    // The realtime shutdown after the transport stopped: the calls in flight finish, and the
    // registered steps flush and clear in their order.
    public async Task RunShutdownStepsAsync()
    {
        await Core.OperationTracker.WaitForAllAsync();
        foreach (var step in Core.Registries.ShutdownSteps.Steps)
        {
            await step.Run();
        }
    }

    public Combatant CombatantOf(CombatTickPlayer player)
    {
        Assert.True(Combat.TryGetPlayerCombatant(player.EntityId, out var combatant));
        return combatant;
    }

    public void Dispose()
    {
        httpClient.Dispose();
        networkMetrics.Dispose();
        Presence.Dispose();
        performanceMetrics.Dispose();
    }
}

internal sealed record CombatTickPlayer(
    RealtimePeerContext Context,
    RecordingPeerConnection Peer,
    PlayerSimulationEntity Entity)
{
    public ulong EntityId => Entity.NetworkEntityId;

    // Every message of the type that the player's peer received, decoded, oldest first.
    public IReadOnlyList<T> Received<T>(RealtimeMessageType type, RealtimeTestDecoder<T> decode)
    {
        return Peer.Sent
            .Where(sent => sent.Type == type)
            .Select(sent =>
            {
                Assert.True(decode(sent.Data, out var value, out var error), error);
                return value;
            })
            .ToArray();
    }

    // The combat states of one combatant that the player received, oldest first.
    public IReadOnlyList<RealtimeCombatantState> StatesOf(ulong entityId)
    {
        return Received<RealtimeCombatantState>(RealtimeMessageType.CombatantState, RealtimeProtocol.TryDecodeCombatantState)
            .Where(state => state.EntityId == entityId)
            .ToArray();
    }

    public IReadOnlyList<RealtimeOwnerCombatState> OwnerStates()
    {
        return Received<RealtimeOwnerCombatState>(RealtimeMessageType.OwnerCombatState, RealtimeProtocol.TryDecodeOwnerCombatState);
    }
}
