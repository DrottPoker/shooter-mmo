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
// every message after every tick without sockets or loop timing. Players join directly, without
// the join handshake, and see each other once ShowEveryoneToEveryone sent their spawns. With
// world actors, their runtime is activated and the Mobs think and walk in the ticks.
internal sealed class CombatTickFixture : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly RealtimeNetworkMetrics networkMetrics = new();
    private readonly RealtimePerformanceMetrics performanceMetrics = new();
    private readonly SimulationTickLoop loop;
    private readonly List<CombatTickPlayer> players = [];
    private TimeSpan nextTick = TimeSpan.Zero;

    public CombatTickFixture(
        CombatRules rules,
        ItemCatalogRuntimeDocument itemCatalog,
        WorldActorRuntime? worldActors = null)
    {
        Config = RealtimeServerHarness.CreateConfig(27015, new RealtimeServerHarnessOptions());
        var staticCollision = CollisionTestWorldFactory.Create();
        CollisionWorld = new CompositeCollisionWorld(
            staticCollision,
            new DynamicCollisionWorld(staticCollision.ChunkSize));
        httpClient = new HttpClient(GameService) { BaseAddress = new Uri("http://game-service.test") };
        var availability = new GameServiceAvailability(
            TimeProvider.System,
            NullLogger<GameServiceAvailability>.Instance);
        var client = new GameServiceClient(httpClient, availability);
        var shardRules = new SimulationShardRules();
        shardRules.Set(new ShardRuleSet("test", PlayerVersusPlayer: true));
        var identity = new SimulationWorkerIdentity(RealtimeServerHarness.RuntimeId, DateTime.UtcNow);
        WorldActors = worldActors is null ? null : new WorldActorStore(worldActors, Entities);
        var host = new RealtimeHostDependencies(
            Config,
            new SimulationJoinService(
                client,
                SessionStore,
                ItemStates,
                Config,
                identity),
            new SimulationSessionReleaseService(
                client,
                SessionStore,
                CharacterStateTestData.SessionStores(ItemStates)),
            SessionStore,
            ItemStates,
            Entities,
            Bindings,
            new RealtimeTransportReadiness(),
            new DurableCorpseStore(TimeProvider.System),
            new CorpseViewerRegistry(),
            staticCollision,
            CollisionWorld,
            WorkerIdentity: WorldActors is null ? null : identity,
            WorldActorStore: WorldActors,
            ShardRules: shardRules,
            GameServiceAvailability: availability);
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
        var combat = new CombatFeature(Core, dependencies, playerCombat, quests, corpses, worldActorFeature);
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

        loop = new SimulationTickLoop(
            Core.Clock,
            Entities,
            Core.Registries.TickPhases,
            performanceMetrics,
            LoopGuard,
            NullLogger.Instance);
    }

    public SimulationWorkerConfig Config { get; }

    public ICollisionWorld CollisionWorld { get; }

    public ScriptedGameServiceHandler GameService { get; } = new();

    public RecordingPeerDirectory Directory { get; } = new();

    public ActiveSimulationSessionStore SessionStore { get; } = new();

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
        var peer = new RecordingPeerConnection(peerId);
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

    // The player's interest is computed again and it receives the spawns of everything it sees.
    public void ShowWorldTo(CombatTickPlayer player)
    {
        Core.Presence.RefreshConnection(player.Context.PeerId, player.EntityId);
        Core.Presence.SendEntityBaseline(
            player.Peer,
            new HashSet<ulong>(Core.Presence.GetVisibleOrdered(player.Context.PeerId)));
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

    public Combatant CombatantOf(CombatTickPlayer player)
    {
        Assert.True(Combat.TryGetPlayerCombatant(player.EntityId, out var combatant));
        return combatant;
    }

    public void Dispose()
    {
        httpClient.Dispose();
        networkMetrics.Dispose();
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
