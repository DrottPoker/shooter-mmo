using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing.Content;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Builds the loop-confined realtime components in memory around recording peers, so a
// test can drive one handler deterministically without sockets or loop timing.
internal sealed class RealtimeLoopFixture : IDisposable
{
    public RealtimeLoopFixture(
        bool enableWorldActors = false,
        IReadOnlyList<IWorldActorCapabilityHandler>? capabilityHandlers = null)
    {
        Config = RealtimeServerHarness.CreateConfig(27015, new RealtimeServerHarnessOptions());
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
            Clock);
        CarrySync = new CarryStateSynchronizer(CarryStateStore, Bindings, Registry, Sender);
        Teardown = new RealtimeSessionTeardown(
            Registry,
            Sender,
            Presence,
            SessionStore,
            CarryStateStore,
            CorpseViewers,
            Leases,
            Bindings,
            Entities,
            Metrics);
        Scheduler = new AuthorityOperationScheduler(Registry);
    }

    public SimulationWorkerConfig Config { get; }

    public ChunkedStaticCollisionWorld StaticCollisionWorld { get; }

    public ICollisionWorld CollisionWorld { get; }

    public RecordingPeerDirectory Directory { get; } = new();

    public RealtimeNetworkMetrics Metrics { get; } = new();

    public RealtimeSimulationClock Clock { get; } = new();

    public ActiveSimulationSessionStore SessionStore { get; } = new();

    public CarryStateStore CarryStateStore { get; } = new();

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

    public AuthorityOperationScheduler Scheduler { get; }

    // Registers a joined player at the movement spawn, bound to a connected peer.
    public ConnectedPlayer ConnectPlayer(int peerId, string name = "Loop Hero")
    {
        var session = RealtimeTestSessions.Create(name);
        Assert.Equal(ActiveSimulationSessionRegistration.Joined, SessionStore.Register(session));
        CarryStateStore.Register(
            session.CharacterId,
            session.SimulationSessionId,
            session.CarryState,
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
            CarryStateStore,
            Entities,
            CarrySync,
            Teardown,
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
            Entities,
            CarrySync,
            Teardown,
            Scheduler,
            OperationTracker,
            CreateWorldInteractionOperations(),
            gathering ?? CreateGatheringHandler());
    }

    public GatheringHandler CreateGatheringHandler(SimulationGatheringService? gatheringService = null)
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
            CarryStateStore,
            Entities,
            CarrySync,
            Teardown,
            Scheduler,
            OperationTracker,
            new GatheringAuthorityOperations(gatheringService, Completions, NullLogger.Instance),
            WorldActorMetrics);
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
        Metrics.Dispose();
        WorldActorMetrics?.Dispose();
    }
}

internal sealed record ConnectedPlayer(
    RealtimePeerContext Context,
    RecordingPeerConnection Peer,
    PlayerSimulationEntity Entity,
    ActiveSimulationSession Session);
