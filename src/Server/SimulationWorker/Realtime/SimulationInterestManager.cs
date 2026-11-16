using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmorpg.SimulationWorker.Config;

namespace Mmorpg.SimulationWorker.Realtime;

public readonly record struct SimulationInterestEntity(ulong EntityId, float X, float Z);

// What one refresh of a connection changed: the entities that entered and left its view, each
// ascending by id, its visible set after the refresh, and the entities that stayed in view and
// came into detail while the connection had missed a state of theirs, ascending by id. The lists
// belong to the manager and hold until the connection's next refresh.
public readonly record struct SimulationInterestUpdate(
    IReadOnlyList<ulong> Entered,
    IReadOnlyList<ulong> Exited,
    IReadOnlySet<ulong> Visible,
    IReadOnlyList<ulong> DetailEntered);

// One connection that held back states of an entity it sees out of detail, and which.
internal readonly record struct SimulationInterestMissed(int ConnectionId, ulong EntityId, MissedStates States);

// The spatial index of the entities and what each connection sees of them. Every indexed entity
// has a slot that stays its own while it is indexed, with its position in flat arrays, and every
// cell lists the slots in it, so the visibility test reads positions without a lookup. A
// connection keeps its visible entities ascending by id, each with its slot, its tier, near
// within NearRadius or still within NearExitRadius when it was near before, and its viewer pair.
// A refresh marks the last visible set by slot, scans the cells around the observer once,
// deciding visibility, tier, and detail where it computes the distance, and touches the visible
// set only for what entered or left.
//
// A connection also keeps, parallel to its visible entities, whether it sees each in detail,
// within DetailRadius or still within DetailExitRadius when it was in detail before, read and
// written in order like the tier. A viewer pair is one connection seeing one entity. The pairs
// of an entity form a list, so the replicators walk the viewers of an entity without asking every
// connection. A pair holds a copy of the detail, which a refresh writes only when it changes, and
// the states the replicators held back from the connection while it saw the entity out of
// detail. A connection sees its own entity, its target, and whoever targets its entity in detail
// whatever the distance. Loop-confined.
public sealed class SimulationInterestManager
{
    private const int InitialSlotCapacity = 256;
    private const int InitialPairCapacity = 1024;

    private static readonly IReadOnlySet<ulong> EmptyVisible = new HashSet<ulong>();
    private static readonly IReadOnlyList<ulong> EmptyOrderedVisible = Array.Empty<ulong>();
    private static readonly IReadOnlyList<bool> EmptyOrderedNear = Array.Empty<bool>();

    private readonly float cellSize;
    private readonly float enterRadiusSquared;
    private readonly float exitRadiusSquared;
    private readonly float nearRadiusSquared;
    private readonly float nearExitRadiusSquared;
    private readonly float detailRadiusSquared;
    private readonly float detailExitRadiusSquared;

    // A cell farther than the exit radius and a margin of a meter, which covers the rounding of a
    // position into its cell, is skipped by a refresh.
    private readonly float cellSkipDistanceSquared;
    private readonly int cellRadius;
    private readonly Dictionary<ulong, int> slotByEntityId = [];
    private readonly Stack<int> freeSlots = [];
    private readonly Dictionary<long, List<int>> slotsByCell = [];
    private readonly Stack<List<int>> cellListPool = [];
    private readonly Dictionary<int, ConnectionInterestState> interestsByConnection = [];
    private readonly Dictionary<ulong, int> connectionIdsByControlledEntityId = [];

    // The viewer pairs of each visible entity, and the entity that each combatant targets.
    private readonly Dictionary<ulong, EntityViewers> viewersByEntity = [];
    private readonly Dictionary<ulong, ulong> targetsByEntity = [];

    // Scratch of one refresh: the visible entities it found, and the merged ordered lists.
    private readonly List<int> candidateSlots = [];
    private readonly List<ulong> mergedIds = [];
    private readonly List<bool> mergedNear = [];
    private readonly List<bool> mergedDetail = [];
    private readonly List<int> mergedSlots = [];
    private readonly List<int> mergedPairs = [];

    // Scratch of a slot's take: the entities on the slot that may have missed states.
    private readonly List<ulong> slotEntities = [];

    // Per slot: the entity, 0 for a free slot, its position and cell, and its index in the cell.
    private ulong[] slotEntityIds = new ulong[InitialSlotCapacity];
    private float[] slotX = new float[InitialSlotCapacity];
    private float[] slotZ = new float[InitialSlotCapacity];
    private long[] slotCells = new long[InitialSlotCapacity];
    private int[] slotIndexInCell = new int[InitialSlotCapacity];

    // Per slot, the stamp of the last synchronization that listed it, and for the refresh that
    // runs: whether the entity was visible, near, and in detail before it, and whether it is now.
    // A slot is marked when its stamp equals the current one, so nothing is cleared.
    private uint[] synchronizedStamps = new uint[InitialSlotCapacity];
    private uint[] wasVisibleStamps = new uint[InitialSlotCapacity];
    private bool[] wasNear = new bool[InitialSlotCapacity];
    private bool[] wasDetail = new bool[InitialSlotCapacity];
    private uint[] visibleStamps = new uint[InitialSlotCapacity];
    private bool[] near = new bool[InitialSlotCapacity];
    private bool[] detail = new bool[InitialSlotCapacity];
    private int usedSlots;
    private uint synchronizeStamp;
    private uint refreshStamp;

    // The viewer pairs; a free pair has no entity and links to the next free one.
    private ViewerPair[] pairs = new ViewerPair[InitialPairCapacity];
    private int usedPairs;
    private int firstFreePair = -1;

    // Every change of a connection's visible list takes the next version, unique across connections.
    private ulong visibleVersion;

    public SimulationInterestManager(InterestManagementConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!IsFinite(config.CellSize)
            || !IsFinite(config.EnterRadius)
            || !IsFinite(config.ExitRadius)
            || config.CellSize <= 0f
            || config.EnterRadius <= 0f
            || config.ExitRadius < config.EnterRadius
            || config.ExitRadius / config.CellSize > 64f
            || !IsFinite(config.NearRadius)
            || !IsFinite(config.NearExitRadius)
            || config.NearRadius <= 0f
            || config.NearExitRadius < config.NearRadius
            || !IsFinite(config.DetailRadius)
            || !IsFinite(config.DetailExitRadius)
            || config.DetailRadius <= 0f
            || config.DetailExitRadius < config.DetailRadius)
        {
            throw new ArgumentException("Interest management configuration is invalid.", nameof(config));
        }

        cellSize = config.CellSize;
        RefreshIntervalSnapshots = Math.Max(1, config.RefreshIntervalSnapshots);
        enterRadiusSquared = config.EnterRadius * config.EnterRadius;
        exitRadiusSquared = config.ExitRadius * config.ExitRadius;
        nearRadiusSquared = config.NearRadius * config.NearRadius;
        nearExitRadiusSquared = config.NearExitRadius * config.NearExitRadius;
        detailRadiusSquared = config.DetailRadius * config.DetailRadius;
        detailExitRadiusSquared = config.DetailExitRadius * config.DetailExitRadius;
        cellSkipDistanceSquared = (config.ExitRadius + 1f) * (config.ExitRadius + 1f);
        cellRadius = (int)Math.Ceiling(config.ExitRadius / config.CellSize);
    }

    // Each connection's visible set is refreshed every this many snapshots.
    public int RefreshIntervalSnapshots { get; }

    public int EntityCount => slotByEntityId.Count;

    // The viewer pairs in use: every connection and entity it sees.
    internal int ViewerPairCount { get; private set; }

    // Makes the index hold exactly these entities at these positions. An entity that stays in
    // its cell only gets its new position; one that changed cells moves, a new one is added, and
    // one that is no longer listed leaves the index. What the connections see changes only at
    // their refreshes, as before.
    public void Synchronize(ReadOnlySpan<SimulationInterestEntity> currentEntities)
    {
        var stamp = NextSynchronizeStamp();
        foreach (var entity in currentEntities)
        {
            ValidateEntity(entity, nameof(currentEntities));
            if (slotByEntityId.TryGetValue(entity.EntityId, out var slot))
            {
                if (synchronizedStamps[slot] == stamp)
                {
                    throw new ArgumentException(
                        $"Interest entity {entity.EntityId} is listed more than once.",
                        nameof(currentEntities));
                }

                Move(slot, entity.X, entity.Z);
            }
            else
            {
                slot = AddSlot(entity);
            }

            synchronizedStamps[slot] = stamp;
        }

        for (var slot = 0; slot < usedSlots; slot++)
        {
            if (slotEntityIds[slot] != 0 && synchronizedStamps[slot] != stamp)
            {
                RemoveSlot(slot);
            }
        }
    }

    public IReadOnlyList<int> AddEntity(SimulationInterestEntity entity)
    {
        ValidateEntity(entity, nameof(entity));
        if (slotByEntityId.ContainsKey(entity.EntityId))
        {
            throw new InvalidOperationException(
                $"Interest entity {entity.EntityId} is already registered.");
        }

        var entitySlot = AddSlot(entity);
        var enteredConnections = new List<int>();
        var centerX = ToCell(entity.X);
        var centerZ = ToCell(entity.Z);
        for (var z = centerZ - cellRadius; z <= centerZ + cellRadius; z++)
        {
            for (var x = centerX - cellRadius; x <= centerX + cellRadius; x++)
            {
                if (!slotsByCell.TryGetValue(CellKey(x, z), out var cellSlots))
                {
                    continue;
                }

                foreach (var observerSlot in cellSlots)
                {
                    var observerId = slotEntityIds[observerSlot];
                    if (observerSlot == entitySlot
                        || !connectionIdsByControlledEntityId.TryGetValue(observerId, out var connectionId)
                        || !interestsByConnection.TryGetValue(connectionId, out var state))
                    {
                        continue;
                    }

                    var distanceSquared = DistanceSquared(observerSlot, entitySlot);
                    if (distanceSquared > enterRadiusSquared || !state.Visible.Add(entity.EntityId))
                    {
                        continue;
                    }

                    var index = state.OrderedVisible.BinarySearch(entity.EntityId);
                    if (index < 0)
                    {
                        state.OrderedVisible.Insert(~index, entity.EntityId);
                        var inDetail = distanceSquared <= detailRadiusSquared;
                        state.OrderedNear.Insert(~index, distanceSquared <= nearRadiusSquared);
                        state.OrderedDetail.Insert(~index, inDetail);
                        state.OrderedSlots.Insert(~index, entitySlot);
                        state.OrderedPairs.Insert(~index, AddPair(state, entity.EntityId, inDetail));
                        state.Version = ++visibleVersion;
                    }

                    enteredConnections.Add(connectionId);
                }
            }
        }

        enteredConnections.Sort();
        return enteredConnections;
    }

    public void UpdateEntity(SimulationInterestEntity entity)
    {
        ValidateEntity(entity, nameof(entity));
        if (!slotByEntityId.TryGetValue(entity.EntityId, out var slot))
        {
            throw new InvalidOperationException(
                $"Interest entity {entity.EntityId} is not registered.");
        }

        Move(slot, entity.X, entity.Z);
    }

    public SimulationInterestUpdate Refresh(int connectionId, ulong controlledEntityId)
    {
        if (!slotByEntityId.TryGetValue(controlledEntityId, out var observerSlot))
        {
            RemoveConnection(connectionId);
            return new SimulationInterestUpdate(EmptyOrderedVisible, EmptyOrderedVisible, EmptyVisible, EmptyOrderedVisible);
        }

        if (!interestsByConnection.TryGetValue(connectionId, out var state))
        {
            state = new ConnectionInterestState(connectionId) { Version = ++visibleVersion };
            interestsByConnection.Add(connectionId, state);
        }

        BindControlledEntity(connectionId, state, controlledEntityId);
        var stamp = NextRefreshStamp();
        var orderedIds = CollectionsMarshal.AsSpan(state.OrderedVisible);
        var orderedNear = CollectionsMarshal.AsSpan(state.OrderedNear);
        var orderedDetail = CollectionsMarshal.AsSpan(state.OrderedDetail);
        var orderedSlots = CollectionsMarshal.AsSpan(state.OrderedSlots);
        var orderedPairs = CollectionsMarshal.AsSpan(state.OrderedPairs);
        for (var index = 0; index < orderedIds.Length; index++)
        {
            var slot = ResolveSlot(orderedIds[index], orderedSlots[index]);
            orderedSlots[index] = slot;
            if (slot >= 0)
            {
                wasVisibleStamps[slot] = stamp;
                wasNear[slot] = orderedNear[index];
                wasDetail[slot] = orderedDetail[index];
            }
        }

        // Within the enter radius an entity is visible either way, so only the band up to the
        // exit radius, and the bands up to the near and detail exit radii, need what was seen
        // before.
        candidateSlots.Clear();
        var observerX = slotX[observerSlot];
        var observerZ = slotZ[observerSlot];
        var centerX = ToCell(observerX);
        var centerZ = ToCell(observerZ);
        for (var z = centerZ - cellRadius; z <= centerZ + cellRadius; z++)
        {
            var cellDeltaZ = DistanceToCell(observerZ, z);
            for (var x = centerX - cellRadius; x <= centerX + cellRadius; x++)
            {
                // A cell whose nearest point lies beyond the exit radius holds nothing visible.
                var cellDeltaX = DistanceToCell(observerX, x);
                if ((cellDeltaX * cellDeltaX) + (cellDeltaZ * cellDeltaZ) > cellSkipDistanceSquared
                    || !slotsByCell.TryGetValue(CellKey(x, z), out var cellSlots))
                {
                    continue;
                }

                foreach (var slot in cellSlots)
                {
                    var deltaX = slotX[slot] - observerX;
                    var deltaZ = slotZ[slot] - observerZ;
                    var distanceSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
                    var wasVisible = wasVisibleStamps[slot] == stamp;
                    if (distanceSquared > enterRadiusSquared
                        && (distanceSquared > exitRadiusSquared || !wasVisible))
                    {
                        continue;
                    }

                    candidateSlots.Add(slot);
                    visibleStamps[slot] = stamp;
                    near[slot] = distanceSquared <= nearRadiusSquared
                        || (distanceSquared <= nearExitRadiusSquared && wasVisible && wasNear[slot]);
                    detail[slot] = distanceSquared <= detailRadiusSquared
                        || (distanceSquared <= detailExitRadiusSquared && wasVisible && wasDetail[slot]);
                }
            }
        }

        state.Exited.Clear();
        for (var index = 0; index < orderedIds.Length; index++)
        {
            var slot = orderedSlots[index];
            if (slot < 0 || visibleStamps[slot] != stamp)
            {
                state.Exited.Add(orderedIds[index]);
            }
        }

        state.Entered.Clear();
        foreach (var slot in candidateSlots)
        {
            if (wasVisibleStamps[slot] != stamp)
            {
                state.Entered.Add(slotEntityIds[slot]);
            }
        }

        state.DetailEntered.Clear();
        if (state.Entered.Count == 0 && state.Exited.Count == 0)
        {
            // The same entities: only the tiers and the detail may change.
            for (var index = 0; index < orderedIds.Length; index++)
            {
                var slot = orderedSlots[index];
                orderedNear[index] = near[slot];
                if (orderedDetail[index] != detail[slot])
                {
                    orderedDetail[index] = detail[slot];
                    ChangeDetail(state, orderedPairs[index], orderedIds[index], detail[slot]);
                }
            }
        }
        else
        {
            state.Entered.Sort();
            ApplyMembershipChange(state, stamp);
        }

        return new SimulationInterestUpdate(state.Entered, state.Exited, state.Visible, state.DetailEntered);
    }

    public IReadOnlySet<ulong> GetVisible(int connectionId)
    {
        return interestsByConnection.TryGetValue(connectionId, out var state)
            ? state.Visible
            : EmptyVisible;
    }

    public IReadOnlyList<ulong> GetVisibleOrdered(int connectionId)
    {
        return interestsByConnection.TryGetValue(connectionId, out var state)
            ? state.OrderedVisible
            : EmptyOrderedVisible;
    }

    // Whether each entity of GetVisibleOrdered, at the same index, is in the near tier.
    public IReadOnlyList<bool> GetNearOrdered(int connectionId)
    {
        return interestsByConnection.TryGetValue(connectionId, out var state)
            ? state.OrderedNear
            : EmptyOrderedNear;
    }

    // The visible entities ascending by id and their tiers, as spans for the snapshot path, and
    // the version of the list, which changes whenever its entities do and never repeats. They
    // hold until the connection's visible set changes.
    internal bool TryGetVisibleSpans(
        int connectionId,
        out ReadOnlySpan<ulong> orderedVisible,
        out ReadOnlySpan<bool> orderedNear,
        out ulong version)
    {
        if (!interestsByConnection.TryGetValue(connectionId, out var state))
        {
            orderedVisible = default;
            orderedNear = default;
            version = 0;
            return false;
        }

        orderedVisible = CollectionsMarshal.AsSpan(state.OrderedVisible);
        orderedNear = CollectionsMarshal.AsSpan(state.OrderedNear);
        version = state.Version;
        return true;
    }

    public bool IsNear(int connectionId, ulong entityId)
    {
        if (!interestsByConnection.TryGetValue(connectionId, out var state))
        {
            return false;
        }

        var index = state.OrderedVisible.BinarySearch(entityId);
        return index >= 0 && state.OrderedNear[index];
    }

    // Whether the connection sees the entity in detail: by distance, or because it is the
    // connection's own entity, its target, or targets it.
    public bool IsInDetail(int connectionId, ulong entityId)
    {
        return TryGetPair(connectionId, entityId, out var pair)
            && PairInDetail(pair, entityId, TargetOf(entityId));
    }

    public bool IsVisibleToAnyConnection(ulong entityId)
    {
        return viewersByEntity.ContainsKey(entityId);
    }

    // The entity that an entity targets, or 0, as the replicators last told it. A connection sees
    // its target and whoever targets its entity in detail.
    internal void SetTarget(ulong entityId, ulong targetEntityId)
    {
        if (targetEntityId == 0)
        {
            targetsByEntity.Remove(entityId);
        }
        else
        {
            targetsByEntity[entityId] = targetEntityId;
        }

        if (connectionIdsByControlledEntityId.TryGetValue(entityId, out var connectionId)
            && interestsByConnection.TryGetValue(connectionId, out var state)
            && state.ControlledEntityId == entityId)
        {
            state.TargetEntityId = targetEntityId;
        }
    }

    // Fills recipients with the connections that see the entity and get a change of the states
    // at once: every one when the change is urgent, otherwise those that see the entity in
    // detail. The others missed the states, unless states is None, as for a combat event. A
    // recipient gets the newest of the states, so it missed none of them any more. The excluded
    // connection is left alone.
    internal void CollectRecipients(
        ulong entityId,
        MissedStates states,
        bool urgent,
        int excludedConnectionId,
        List<int> recipients)
    {
        recipients.Clear();
        ref var viewers = ref CollectionsMarshal.GetValueRefOrNullRef(viewersByEntity, entityId);
        if (Unsafe.IsNullRef(ref viewers))
        {
            return;
        }

        var target = TargetOf(entityId);
        var kept = (byte)~(byte)states;
        for (var pair = viewers.First; pair >= 0; pair = pairs[pair].Next)
        {
            ref var viewer = ref pairs[pair];
            var connectionId = viewer.Viewer!.ConnectionId;
            if (connectionId == excludedConnectionId)
            {
                continue;
            }

            if (urgent || PairInDetail(pair, entityId, target))
            {
                viewer.Missed &= kept;
                recipients.Add(connectionId);
            }
            else if (states != MissedStates.None)
            {
                viewer.Missed |= (byte)states;
                viewers.AnyMissed = true;
            }
        }
    }

    // Takes the states that the connection missed of the entity; it has missed none afterwards.
    internal MissedStates TakeMissed(int connectionId, ulong entityId)
    {
        if (!TryGetPair(connectionId, entityId, out var pair))
        {
            return MissedStates.None;
        }

        var missed = (MissedStates)pairs[pair].Missed;
        pairs[pair].Missed = 0;
        return missed;
    }

    // The connection missed these states of the entity again, such as states held back further.
    internal void MarkMissed(int connectionId, ulong entityId, MissedStates states)
    {
        if (states == MissedStates.None || !TryGetPair(connectionId, entityId, out var pair))
        {
            return;
        }

        pairs[pair].Missed |= (byte)states;
        CollectionsMarshal.GetValueRefOrNullRef(viewersByEntity, entityId).AnyMissed = true;
    }

    // Takes what the connections missed of the entities whose slot this is, the entities whose
    // id modulo slotCount is slotPhase, grouped by entity: each such connection missed nothing
    // afterwards.
    internal void TakeMissedOnSlot(uint slotPhase, uint slotCount, List<SimulationInterestMissed> destination)
    {
        destination.Clear();
        slotEntities.Clear();
        foreach (var (entityId, entityViewers) in viewersByEntity)
        {
            if (entityViewers.AnyMissed && entityId % slotCount == slotPhase)
            {
                slotEntities.Add(entityId);
            }
        }

        foreach (var entityId in slotEntities)
        {
            ref var viewers = ref CollectionsMarshal.GetValueRefOrNullRef(viewersByEntity, entityId);
            viewers.AnyMissed = false;
            for (var pair = viewers.First; pair >= 0; pair = pairs[pair].Next)
            {
                ref var viewer = ref pairs[pair];
                if (viewer.Missed != 0)
                {
                    destination.Add(new SimulationInterestMissed(
                        viewer.Viewer!.ConnectionId,
                        entityId,
                        (MissedStates)viewer.Missed));
                    viewer.Missed = 0;
                }
            }
        }
    }

    public bool RemoveConnection(int connectionId)
    {
        if (!interestsByConnection.Remove(connectionId, out var state))
        {
            return false;
        }

        foreach (var pair in state.OrderedPairs)
        {
            RemovePair(pair);
        }

        if (connectionIdsByControlledEntityId.TryGetValue(
                state.ControlledEntityId,
                out var currentConnectionId)
            && currentConnectionId == connectionId)
        {
            connectionIdsByControlledEntityId.Remove(state.ControlledEntityId);
        }

        return true;
    }

    public IReadOnlyList<int> ForgetEntity(ulong entityId)
    {
        if (slotByEntityId.TryGetValue(entityId, out var slot))
        {
            RemoveSlot(slot);
        }

        targetsByEntity.Remove(entityId);
        if (connectionIdsByControlledEntityId.Remove(entityId, out var connectionId)
            && interestsByConnection.TryGetValue(connectionId, out var controlledState)
            && controlledState.ControlledEntityId == entityId)
        {
            controlledState.ControlledEntityId = 0;
            controlledState.TargetEntityId = 0;
        }

        // The entity's viewer pairs name every connection that sees it.
        var affectedConnections = new List<int>();
        if (!viewersByEntity.TryGetValue(entityId, out var viewers))
        {
            return affectedConnections;
        }

        for (var pair = viewers.First; pair >= 0;)
        {
            var next = pairs[pair].Next;
            var state = pairs[pair].Viewer!;
            state.Visible.Remove(entityId);
            var orderedIndex = state.OrderedVisible.BinarySearch(entityId);
            if (orderedIndex >= 0)
            {
                state.OrderedVisible.RemoveAt(orderedIndex);
                state.OrderedNear.RemoveAt(orderedIndex);
                state.OrderedDetail.RemoveAt(orderedIndex);
                state.OrderedSlots.RemoveAt(orderedIndex);
                state.OrderedPairs.RemoveAt(orderedIndex);
                state.Version = ++visibleVersion;
            }

            affectedConnections.Add(state.ConnectionId);
            RemovePair(pair);
            pair = next;
        }

        affectedConnections.Sort();
        return affectedConnections;
    }

    public void Clear()
    {
        slotByEntityId.Clear();
        freeSlots.Clear();
        foreach (var cellSlots in slotsByCell.Values)
        {
            cellSlots.Clear();
            cellListPool.Push(cellSlots);
        }

        slotsByCell.Clear();
        Array.Clear(slotEntityIds, 0, usedSlots);
        usedSlots = 0;
        interestsByConnection.Clear();
        connectionIdsByControlledEntityId.Clear();
        viewersByEntity.Clear();
        targetsByEntity.Clear();
        Array.Clear(pairs, 0, usedPairs);
        usedPairs = 0;
        firstFreePair = -1;
        ViewerPairCount = 0;
    }

    // The new visible lists: the entities that stayed, in their order, merged with the ones that
    // entered, each with its tier and its viewer pair. The visible set changes only by what
    // entered and left, and a pair only for what entered and left.
    private void ApplyMembershipChange(ConnectionInterestState state, uint stamp)
    {
        foreach (var entityId in state.Exited)
        {
            state.Visible.Remove(entityId);
        }

        foreach (var entityId in state.Entered)
        {
            state.Visible.Add(entityId);
        }

        mergedIds.Clear();
        mergedNear.Clear();
        mergedDetail.Clear();
        mergedSlots.Clear();
        mergedPairs.Clear();
        var stayed = 0;
        var entered = 0;
        var orderedIds = state.OrderedVisible;
        var orderedDetail = state.OrderedDetail;
        var orderedSlots = state.OrderedSlots;
        var orderedPairs = state.OrderedPairs;
        while (stayed < orderedIds.Count || entered < state.Entered.Count)
        {
            if (stayed < orderedIds.Count)
            {
                var slot = orderedSlots[stayed];
                if (slot < 0 || visibleStamps[slot] != stamp)
                {
                    RemovePair(orderedPairs[stayed]);
                    stayed++;
                    continue;
                }

                if (entered >= state.Entered.Count || orderedIds[stayed] < state.Entered[entered])
                {
                    var pair = orderedPairs[stayed];
                    if (orderedDetail[stayed] != detail[slot])
                    {
                        ChangeDetail(state, pair, orderedIds[stayed], detail[slot]);
                    }

                    AddMerged(orderedIds[stayed], slot, pair);
                    stayed++;
                    continue;
                }
            }

            var enteredId = state.Entered[entered++];
            var enteredSlot = slotByEntityId[enteredId];
            AddMerged(enteredId, enteredSlot, AddPair(state, enteredId, detail[enteredSlot]));
        }

        state.OrderedVisible.Clear();
        state.OrderedVisible.AddRange(mergedIds);
        state.OrderedNear.Clear();
        state.OrderedNear.AddRange(mergedNear);
        state.OrderedDetail.Clear();
        state.OrderedDetail.AddRange(mergedDetail);
        state.OrderedSlots.Clear();
        state.OrderedSlots.AddRange(mergedSlots);
        state.OrderedPairs.Clear();
        state.OrderedPairs.AddRange(mergedPairs);
        state.Version = ++visibleVersion;
    }

    private void AddMerged(ulong entityId, int slot, int pair)
    {
        mergedIds.Add(entityId);
        mergedNear.Add(near[slot]);
        mergedDetail.Add(detail[slot]);
        mergedSlots.Add(slot);
        mergedPairs.Add(pair);
    }

    // An entity that stays in view and came into detail or left it: its pair takes the detail,
    // and one that came into detail while the connection had missed one of its states is listed,
    // so the connection gets them.
    private void ChangeDetail(ConnectionInterestState state, int pair, ulong entityId, bool inDetail)
    {
        ref var viewer = ref pairs[pair];
        if (inDetail && viewer.Missed != 0)
        {
            state.DetailEntered.Add(entityId);
        }

        viewer.Detail = inDetail;
    }

    // Whether the pair's connection sees the entity in detail, given the entity's target.
    private bool PairInDetail(int pair, ulong entityId, ulong entityTarget)
    {
        ref var viewer = ref pairs[pair];
        if (viewer.Detail)
        {
            return true;
        }

        var state = viewer.Viewer!;
        return state.ControlledEntityId == entityId
            || state.TargetEntityId == entityId
            || (entityTarget != 0 && entityTarget == state.ControlledEntityId);
    }

    private ulong TargetOf(ulong entityId)
    {
        return targetsByEntity.TryGetValue(entityId, out var target) ? target : 0;
    }

    private bool TryGetPair(int connectionId, ulong entityId, out int pair)
    {
        pair = -1;
        if (!interestsByConnection.TryGetValue(connectionId, out var state))
        {
            return false;
        }

        var index = state.OrderedVisible.BinarySearch(entityId);
        if (index < 0)
        {
            return false;
        }

        pair = state.OrderedPairs[index];
        return true;
    }

    // A new pair at the head of the entity's viewers.
    private int AddPair(ConnectionInterestState state, ulong entityId, bool inDetail)
    {
        int pair;
        if (firstFreePair >= 0)
        {
            pair = firstFreePair;
            firstFreePair = pairs[pair].Next;
        }
        else
        {
            pair = usedPairs++;
            if (usedPairs > pairs.Length)
            {
                Array.Resize(ref pairs, pairs.Length * 2);
            }
        }

        ref var viewers = ref CollectionsMarshal.GetValueRefOrAddDefault(viewersByEntity, entityId, out var exists);
        var first = exists ? viewers.First : -1;
        if (!exists)
        {
            viewers.AnyMissed = false;
        }

        viewers.First = pair;
        pairs[pair] = new ViewerPair
        {
            EntityId = entityId,
            Viewer = state,
            Previous = -1,
            Next = first,
            Detail = inDetail
        };
        if (first >= 0)
        {
            pairs[first].Previous = pair;
        }

        ViewerPairCount++;
        return pair;
    }

    // Unlinks the pair from its entity's viewers, which leave with their last pair, and frees it.
    private void RemovePair(int pair)
    {
        var entityId = pairs[pair].EntityId;
        var previous = pairs[pair].Previous;
        var next = pairs[pair].Next;
        if (next >= 0)
        {
            pairs[next].Previous = previous;
        }

        if (previous >= 0)
        {
            pairs[previous].Next = next;
        }
        else if (next >= 0)
        {
            CollectionsMarshal.GetValueRefOrNullRef(viewersByEntity, entityId).First = next;
        }
        else
        {
            viewersByEntity.Remove(entityId);
        }

        pairs[pair] = new ViewerPair { Previous = -1, Next = firstFreePair };
        firstFreePair = pair;
        ViewerPairCount--;
    }

    // The slot an ordered entry names, checked against its entity, or found again when the
    // entity left the index and came back; -1 when it is no longer indexed.
    private int ResolveSlot(ulong entityId, int slot)
    {
        if ((uint)slot < (uint)usedSlots && slotEntityIds[slot] == entityId)
        {
            return slot;
        }

        return slotByEntityId.TryGetValue(entityId, out var current) ? current : -1;
    }

    private void BindControlledEntity(
        int connectionId,
        ConnectionInterestState state,
        ulong controlledEntityId)
    {
        if (state.ControlledEntityId == controlledEntityId)
        {
            return;
        }

        if (state.ControlledEntityId != 0
            && connectionIdsByControlledEntityId.TryGetValue(
                state.ControlledEntityId,
                out var previousConnectionId)
            && previousConnectionId == connectionId)
        {
            connectionIdsByControlledEntityId.Remove(state.ControlledEntityId);
        }

        if (connectionIdsByControlledEntityId.TryGetValue(
                controlledEntityId,
                out var replacedConnectionId)
            && replacedConnectionId != connectionId
            && interestsByConnection.TryGetValue(replacedConnectionId, out var replacedState))
        {
            replacedState.ControlledEntityId = 0;
            replacedState.TargetEntityId = 0;
        }

        state.ControlledEntityId = controlledEntityId;
        state.TargetEntityId = TargetOf(controlledEntityId);
        connectionIdsByControlledEntityId[controlledEntityId] = connectionId;
    }

    private int AddSlot(SimulationInterestEntity entity)
    {
        if (!freeSlots.TryPop(out var slot))
        {
            slot = usedSlots++;
            EnsureCapacity(usedSlots);
        }

        slotByEntityId.Add(entity.EntityId, slot);
        slotEntityIds[slot] = entity.EntityId;
        slotX[slot] = entity.X;
        slotZ[slot] = entity.Z;
        AddToCell(slot, CellKey(ToCell(entity.X), ToCell(entity.Z)));
        return slot;
    }

    private void RemoveSlot(int slot)
    {
        slotByEntityId.Remove(slotEntityIds[slot]);
        RemoveFromCell(slot);
        slotEntityIds[slot] = 0;
        freeSlots.Push(slot);
    }

    private void Move(int slot, float x, float z)
    {
        var cell = CellKey(ToCell(x), ToCell(z));
        if (cell != slotCells[slot])
        {
            RemoveFromCell(slot);
            AddToCell(slot, cell);
        }

        slotX[slot] = x;
        slotZ[slot] = z;
    }

    private void AddToCell(int slot, long cellKey)
    {
        if (!slotsByCell.TryGetValue(cellKey, out var cellSlots))
        {
            cellSlots = cellListPool.TryPop(out var pooled) ? pooled : [];
            slotsByCell.Add(cellKey, cellSlots);
        }

        slotCells[slot] = cellKey;
        slotIndexInCell[slot] = cellSlots.Count;
        cellSlots.Add(slot);
    }

    private void RemoveFromCell(int slot)
    {
        var cellKey = slotCells[slot];
        if (!slotsByCell.TryGetValue(cellKey, out var cellSlots))
        {
            return;
        }

        // The order inside a cell does not matter, so the last slot fills the gap.
        var index = slotIndexInCell[slot];
        var last = cellSlots[^1];
        cellSlots[index] = last;
        slotIndexInCell[last] = index;
        cellSlots.RemoveAt(cellSlots.Count - 1);
        if (cellSlots.Count == 0)
        {
            slotsByCell.Remove(cellKey);
            cellListPool.Push(cellSlots);
        }
    }

    private void EnsureCapacity(int slots)
    {
        if (slots <= slotEntityIds.Length)
        {
            return;
        }

        var capacity = Math.Max(slots, slotEntityIds.Length * 2);
        Array.Resize(ref slotEntityIds, capacity);
        Array.Resize(ref slotX, capacity);
        Array.Resize(ref slotZ, capacity);
        Array.Resize(ref slotCells, capacity);
        Array.Resize(ref slotIndexInCell, capacity);
        Array.Resize(ref synchronizedStamps, capacity);
        Array.Resize(ref wasVisibleStamps, capacity);
        Array.Resize(ref wasNear, capacity);
        Array.Resize(ref wasDetail, capacity);
        Array.Resize(ref visibleStamps, capacity);
        Array.Resize(ref near, capacity);
        Array.Resize(ref detail, capacity);
    }

    // A stamp that no slot holds yet; when the counter wraps, the old stamps are cleared.
    private uint NextSynchronizeStamp()
    {
        if (++synchronizeStamp == 0)
        {
            Array.Clear(synchronizedStamps);
            synchronizeStamp = 1;
        }

        return synchronizeStamp;
    }

    private uint NextRefreshStamp()
    {
        if (++refreshStamp == 0)
        {
            Array.Clear(wasVisibleStamps);
            Array.Clear(visibleStamps);
            refreshStamp = 1;
        }

        return refreshStamp;
    }

    private float DistanceSquared(int slot, int otherSlot)
    {
        var deltaX = slotX[otherSlot] - slotX[slot];
        var deltaZ = slotZ[otherSlot] - slotZ[slot];
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }

    private static void ValidateEntity(
        SimulationInterestEntity entity,
        string parameterName)
    {
        if (entity.EntityId == 0 || !IsFinite(entity.X) || !IsFinite(entity.Z))
        {
            throw new ArgumentException(
                "Interest entities must have a nonzero id and finite positions.",
                parameterName);
        }
    }

    private int ToCell(float position)
    {
        return (int)Math.Floor(position / cellSize);
    }

    // How far a position lies outside a cell along one axis, 0 inside it.
    private float DistanceToCell(float position, int cell)
    {
        var start = cell * cellSize;
        return Math.Max(0f, Math.Max(start - position, position - (start + cellSize)));
    }

    private static long CellKey(int x, int z)
    {
        return ((long)x << 32) | (uint)z;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    // One connection seeing one entity, linked with the other viewers of the entity.
    private struct ViewerPair
    {
        // The entity, 0 for a free pair.
        public ulong EntityId;

        public ConnectionInterestState? Viewer;

        // The neighbouring pairs of the entity, or -1; a free pair's Next is the next free one.
        public int Previous;

        public int Next;

        public bool Detail;

        // The MissedStates the replicators held back from the connection.
        public byte Missed;
    }

    // The first viewer pair of an entity, and whether a pair of it may have missed a state.
    private struct EntityViewers
    {
        public int First;

        public bool AnyMissed;
    }

    private sealed class ConnectionInterestState(int connectionId)
    {
        public int ConnectionId { get; } = connectionId;

        public ulong ControlledEntityId { get; set; }

        // The entity the controlled entity targets, or 0.
        public ulong TargetEntityId { get; set; }

        public HashSet<ulong> Visible { get; } = [];

        // The visible entities ascending by id, and parallel to them their tier, their detail,
        // their slot, which a refresh checks against the entity before it trusts it, and their
        // viewer pair.
        public List<ulong> OrderedVisible { get; } = [];

        public List<bool> OrderedNear { get; } = [];

        public List<bool> OrderedDetail { get; } = [];

        public List<int> OrderedSlots { get; } = [];

        public List<int> OrderedPairs { get; } = [];

        // The version of OrderedVisible.
        public ulong Version { get; set; }

        public List<ulong> Entered { get; } = [];

        public List<ulong> Exited { get; } = [];

        public List<ulong> DetailEntered { get; } = [];
    }
}
