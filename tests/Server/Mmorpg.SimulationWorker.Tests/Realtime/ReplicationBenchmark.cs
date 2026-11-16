using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Text.Json.Serialization;
using LiteNetLib;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing;
using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A rectangle or circle on the World's ground in which entities start and walk.
internal sealed record ReplicationArea(string Name, float CenterX, float CenterZ, float HalfSizeX, float HalfSizeZ, bool IsCircle)
{
    public static ReplicationArea Rectangle(string name, float minimumX, float minimumZ, float maximumX, float maximumZ)
    {
        return new ReplicationArea(
            name,
            (minimumX + maximumX) / 2f,
            (minimumZ + maximumZ) / 2f,
            (maximumX - minimumX) / 2f,
            (maximumZ - minimumZ) / 2f,
            false);
    }

    public static ReplicationArea Circle(string name, float centerX, float centerZ, float radius)
    {
        return new ReplicationArea(name, centerX, centerZ, radius, radius, true);
    }

    // A point inside, by rejection inside the bounding square for a circle.
    public (float X, float Z) Sample(ReplicationRandom random)
    {
        while (true)
        {
            var offsetX = random.Range(-HalfSizeX, HalfSizeX);
            var offsetZ = random.Range(-HalfSizeZ, HalfSizeZ);
            if (!IsCircle || (offsetX * offsetX) + (offsetZ * offsetZ) <= HalfSizeX * HalfSizeX)
            {
                return (CenterX + offsetX, CenterZ + offsetZ);
            }
        }
    }
}

// Count entities that start in Start and walk their legs to points of Legs.
internal sealed record ReplicationGroup(string Role, int Count, ReplicationArea Start, ReplicationArea Legs);

// One seeded layout: players and Mobs by group, the shares of them that walk, and the ticks of
// the warm-up and of the measurement.
internal sealed record ReplicationLayout(
    string Name,
    ulong Seed,
    IReadOnlyList<ReplicationGroup> Players,
    IReadOnlyList<ReplicationGroup> Mobs,
    double MovingPlayerShare,
    double MovingMobShare,
    int WarmUpTicks,
    int MeasuredTicks)
{
    public const float MobWalkSpeed = 1.4f;

    // The ground of Development World 2 that the walkers of playing-500 roam.
    private static readonly ReplicationArea World = ReplicationArea.Rectangle("world", -180f, -180f, 180f, 180f);

    // The playing-500 workload on Development World 2, from tools/load/scenarios/playing-500.json:
    // 40 percent fighters in the eight hunting grounds, 20 percent gatherers in the six node
    // fields, 30 percent walkers that start on the three roads and roam the World, and 10 percent
    // traders in the town, with 180 Mobs in the hunting grounds' wolf areas.
    public static ReplicationLayout Spread500 { get; } = CreateSpread500();

    // 200 players inside one 30 m circle.
    public static ReplicationLayout Crowd200 { get; } = Crowd("crowd-200", 200, 30f, 2002);

    // 500 players inside one 60 m circle.
    public static ReplicationLayout Crowd500 { get; } = Crowd("crowd-500", 500, 60f, 5005);

    public static IReadOnlyList<ReplicationLayout> Measured { get; } = [Spread500, Crowd200, Crowd500];

    // The small layout of the fast test: 40 players in two groups 150 m apart and 12 Mobs, with an
    // aggregate snapshot budget small enough to defer updates as 500 players do.
    public static ReplicationLayout Small { get; } = new(
        "small-40",
        40,
        [
            new ReplicationGroup("town", 25, ReplicationArea.Circle("town", 0f, 0f, 20f), ReplicationArea.Circle("town", 0f, 0f, 20f)),
            new ReplicationGroup("field", 15, ReplicationArea.Rectangle("field", 130f, -20f, 170f, 20f), ReplicationArea.Rectangle("field", 130f, -20f, 170f, 20f))
        ],
        [
            new ReplicationGroup("mobs", 12, ReplicationArea.Rectangle("wolves", 132f, -18f, 168f, 18f), ReplicationArea.Rectangle("wolves", 132f, -18f, 168f, 18f))
        ],
        0.6,
        0.5,
        30,
        90)
    {
        AggregateSnapshotBytesPerSecond = 150_000
    };

    // Replaces the aggregate snapshot budget of the worker's appsettings.json when set.
    public int? AggregateSnapshotBytesPerSecond { get; init; }

    public int PlayerCount => Players.Sum(group => group.Count);

    public int MobCount => Mobs.Sum(group => group.Count);

    private static ReplicationLayout Crowd(string name, int players, float radius, ulong seed)
    {
        var circle = ReplicationArea.Circle("crowd", 0f, 0f, radius);
        return new ReplicationLayout(name, seed, [new ReplicationGroup("crowd", players, circle, circle)], [], 0.6, 0, 90, 900);
    }

    private static ReplicationLayout CreateSpread500()
    {
        ReplicationArea[] hunting =
        [
            ReplicationArea.Rectangle("west-field", -150, -60, -110, -20),
            ReplicationArea.Rectangle("south-field", -40, -170, 0, -130),
            ReplicationArea.Rectangle("southwest-field", -110, -180, -70, -140),
            ReplicationArea.Rectangle("southeast-field", 30, -180, 70, -140),
            ReplicationArea.Rectangle("east-field", 130, -70, 170, -30),
            ReplicationArea.Rectangle("far-southeast-field", 130, -180, 170, -140),
            ReplicationArea.Rectangle("highland-field", 70, 70, 110, 110),
            ReplicationArea.Rectangle("northwest-field", -180, 20, -140, 60)
        ];
        ReplicationArea[] nodes =
        [
            ReplicationArea.Rectangle("meadow-veins", 47.0f, -50.5f, 73.0f, -29.5f),
            ReplicationArea.Rectangle("road-veins", -113.0f, 19.5f, -87.0f, 40.5f),
            ReplicationArea.Rectangle("gate-veins", -23.0f, -70.5f, 3.0f, -49.5f),
            ReplicationArea.Rectangle("east-veins", 137.0f, -10.5f, 163.0f, 10.5f),
            ReplicationArea.Rectangle("north-oaks", -27.0f, 151.5f, 7.0f, 178.5f),
            ReplicationArea.Rectangle("east-oaks", 133.0f, -113.5f, 167.0f, -86.5f)
        ];
        ReplicationArea[] roads =
        [
            ReplicationArea.Rectangle("north-road", -10, 44, 10, 60),
            ReplicationArea.Rectangle("east-road", 44, -10, 60, 10),
            ReplicationArea.Rectangle("west-road", -60, -8, -44, 8)
        ];
        var town = ReplicationArea.Rectangle("town", -24, -24, 24, 4);
        var players = new List<ReplicationGroup>();
        // Each role's bots go in turn over the grounds of its kind, as the workload assigns them.
        players.AddRange(Spread("fighter", 200, hunting, legsInWorld: false));
        players.AddRange(Spread("gatherer", 100, nodes, legsInWorld: false));
        players.AddRange(Spread("walker", 150, roads, legsInWorld: true));
        players.Add(new ReplicationGroup("trader", 50, town, town));
        // The wolf areas lie 2 m inside the hunting grounds.
        var wolfAreas = hunting
            .Select(ground => ground with { HalfSizeX = ground.HalfSizeX - 2f, HalfSizeZ = ground.HalfSizeZ - 2f })
            .ToArray();
        return new ReplicationLayout(
            "spread-500",
            500,
            players,
            [.. Spread("mob", 180, wolfAreas, legsInWorld: false)],
            0.6,
            0.2,
            90,
            900);
    }

    private static IEnumerable<ReplicationGroup> Spread(string role, int count, ReplicationArea[] areas, bool legsInWorld)
    {
        for (var index = 0; index < areas.Length; index++)
        {
            var share = (count / areas.Length) + (index < count % areas.Length ? 1 : 0);
            yield return new ReplicationGroup(role, share, areas[index], legsInWorld ? World : areas[index]);
        }
    }
}

// SplitMix64, so a seed gives the same numbers on every runtime and machine.
internal sealed class ReplicationRandom(ulong seed)
{
    private ulong state = seed;

    public ulong NextUInt64()
    {
        var value = state += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    public double NextDouble()
    {
        return (NextUInt64() >> 11) * (1d / (1UL << 53));
    }

    public float Range(float minimum, float maximum)
    {
        return (float)(minimum + (NextDouble() * (maximum - minimum)));
    }

    public Guid NextGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, NextUInt64());
        BitConverter.TryWriteBytes(bytes[8..], NextUInt64());
        return new Guid(bytes);
    }
}

// A clock that stands still until the benchmark advances it by one tick, so the per-peer and
// aggregate byte budgets refill exactly as at the tick rate, however fast the ticks run.
internal sealed class ReplicationClock(int tickRateHz) : TimeProvider
{
    private const long Frequency = 3_000_000;
    private static readonly DateTimeOffset Epoch = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
    private readonly long ticksPerTick = Frequency % tickRateHz == 0
        ? Frequency / tickRateHz
        : throw new ArgumentOutOfRangeException(nameof(tickRateHz));
    private long timestamp;

    public override long TimestampFrequency => Frequency;

    public override long GetTimestamp() => timestamp;

    public override DateTimeOffset GetUtcNow() => Epoch.AddSeconds(timestamp / (double)Frequency);

    public void AdvanceOneTick()
    {
        timestamp += ticksPerTick;
    }
}

// The transport double: a peer that counts what the worker sends to it, by delivery method, and
// the entity records of the snapshots among it, and discards the packets. It copies the records
// of the remote snapshots, as a transport copies a packet, into a buffer that keeps its capacity,
// and the benchmark reads their entity ids after the tick, so the peer adds neither work beyond
// the copy nor an allocation of its own to the loop that the benchmark measures.
internal sealed class CountingPeerConnection(int id) : IRealtimePeerConnection
{
    private const int RecordIdOffset = 0;

    private byte[] receivedRecords = new byte[16 * 1024];
    private int receivedLength;

    public int Id { get; } = id;

    public long ReliablePackets { get; private set; }

    public long ReliableBytes { get; private set; }

    public long UnreliablePackets { get; private set; }

    public long UnreliableBytes { get; private set; }

    public long OwnerSnapshots { get; private set; }

    public long RemoteRecords { get; private set; }

    public void Send(byte[] data, DeliveryMethod deliveryMethod)
    {
        Send(new ReadOnlySpan<byte>(data), deliveryMethod);
    }

    public void Send(ReadOnlySpan<byte> data, DeliveryMethod deliveryMethod)
    {
        if (deliveryMethod == DeliveryMethod.Unreliable)
        {
            UnreliablePackets++;
            UnreliableBytes += data.Length;
        }
        else
        {
            ReliablePackets++;
            ReliableBytes += data.Length;
        }

        if (!RealtimeProtocol.TryReadMessageType(data, out var type))
        {
            throw new InvalidOperationException("The worker sent a packet without a message type.");
        }

        if (type == RealtimeMessageType.OwnerSimulationSnapshot)
        {
            OwnerSnapshots++;
        }
        else if (type == RealtimeMessageType.SimulationSnapshot)
        {
            var payload = data.Length - RealtimeProtocol.SimulationSnapshotPacketHeaderSize;
            if (payload % RealtimeProtocol.SimulationSnapshotEntitySize != 0)
            {
                throw new InvalidOperationException("A remote snapshot holds a partial entity record.");
            }

            RemoteRecords += payload / RealtimeProtocol.SimulationSnapshotEntitySize;
            if (receivedLength + payload > receivedRecords.Length)
            {
                Array.Resize(ref receivedRecords, Math.Max(receivedRecords.Length * 2, receivedLength + payload));
            }

            data[RealtimeProtocol.SimulationSnapshotPacketHeaderSize..].CopyTo(receivedRecords.AsSpan(receivedLength));
            receivedLength += payload;
        }
    }

    // Adds the entity ids of the remote records since the last call to destination.
    public void TakeReceivedEntityIds(HashSet<ulong> destination)
    {
        for (var offset = 0; offset < receivedLength; offset += RealtimeProtocol.SimulationSnapshotEntitySize)
        {
            destination.Add(BinaryPrimitives.ReadUInt32LittleEndian(receivedRecords.AsSpan(offset + RecordIdOffset)));
        }

        receivedLength = 0;
    }

    public void SendDisconnectReason(byte[] data, RealtimePendingDisconnect disconnect)
    {
        Send(data, DeliveryMethod.ReliableOrdered);
    }

    public int GetPacketsCountInReliableQueue(byte channelNumber, bool ordered)
    {
        return 0;
    }

    public void Reset()
    {
        ReliablePackets = 0;
        ReliableBytes = 0;
        UnreliablePackets = 0;
        UnreliableBytes = 0;
        OwnerSnapshots = 0;
        RemoteRecords = 0;
        receivedLength = 0;
    }
}

internal sealed class CountingPeerDirectory : IRealtimePeerDirectory
{
    public Dictionary<int, CountingPeerConnection> Peers { get; } = [];

    public bool TryGetConnectedPeer(int peerId, [NotNullWhen(true)] out IRealtimePeerConnection? peer)
    {
        peer = Peers.TryGetValue(peerId, out var connection) ? connection : null;
        return peer is not null;
    }

    public void DisconnectPeer(IRealtimePeerConnection peer, byte[]? data)
    {
        throw new InvalidOperationException($"The benchmark disconnected peer {peer.Id}; a recipient failed.");
    }
}

// The numbers that depend only on the code and the layout, equal on every run and machine.
internal sealed record ReplicationDeterministicNumbers(
    long Snapshots,
    long SnapshotBytes,
    long ReliableBytes,
    long AllBytes,
    long UnreliablePackets,
    long ReliablePackets,
    long SnapshotRecords,
    long OwnerRecords,
    long RemoteRecords,
    long DeferredUpdates,
    // The key stays the one of the recorded baselines, so their fingerprint holds.
    [property: JsonPropertyName("visibilityGroups")] long RemoteRecipients,
    long PacketsEncoded,
    long SpawnPackets,
    long DespawnPackets,
    long DroppedSnapshotPackets,
    long NearMovingSightings,
    long NearMovingRecords,
    double SnapshotBytesPerPlayerPerSecond,
    double AllBytesPerPlayerPerSecond,
    double PacketsPerSecond,
    double RecordsPerPlayerPerSnapshot,
    double BytesPerRecord,
    double DeferredShare,
    double UpdateRateAverageHz,
    double UpdateRateP5Hz,
    double UpdateRateMinimumHz,
    double AllEntitiesUpdateRateAverageHz,
    double AllEntitiesUpdateRateP5Hz,
    double AllEntitiesUpdateRateMinimumHz,
    double NearestMovingUpdateRateHz,
    double FarMovingUpdateRateHz);

// The numbers that depend on the machine and what else runs on it; informative only.
internal sealed record ReplicationTimingNumbers(
    double WallSeconds,
    double TickAverageMs,
    double TickP99Ms,
    double InterestAverageMs,
    double InterestP99Ms,
    double BroadcastAverageMs,
    double BroadcastP99Ms,
    double AllocatedBytesPerTick,
    double Generation0PerSecond,
    double Generation1PerSecond,
    double Generation2PerSecond,
    double GcPauseMsPerSecond,
    IReadOnlyDictionary<string, double> PhaseAverageMs);

internal sealed record ReplicationLayoutResult(
    string Name,
    int Players,
    int Mobs,
    int MovingPlayers,
    int MovingMobs,
    int WarmUpTicks,
    int MeasuredTicks,
    double MeasuredSeconds,
    ReplicationDeterministicNumbers Deterministic,
    ReplicationTimingNumbers Timing);

// Builds the worker's replication objects the way RealtimeServerComposition builds them, on the
// worker's own appsettings.json, around counting peers and without UDP, GameService, or a
// database, and runs the tick phases that replication needs: a seeded motion in the place of
// movement and the Mob brains, and the snapshot broadcast with its interest refresh. Every
// timing it reports comes from the loop's own timers and histograms.
internal sealed class ReplicationBenchmark : IDisposable
{
    private const string MotionPhaseName = "benchmark motion";

    // The nearest walking entities, those a player watches most closely.
    private const float NearestRadius = 16f;

    private readonly ReplicationLayout layout;
    private readonly SimulationWorkerConfig config;
    private readonly ReplicationClock timeProvider;
    private readonly RealtimeSimulationClock clock = new();
    private readonly SimulationEntityRegistry entities = new();
    private readonly ConnectionEntityBindingRegistry bindings = new();
    private readonly CountingPeerDirectory directory = new();
    private readonly RealtimeNetworkMetrics networkMetrics = new();
    private readonly RealtimePerformanceMetrics performanceMetrics = new();
    private readonly HttpClient httpClient = new(new ScriptedGameServiceHandler()) { BaseAddress = new Uri("http://game-service.test") };
    private readonly WorldActorStore mobStore;
    private readonly EntityPresenceReplicator presence;
    private readonly SimulationTickLoop loop;
    private readonly MovingEntities players;
    private readonly MovingEntities mobs;
    private readonly List<CountingPeerConnection> recipients = [];
    private readonly List<ulong> recipientEntityIds = [];
    private readonly HashSet<ulong> entitiesWithState = [];
    private readonly HashSet<ulong> movingEntityIds = [];
    private readonly Dictionary<ulong, (float X, float Z)> positions = [];
    private readonly HashSet<ulong> received = [];
    private readonly MovingMobs mobSnapshots;
    private readonly int snapshotIntervalTicks;
    private TimeSpan nextTick = TimeSpan.Zero;

    public ReplicationBenchmark(ReplicationLayout layout)
    {
        this.layout = layout;
        config = LoadWorkerConfig();
        if (layout.AggregateSnapshotBytesPerSecond is { } aggregate)
        {
            config = config with { UdpQuotas = config.UdpQuotas with { AggregateSnapshotBytesPerSecond = aggregate } };
        }

        timeProvider = new ReplicationClock(config.MovementSimulation.TickRateHz);
        snapshotIntervalTicks = config.MovementSimulation.TickRateHz / config.SnapshotRateHz;
        var random = new ReplicationRandom(layout.Seed);
        var hooks = new RealtimeLifecycleHooks();
        var loopGuard = new RealtimeLoopGuard(config.TickPhaseFailureLimit, clock, performanceMetrics, NullLogger.Instance);
        var peers = new RealtimePeerRegistry(directory, bindings, entities, networkMetrics);
        var sender = new RealtimePeerSender(networkMetrics, NullLogger.Instance, peers, loopGuard);

        // World actors take their ids before the players, as at the worker's start.
        mobStore = CreateMobStore(layout, random, entities, config);
        presence = new EntityPresenceReplicator(
            peers,
            sender,
            entities,
            bindings,
            mobStore,
            new SimulationInterestManager(config.InterestManagement),
            networkMetrics,
            clock,
            hooks);
        mobs = new MovingEntities(
            mobStore.ListActors().Select(actor => actor.NetworkEntityId).ToArray(),
            layout.Mobs,
            layout.MovingMobShare,
            ReplicationLayout.MobWalkSpeed / config.MovementSimulation.TickRateHz,
            mobStore.ListActors().Select(actor => (actor.PositionX, actor.PositionZ)).ToArray(),
            random);
        mobSnapshots = new MovingMobs(mobs, mobStore);

        var collisionWorld = CollisionTestWorldFactory.Create();
        var sessionStore = new ActiveSimulationSessionStore();
        var itemStates = new CharacterItemStateStore();
        var playerIds = new List<ulong>();
        var starts = new List<(float X, float Z)>();
        var peerId = 0;
        foreach (var group in layout.Players)
        {
            for (var index = 0; index < group.Count; index++)
            {
                peerId++;
                var start = group.Start.Sample(random);
                var session = CreateSession(random, peerId, config);
                var state = new PlayerMovementState(start.X, 0f, start.Z, 0f, 0f, 0f, 0f, true);
                var entity = entities.RegisterPlayer(
                        session,
                        () => new AuthoritativePlayerMovement(state, config.MovementSimulation, session.CarryState, collisionWorld))
                    .Entity;
                bindings.Bind(peerId, entity.NetworkEntityId);
                var connection = new CountingPeerConnection(peerId);
                directory.Peers[peerId] = connection;
                peers.Register(new RealtimePeerContext(peerId, timeProvider.GetUtcNow().UtcDateTime, config.UdpQuotas, timeProvider)
                {
                    Session = session
                });
                recipients.Add(connection);
                recipientEntityIds.Add(entity.NetworkEntityId);
                playerIds.Add(entity.NetworkEntityId);
                starts.Add(start);
            }
        }

        players = new MovingEntities(
            [.. playerIds],
            layout.Players,
            layout.MovingPlayerShare,
            config.MovementSimulation.WalkSpeed / config.MovementSimulation.TickRateHz,
            [.. starts],
            random);
        entitiesWithState.UnionWith(playerIds);
        entitiesWithState.UnionWith(mobStore.ListActors().Select(actor => actor.NetworkEntityId));
        movingEntityIds.UnionWith(players.MovingIds);
        movingEntityIds.UnionWith(mobs.MovingIds);

        // The failure isolation of the composition; nothing fails in a benchmark run.
        var client = new GameServiceClient(httpClient);
        var releaseService = new SimulationSessionReleaseService(
            client,
            sessionStore,
            CharacterSessionStores.Create(itemStates, new CharacterLookStore(), new CharacterQuestLogStore()));
        var sessionOperations = new SessionAuthorityOperations(
            new SimulationJoinService(
                client,
                sessionStore,
                itemStates,
                config,
                new SimulationWorkerIdentity("replication-benchmark", DateTime.UnixEpoch)),
            releaseService,
            new RealtimeCompletionQueue(),
            new RealtimeOperationTracker(),
            NullLogger.Instance);
        var isolation = new RealtimeFailureIsolation(sessionStore, sender, loopGuard, sessionOperations, releaseService.FinalStates);
        isolation.AddHooks(hooks);

        var phases = new SimulationTickPhases();
        phases.Add(SimulationTickOrder.Movement, MotionPhaseName, (in SimulationTick _) => Move());
        new SnapshotReplicator(
            config,
            mobSnapshots,
            entities,
            bindings,
            peers,
            sender,
            presence,
            networkMetrics,
            performanceMetrics,
            clock,
            isolation,
            timeProvider).AddTickPhase(phases);
        loop = new SimulationTickLoop(clock, entities, phases, performanceMetrics, loopGuard, NullLogger.Instance);
    }

    public static SimulationWorkerConfig LoadWorkerConfig()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryPaths.Root, "src", "Server", "SimulationWorker", "Config", "appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SimulationWorker:WorldDataPath"] = RepositoryPaths.Worlds,
                ["SIMULATION_WORKER_SERVICE_SECRET"] = "replication-benchmark-secret-that-is-long-enough"
            })
            .Build();
        return SimulationWorkerConfig.FromConfiguration(configuration);
    }

    public ReplicationLayoutResult Run()
    {
        for (var tick = 0; tick < layout.WarmUpTicks; tick++)
        {
            RunTick();
        }

        // Only the measured ticks count.
        performanceMetrics.CaptureAndReset();
        var networkBefore = networkMetrics.Capture();
        foreach (var recipient in recipients)
        {
            recipient.Reset();
        }

        var remoteRecords = new long[recipients.Count];
        var visibleWithState = new long[recipients.Count];
        var nearMovingSightings = new long[recipients.Count];
        var nearMovingRecords = new long[recipients.Count];
        var nearRadiusSquared = config.InterestManagement.NearRadius * config.InterestManagement.NearRadius;
        var farRadiusSquared = config.InterestManagement.NearExitRadius * config.InterestManagement.NearExitRadius;
        var nearestRadiusSquared = NearestRadius * NearestRadius;
        var nearest = new MovingSightings();
        var far = new MovingSightings();
        using var timings = new TimingRecorder(layout.MeasuredTicks);
        var generation0 = GC.CollectionCount(0);
        var generation1 = GC.CollectionCount(1);
        var generation2 = GC.CollectionCount(2);
        var gcPause = GC.GetTotalPauseDuration();
        long allocated = 0;
        long snapshots = 0;
        var wall = Stopwatch.StartNew();
        for (var tick = 0; tick < layout.MeasuredTicks; tick++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var snapshot = RunTick();
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            if (!snapshot)
            {
                continue;
            }

            snapshots++;
            CapturePositions();
            for (var index = 0; index < recipients.Count; index++)
            {
                var recipient = recipients[index];
                var own = recipientEntityIds[index];
                var ownPosition = positions[own];
                received.Clear();
                recipient.TakeReceivedEntityIds(received);
                var visible = presence.GetVisibleOrdered(recipient.Id);
                for (var entity = 0; entity < visible.Count; entity++)
                {
                    var entityId = visible[entity];
                    if (entityId == own || !entitiesWithState.Contains(entityId))
                    {
                        continue;
                    }

                    visibleWithState[index]++;
                    if (!movingEntityIds.Contains(entityId))
                    {
                        continue;
                    }

                    // A walking entity near the recipient, by the worker's near radius, among
                    // them the nearest, and one that is always far, beyond the near exit radius.
                    var position = positions[entityId];
                    var deltaX = position.X - ownPosition.X;
                    var deltaZ = position.Z - ownPosition.Z;
                    var distanceSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
                    var got = received.Contains(entityId) ? 1 : 0;
                    if (distanceSquared <= nearRadiusSquared)
                    {
                        nearMovingSightings[index]++;
                        nearMovingRecords[index] += got;
                    }

                    if (distanceSquared <= nearestRadiusSquared)
                    {
                        nearest.Add(got);
                    }
                    else if (distanceSquared > farRadiusSquared)
                    {
                        far.Add(got);
                    }
                }
            }
        }

        wall.Stop();
        var gcPauseMilliseconds = (GC.GetTotalPauseDuration() - gcPause).TotalMilliseconds;
        var collections = (
            GC.CollectionCount(0) - generation0,
            GC.CollectionCount(1) - generation1,
            GC.CollectionCount(2) - generation2);
        var performance = performanceMetrics.CaptureAndReset();
        var network = networkMetrics.Capture();
        for (var index = 0; index < recipients.Count; index++)
        {
            remoteRecords[index] = recipients[index].RemoteRecords;
        }

        var seconds = layout.MeasuredTicks / (double)config.MovementSimulation.TickRateHz;
        return new ReplicationLayoutResult(
            layout.Name,
            layout.PlayerCount,
            layout.MobCount,
            players.MovingIds.Count,
            mobs.MovingIds.Count,
            layout.WarmUpTicks,
            layout.MeasuredTicks,
            seconds,
            Deterministic(snapshots, seconds, networkBefore, network, performance, remoteRecords, visibleWithState, nearMovingSightings, nearMovingRecords, nearest, far),
            new ReplicationTimingNumbers(
                wall.Elapsed.TotalSeconds,
                timings.Tick.Average,
                timings.Tick.P99,
                timings.Interest.Average,
                timings.Interest.P99,
                timings.Broadcast.Average,
                timings.Broadcast.P99,
                allocated / (double)layout.MeasuredTicks,
                collections.Item1 / seconds,
                collections.Item2 / seconds,
                collections.Item3 / seconds,
                gcPauseMilliseconds / seconds,
                performance.TickPhases.ToDictionary(
                    phase => phase.Name,
                    phase => phase.Summary.AverageMilliseconds,
                    StringComparer.Ordinal)));
    }

    public void Dispose()
    {
        httpClient.Dispose();
        networkMetrics.Dispose();
        performanceMetrics.Dispose();
    }

    private ReplicationDeterministicNumbers Deterministic(
        long snapshots,
        double seconds,
        RealtimeNetworkMetricsSnapshot before,
        RealtimeNetworkMetricsSnapshot after,
        RealtimePerformanceMetricsSnapshot performance,
        long[] remoteRecords,
        long[] visibleWithState,
        long[] nearMovingSightings,
        long[] nearMovingRecords,
        MovingSightings nearest,
        MovingSightings far)
    {
        var snapshotBytes = recipients.Sum(recipient => recipient.UnreliableBytes);
        var reliableBytes = recipients.Sum(recipient => recipient.ReliableBytes);
        var unreliablePackets = recipients.Sum(recipient => recipient.UnreliablePackets);
        var reliablePackets = recipients.Sum(recipient => recipient.ReliablePackets);
        var ownerRecords = recipients.Sum(recipient => recipient.OwnerSnapshots);
        var remote = remoteRecords.Sum();
        var records = after.SnapshotEntityRecords - before.SnapshotEntityRecords;
        if (records != ownerRecords + remote
            || snapshotBytes != after.SnapshotBytes - before.SnapshotBytes
            || snapshotBytes + reliableBytes != after.SentBytes - before.SentBytes)
        {
            throw new InvalidOperationException("The counting peers and the worker's network metrics disagree.");
        }

        // The records per entity with a state that each recipient saw, as the update rate was
        // measured before records went out only when due.
        var rates = new double[recipients.Count];
        for (var index = 0; index < rates.Length; index++)
        {
            rates[index] = visibleWithState[index] == 0
                ? config.SnapshotRateHz
                : config.SnapshotRateHz * (remoteRecords[index] / (double)visibleWithState[index]);
        }

        Array.Sort(rates);

        // The records per snapshot in which a walking entity was near, over the recipients that
        // ever had one near.
        var nearRates = Enumerable.Range(0, recipients.Count)
            .Where(index => nearMovingSightings[index] > 0)
            .Select(index => config.SnapshotRateHz * (nearMovingRecords[index] / (double)nearMovingSightings[index]))
            .Order()
            .ToArray();
        var playerCount = Math.Max(1, recipients.Count);
        var deferred = performance.SnapshotEntityUpdatesDeferred;
        return new ReplicationDeterministicNumbers(
            snapshots,
            snapshotBytes,
            reliableBytes,
            snapshotBytes + reliableBytes,
            unreliablePackets,
            reliablePackets,
            records,
            ownerRecords,
            remote,
            deferred,
            performance.SnapshotRemoteRecipients,
            performance.SnapshotPacketsEncoded,
            after.SpawnPackets - before.SpawnPackets,
            after.DespawnPackets - before.DespawnPackets,
            after.DroppedSnapshotPackets - before.DroppedSnapshotPackets,
            nearMovingSightings.Sum(),
            nearMovingRecords.Sum(),
            snapshotBytes / seconds / playerCount,
            (snapshotBytes + reliableBytes) / seconds / playerCount,
            (unreliablePackets + reliablePackets) / seconds,
            records / (double)playerCount / Math.Max(1, snapshots),
            records == 0 ? 0 : snapshotBytes / (double)records,
            deferred + remote == 0 ? 0 : deferred / (double)(deferred + remote),
            nearRates.Length == 0 ? 0 : nearRates.Average(),
            P5(nearRates),
            nearRates.Length == 0 ? 0 : nearRates[0],
            rates.Length == 0 ? 0 : rates.Average(),
            P5(rates),
            rates.Length == 0 ? 0 : rates[0],
            nearest.RateHz(config.SnapshotRateHz),
            far.RateHz(config.SnapshotRateHz));
    }

    // The snapshots in which a walking entity lay in one distance band of a recipient, and how
    // many of them carried its record.
    private sealed class MovingSightings
    {
        private long sightings;
        private long records;

        public void Add(int got)
        {
            sightings++;
            records += got;
        }

        public double RateHz(int snapshotRateHz)
        {
            return sightings == 0 ? 0 : snapshotRateHz * (records / (double)sightings);
        }
    }

    private static double P5(double[] sorted)
    {
        return sorted.Length == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.05) - 1)];
    }

    // Where every player and Mob stands after the tick.
    private void CapturePositions()
    {
        positions.Clear();
        foreach (var recipientEntityId in players.AllIds)
        {
            if (entities.TryGetPlayer(recipientEntityId, out var player))
            {
                positions[recipientEntityId] = (player!.Movement.State.PositionX, player.Movement.State.PositionZ);
            }
        }

        foreach (var actor in mobStore.ListActors())
        {
            positions[actor.NetworkEntityId] = (actor.PositionX, actor.PositionZ);
        }
    }

    // Advances the clock and runs one tick; true when the tick broadcast a snapshot.
    private bool RunTick()
    {
        timeProvider.AdvanceOneTick();
        loop.ProcessDueTicks(nextTick, TimeSpan.FromSeconds(1d / config.MovementSimulation.TickRateHz), ref nextTick);
        return clock.ServerTick % (uint)snapshotIntervalTicks == 0;
    }

    private void Move()
    {
        players.Step();
        for (var index = 0; index < players.MovingIds.Count; index++)
        {
            var mover = players.Movers[index];
            if (entities.TryGetPlayer(mover.EntityId, out var player))
            {
                player!.Movement.Teleport(new PlayerMovementState(
                    mover.X,
                    0f,
                    mover.Z,
                    mover.VelocityX,
                    0f,
                    mover.VelocityZ,
                    mover.YawDegrees,
                    true));
            }
        }

        // A walking Mob's actor holds its pose, which interest reads, as MobSystem keeps it.
        mobs.Step();
        foreach (var mover in mobs.Movers)
        {
            if (mobStore.TryGetByEntityId(mover.EntityId, out var actor))
            {
                actor!.SetPose(mover.X, 0f, mover.Z, mover.YawDegrees);
            }
        }
    }

    private static WorldActorStore CreateMobStore(
        ReplicationLayout layout,
        ReplicationRandom random,
        SimulationEntityRegistry entities,
        SimulationWorkerConfig config)
    {
        // The store orders spawns by id, so the ids keep the order of the groups.
        var spawns = new List<WorldActorSpawnInstanceDefinition>();
        for (var groupIndex = 0; groupIndex < layout.Mobs.Count; groupIndex++)
        {
            var group = layout.Mobs[groupIndex];
            for (var index = 0; index < group.Count; index++)
            {
                var position = group.Start.Sample(random);
                spawns.Add(new WorldActorSpawnInstanceDefinition
                {
                    Id = $"benchmark.{groupIndex:D2}.{index:D4}",
                    SpawnDefinitionId = $"benchmark.{group.Start.Name}",
                    ActorId = BenchmarkMob.Id,
                    X = position.X,
                    Y = 0f,
                    Z = position.Z,
                    YawDegrees = 0f,
                    PatrolPathId = string.Empty,
                    StructuralFingerprint = string.Empty
                });
            }
        }

        var runtime = new WorldActorRuntime(
            BenchmarkMob.Catalog,
            new WorldActorSpawnRuntimeDocument
            {
                FormatVersion = 1,
                CatalogId = BenchmarkMob.Catalog.CatalogId,
                WorldId = config.WorldId,
                Revision = "replication-benchmark",
                SpawnSources = [],
                SpawnInstances = [.. spawns],
                PatrolPaths = []
            });
        var store = new WorldActorStore(runtime, entities, random.NextGuid);
        store.ActivateAssignment(config.WorldId, config.ShardId, "replication-benchmark");
        return store;
    }

    private static ActiveSimulationSession CreateSession(ReplicationRandom random, int index, SimulationWorkerConfig config)
    {
        return new ActiveSimulationSession(
            random.NextGuid(),
            random.NextGuid().ToString("N"),
            random.NextGuid(),
            random.NextGuid(),
            $"Benchmark {index:D4}",
            config.ShardId,
            config.WorldId,
            config.SimulationWorkerId,
            "replication-benchmark",
            DateTime.UnixEpoch,
            DateTime.UnixEpoch.AddDays(1),
            new PlayerCarryState(0, 0, 200),
            false);
    }

    // One walking or standing entity of the seeded motion.
    internal struct Mover
    {
        public ulong EntityId;
        public float X;
        public float Z;
        public float TargetX;
        public float TargetZ;
        public float VelocityX;
        public float VelocityZ;
        public float YawDegrees;
        public int Group;
    }

    // The seeded motion: a fixed share of the entities walks straight legs to random points of
    // its group's leg area at a fixed speed, and the rest stands where it started.
    internal sealed class MovingEntities
    {
        private readonly IReadOnlyList<ReplicationGroup> groups;
        private readonly ReplicationRandom random;
        private readonly float stepPerTick;

        public MovingEntities(
            ulong[] entityIds,
            IReadOnlyList<ReplicationGroup> groups,
            double movingShare,
            float stepPerTick,
            (float X, float Z)[] starts,
            ReplicationRandom random)
        {
            this.groups = groups;
            this.random = random;
            this.stepPerTick = stepPerTick;
            var groupOfEntity = new int[entityIds.Length];
            var cursor = 0;
            for (var group = 0; group < groups.Count; group++)
            {
                for (var index = 0; index < groups[group].Count; index++)
                {
                    groupOfEntity[cursor++] = group;
                }
            }

            // A seeded shuffle picks exactly the moving share.
            var order = Enumerable.Range(0, entityIds.Length).ToArray();
            for (var index = order.Length - 1; index > 0; index--)
            {
                var swap = (int)(random.NextUInt64() % (ulong)(index + 1));
                (order[index], order[swap]) = (order[swap], order[index]);
            }

            var moving = order
                .Take((int)Math.Round(entityIds.Length * movingShare, MidpointRounding.AwayFromZero))
                .Order()
                .ToArray();
            Movers = new Mover[moving.Length];
            for (var index = 0; index < moving.Length; index++)
            {
                var entity = moving[index];
                Movers[index] = new Mover
                {
                    EntityId = entityIds[entity],
                    X = starts[entity].X,
                    Z = starts[entity].Z,
                    Group = groupOfEntity[entity]
                };
                PickTarget(ref Movers[index]);
            }

            MovingIds = [.. Movers.Select(mover => mover.EntityId)];
            AllIds = entityIds;
        }

        public Mover[] Movers { get; }

        public IReadOnlyList<ulong> MovingIds { get; }

        public IReadOnlyList<ulong> AllIds { get; }

        public void Step()
        {
            for (var index = 0; index < Movers.Length; index++)
            {
                ref var mover = ref Movers[index];
                var deltaX = mover.TargetX - mover.X;
                var deltaZ = mover.TargetZ - mover.Z;
                var distance = MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
                if (distance <= stepPerTick)
                {
                    mover.X = mover.TargetX;
                    mover.Z = mover.TargetZ;
                    PickTarget(ref mover);
                    continue;
                }

                var scale = stepPerTick / distance;
                mover.VelocityX = deltaX * scale;
                mover.VelocityZ = deltaZ * scale;
                mover.X += mover.VelocityX;
                mover.Z += mover.VelocityZ;
            }
        }

        private void PickTarget(ref Mover mover)
        {
            var target = groups[mover.Group].Legs.Sample(random);
            mover.TargetX = target.X;
            mover.TargetZ = target.Z;
            // The yaw only fills the record; no count depends on it.
            mover.YawDegrees = MathF.Atan2(target.X - mover.X, target.Z - mover.Z) * (180f / MathF.PI);
        }
    }

    // The Mobs as the snapshot broadcast takes them: every Mob of the store, a walking one at the
    // pose of the seeded motion, in entity id order.
    private sealed class MovingMobs : IMobSnapshotSource
    {
        private readonly WorldActorRuntimeState[] actors;
        private readonly Dictionary<ulong, int> moverIndexByEntityId = [];
        private readonly MovingEntities mobs;

        public MovingMobs(MovingEntities mobs, WorldActorStore store)
        {
            this.mobs = mobs;
            actors = [.. store.ListActors().OrderBy(actor => actor.NetworkEntityId)];
            for (var index = 0; index < mobs.Movers.Length; index++)
            {
                moverIndexByEntityId.Add(mobs.Movers[index].EntityId, index);
            }
        }

        public void CopyStatesTo(List<SnapshotEntityState> destination)
        {
            destination.Clear();
            foreach (var actor in actors)
            {
                if (moverIndexByEntityId.TryGetValue(actor.NetworkEntityId, out var index))
                {
                    var mover = mobs.Movers[index];
                    destination.Add(new SnapshotEntityState(
                        mover.EntityId,
                        new PlayerMovementState(mover.X, 0f, mover.Z, mover.VelocityX, 0f, mover.VelocityZ, mover.YawDegrees, true)));
                    continue;
                }

                destination.Add(new SnapshotEntityState(
                    actor.NetworkEntityId,
                    new PlayerMovementState(actor.PositionX, actor.PositionY, actor.PositionZ, 0f, 0f, 0f, actor.YawDegrees, true)));
            }
        }
    }

    // Exact tick, interest, and broadcast durations of the measured ticks, from the worker's own
    // histograms, so the benchmark adds no timer of its own to the path it measures.
    private sealed class TimingRecorder : IDisposable
    {
        private readonly MeterListener listener = new();

        public TimingRecorder(int ticks)
        {
            Tick = new Samples(ticks);
            Interest = new Samples(ticks);
            Broadcast = new Samples(ticks);
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RealtimePerformanceMetrics.MeterName
                    && Select(instrument.Name) is not null)
                {
                    meterListener.EnableMeasurementEvents(instrument, Select(instrument.Name));
                }
            };
            listener.SetMeasurementEventCallback<double>(static (_, value, _, state) => ((Samples)state!).Add(value));
            listener.Start();
        }

        public Samples Tick { get; }

        public Samples Interest { get; }

        public Samples Broadcast { get; }

        public void Dispose()
        {
            listener.Dispose();
        }

        private Samples? Select(string instrument)
        {
            return instrument switch
            {
                "simulation_worker.simulation.tick.duration" => Tick,
                "simulation_worker.simulation.interest.duration" => Interest,
                "simulation_worker.simulation.snapshot_broadcast.duration" => Broadcast,
                _ => null
            };
        }
    }

    private sealed class Samples(int capacity)
    {
        private readonly List<double> values = new(capacity);

        public double Average => values.Count == 0 ? 0 : values.Average();

        // The nearest-rank 99th percentile.
        public double P99
        {
            get
            {
                if (values.Count == 0)
                {
                    return 0;
                }

                var sorted = values.Order().ToArray();
                return sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.99) - 1)];
            }
        }

        public void Add(double value)
        {
            values.Add(value);
        }
    }
}

// The benchmark's own Mob, shaped like the grey wolf of the content, so its spawns cost what a
// wolf's do and no content change moves the benchmark's numbers.
internal static class BenchmarkMob
{
    public const string Id = "mob.benchmark_wolf";

    public static WorldActorCatalogRuntimeDocument Catalog { get; } = new()
    {
        FormatVersion = 1,
        CatalogId = "replication-benchmark",
        Revision = "replication-benchmark",
        Factions = [],
        PresentationArchetypes = [],
        ActivityProfiles =
        [
            new WorldActorActivityProfileDefinition
            {
                Id = "mob.standard",
                ActivationRange = 48f,
                DormancyRange = 64f,
                ActiveTickRateHz = 10f,
                DormantTickRateHz = 1f,
                StructuralFingerprint = string.Empty
            }
        ],
        RespawnProfiles = [],
        ResourceProfiles = [],
        CombatProfiles = [],
        BrainProfiles =
        [
            new WorldActorBrainProfileDefinition
            {
                Id = "mob.defensive",
                Aggression = WorldActorAggressionIds.Defensive,
                DetectionRange = 10f,
                LeashRange = 30f,
                GiveUpSeconds = 10f,
                StructuralFingerprint = string.Empty
            }
        ],
        LocomotionProfiles = [],
        LootProfiles = [],
        Actors =
        [
            new WorldActorDefinition
            {
                Id = Id,
                Kind = WorldActorKindIds.Mob,
                FactionId = "wild",
                Disposition = WorldActorDispositionIds.Neutral,
                PresentationArchetypeId = "mob.feral_wolf",
                DamagePolicy = "damageable",
                Tags = ["creature", "wild"],
                InteractionBounds = new WorldActorBoundsDefinition { CenterY = 0.55f, SizeX = 1.2f, SizeY = 1.1f, SizeZ = 1.8f },
                CollisionBounds = new WorldActorBoundsDefinition(),
                Capabilities = [],
                ActivityProfileId = "mob.standard",
                RespawnProfileId = "mob.normal",
                CorpsePersistenceMode = "live",
                CorpseLifetimeSeconds = 120f,
                Level = 1,
                CombatProfileId = "mob.feral_wolf",
                BrainProfileId = "mob.defensive",
                LocomotionProfileId = "mob.quadruped",
                LootProfileId = "mob.feral_wolf",
                KillCredit = "tag",
                StructuralFingerprint = string.Empty
            }
        ]
    };
}
