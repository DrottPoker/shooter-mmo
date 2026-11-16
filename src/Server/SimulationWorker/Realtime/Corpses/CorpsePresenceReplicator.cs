using System.Diagnostics;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;

namespace Mmorpg.SimulationWorker.Realtime;

// Tells every joined peer which corpses are within its discovery radius and closes views
// of corpses that are no longer active. Corpse stores change on other threads, so the
// loop polls them here. A broadcast puts the active corpses into cells as wide as the
// discovery radius, so each peer tests only the corpses around it, builds its lists in
// reusable buffers, and sends a peer only what differs from what it was sent last, so one
// that changes nothing allocates nothing. With performance metrics it times its parts:
// collecting the active corpses, selecting each peer's corpses, and building and sending the
// presences that changed. Loop-confined.
internal sealed class CorpsePresenceReplicator(
    SimulationWorkerConfig config,
    DurableCorpseStore corpseStore,
    LiveMobCorpseStore? liveMobCorpseStore,
    CorpseRuntimeMetrics? corpseRuntimeMetrics,
    CorpseViewDistributor views,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    RealtimePerformanceMetrics? performanceMetrics = null)
{
    public const string PhaseName = "corpse presence";

    private readonly RealtimePerformanceMetrics.DurationTimer? collectionTimer =
        performanceMetrics?.TickPhasePartTimer(PhaseName, "collection");
    private readonly RealtimePerformanceMetrics.DurationTimer? selectionTimer =
        performanceMetrics?.TickPhasePartTimer(PhaseName, "selection");
    private readonly RealtimePerformanceMetrics.DurationTimer? sendsTimer =
        performanceMetrics?.TickPhasePartTimer(PhaseName, "sends");
    private readonly List<ICorpseRuntimePresence> activeCorpses = [];
    private readonly HashSet<Guid> activeCorpseIds = [];
    private readonly List<Guid> viewedCorpseIds = [];
    private readonly List<ICorpseRuntimePresence> corpsesInRange = [];

    // The active corpses by their cell on the ground plane, rebuilt by every broadcast in reused
    // lists, so a peer tests only the corpses around it instead of every active one.
    private readonly Dictionary<long, List<ICorpseRuntimePresence>> corpseCells = [];
    private readonly Stack<List<ICorpseRuntimePresence>> freeCorpseCells = new();
    private readonly Dictionary<Guid, ICorpseRuntimePresence> lootGroups = [];
    private readonly List<ICorpseRuntimePresence> nearbyCorpses = [];
    private uint corpsePresenceSequence;

    // The equipment slots whose items a player corpse shows.
    public IReadOnlyList<string> VisibleEquipmentSlots { get; init; } = [];

    // Sends corpse presence twice a second on its lane.
    public void AddTickPhase(SimulationTickPhases phases, SimulationTickCadence cadence)
    {
        ArgumentNullException.ThrowIfNull(cadence);
        var lane = cadence.AddLane(PhaseName, Math.Max(1, config.MovementSimulation.TickRateHz / 2));
        phases.AddPeriodic(
            SimulationTickOrder.CorpsePresence,
            PhaseName,
            (in SimulationTick tick) =>
            {
                if (!lane.IsDue(tick.ServerTick))
                {
                    return false;
                }

                Broadcast();
                return true;
            });
    }

    public void Broadcast()
    {
        var started = Stopwatch.GetTimestamp();
        activeCorpses.Clear();
        var durableCount = corpseStore.AddActiveTo(activeCorpses);
        var liveCount = liveMobCorpseStore?.AddActiveTo(activeCorpses) ?? 0;
        corpseRuntimeMetrics?.Observe(durableCount, liveCount);
        activeCorpses.Sort(static (left, right) =>
        {
            var created = left.CreatedAt.CompareTo(right.CreatedAt);
            return created != 0 ? created : left.CorpseId.CompareTo(right.CorpseId);
        });
        activeCorpseIds.Clear();
        foreach (var corpse in activeCorpses)
        {
            activeCorpseIds.Add(corpse.CorpseId);
        }

        IndexActiveCorpses();

        views.CopyViewedCorpseIdsTo(viewedCorpseIds);
        foreach (var viewedCorpseId in viewedCorpseIds)
        {
            if (!activeCorpseIds.Contains(viewedCorpseId))
            {
                views.CloseInactiveCorpseForAll(
                    viewedCorpseId,
                    RealtimeCorpseViewClosureCodes.Expired,
                    "The corpse expired and is no longer available.");
            }
        }

        unchecked
        {
            corpsePresenceSequence++;
        }

        var partStarted = Stopwatch.GetTimestamp();
        collectionTimer?.Record(Stopwatch.GetElapsedTime(started, partStarted));
        long selectionTicks = 0;
        long sendTicks = 0;
        foreach (var context in peers.Contexts)
        {
            if (context.Session is null
                || !peers.TryGetBoundPlayer(context, out var entity)
                || !peers.TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            var state = entity.Movement.State;
            var characterId = context.Session.CharacterId;
            AddCorpsesInRange(state.PositionX, state.PositionY, state.PositionZ);
            CorpseLootRights.SelectForViewer(corpsesInRange, characterId, lootGroups, nearbyCorpses);
            var changed = context.CorpsePresence.Remember(nearbyCorpses, characterId);
            var selected = Stopwatch.GetTimestamp();
            selectionTicks += selected - partStarted;
            partStarted = selected;
            if (!changed)
            {
                continue;
            }

            foreach (var chunk in CorpseRealtimePacketBuilder.BuildPresence(
                         corpsePresenceSequence,
                         nearbyCorpses,
                         characterId,
                         VisibleEquipmentSlots))
            {
                sender.SendControl(
                    peer,
                    RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(chunk));
            }

            partStarted = Stopwatch.GetTimestamp();
            sendTicks += partStarted - selected;
        }

        selectionTimer?.Record(Stopwatch.GetElapsedTime(0, selectionTicks));
        sendsTimer?.Record(Stopwatch.GetElapsedTime(0, sendTicks));
    }

    // Puts the active corpses into square cells on the ground plane, as wide as the discovery
    // radius, in reusable lists.
    private void IndexActiveCorpses()
    {
        foreach (var cell in corpseCells.Values)
        {
            cell.Clear();
            freeCorpseCells.Push(cell);
        }

        corpseCells.Clear();
        var cellSize = CorpseCellSize;
        foreach (var corpse in activeCorpses)
        {
            var key = CellKey(CellOf(corpse.PositionX, cellSize), CellOf(corpse.PositionZ, cellSize));
            if (!corpseCells.TryGetValue(key, out var cell))
            {
                cell = freeCorpseCells.Count > 0 ? freeCorpseCells.Pop() : [];
                corpseCells.Add(key, cell);
            }

            cell.Add(corpse);
        }
    }

    // Fills corpsesInRange with the corpses within the discovery radius of the position, testing
    // only those of the cells that the radius reaches. A meter beyond the radius keeps a corpse on
    // a cell border among the cells read; the range test alone decides, so the corpses are those a
    // test of every active corpse finds, in another order, which the viewer's selection sorts.
    private void AddCorpsesInRange(float playerX, float playerY, float playerZ)
    {
        corpsesInRange.Clear();
        var cellSize = CorpseCellSize;
        var reach = config.ItemInteraction.CorpseDiscoveryRadius + 1d;
        var maximumCellX = CellOf(playerX + reach, cellSize);
        var minimumCellZ = CellOf(playerZ - reach, cellSize);
        var maximumCellZ = CellOf(playerZ + reach, cellSize);
        for (var cellX = CellOf(playerX - reach, cellSize); cellX <= maximumCellX; cellX++)
        {
            for (var cellZ = minimumCellZ; cellZ <= maximumCellZ; cellZ++)
            {
                if (!corpseCells.TryGetValue(CellKey(cellX, cellZ), out var cell))
                {
                    continue;
                }

                foreach (var corpse in cell)
                {
                    if (IsWithinCorpseDiscoveryRange(corpse, playerX, playerY, playerZ))
                    {
                        corpsesInRange.Add(corpse);
                    }
                }
            }
        }
    }

    private double CorpseCellSize => Math.Max(1d, config.ItemInteraction.CorpseDiscoveryRadius);

    // A cell as a long, so no position makes a cell loop overflow; two far cells that share a key
    // only cost range tests.
    private static long CellOf(double coordinate, double cellSize)
    {
        return (long)Math.Floor(coordinate / cellSize);
    }

    private static long CellKey(long cellX, long cellZ)
    {
        return (cellX << 32) | (uint)cellZ;
    }

    private bool IsWithinCorpseDiscoveryRange(
        ICorpseRuntimePresence corpse,
        float playerX,
        float playerY,
        float playerZ)
    {
        var deltaX = corpse.PositionX - playerX;
        var deltaY = corpse.PositionY - playerY;
        var deltaZ = corpse.PositionZ - playerZ;
        var radius = config.ItemInteraction.CorpseDiscoveryRadius;
        return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ)
            <= radius * radius;
    }
}

// What one peer was sent last as its corpse presence: per corpse what the presence shows of it.
// A broadcast sends again only when this changed. Loop-confined.
internal sealed class CorpsePresenceSent
{
    private readonly List<Entry> entries = [];
    private bool sent;

    // Takes the presence the peer gets now and returns whether it differs from the last one;
    // the first one always counts as changed, also when it holds no corpse.
    public bool Remember(IReadOnlyList<ICorpseRuntimePresence> corpses, Guid characterId)
    {
        if (sent && Matches(corpses, characterId))
        {
            return false;
        }

        sent = true;
        entries.Clear();
        foreach (var corpse in corpses)
        {
            entries.Add(Entry.Of(corpse, characterId));
        }

        return true;
    }

    private bool Matches(IReadOnlyList<ICorpseRuntimePresence> corpses, Guid characterId)
    {
        if (corpses.Count != entries.Count)
        {
            return false;
        }

        for (var index = 0; index < corpses.Count; index++)
        {
            if (!entries[index].Equals(Entry.Of(corpses[index], characterId)))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct Entry(Guid CorpseId, long Revision, bool IsEmpty, bool HasLook, bool CanLoot)
    {
        public static Entry Of(ICorpseRuntimePresence corpse, Guid characterId) => new(
            corpse.CorpseId,
            corpse.Revision,
            corpse.IsEmpty,
            corpse.Look is not null,
            CorpseLootRights.CanLoot(corpse, characterId));
    }
}
