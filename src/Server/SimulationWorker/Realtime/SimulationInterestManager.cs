using System.Runtime.InteropServices;
using Mmorpg.SimulationWorker.Config;

namespace Mmorpg.SimulationWorker.Realtime;

public readonly record struct SimulationInterestEntity(ulong EntityId, float X, float Z);

// What one refresh of a connection changed: the entities that entered and left its view, each
// ascending by id, and its visible set after the refresh. The lists belong to the manager and
// hold until the connection's next refresh.
public readonly record struct SimulationInterestUpdate(
    IReadOnlyList<ulong> Entered,
    IReadOnlyList<ulong> Exited,
    IReadOnlySet<ulong> Visible);

// The spatial index of the entities and what each connection sees of them. Every indexed entity
// has a slot that stays its own while it is indexed, with its position in flat arrays, and every
// cell lists the slots in it, so the visibility test reads positions without a lookup. A
// connection keeps its visible entities ascending by id, each with its slot and its tier, near
// within NearRadius or still within NearExitRadius when it was near before. A refresh marks the
// last visible set by slot, scans the cells around the observer once, deciding visibility and
// tier where it computes the distance, and touches the visible set only for what entered or
// left. Loop-confined.
public sealed class SimulationInterestManager
{
    private const int InitialSlotCapacity = 256;

    private static readonly IReadOnlySet<ulong> EmptyVisible = new HashSet<ulong>();
    private static readonly IReadOnlyList<ulong> EmptyOrderedVisible = Array.Empty<ulong>();
    private static readonly IReadOnlyList<bool> EmptyOrderedNear = Array.Empty<bool>();

    private readonly float cellSize;
    private readonly float enterRadiusSquared;
    private readonly float exitRadiusSquared;
    private readonly float nearRadiusSquared;
    private readonly float nearExitRadiusSquared;

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

    // Scratch of one refresh: the visible entities it found, and the merged ordered lists.
    private readonly List<int> candidateSlots = [];
    private readonly List<ulong> mergedIds = [];
    private readonly List<bool> mergedNear = [];
    private readonly List<int> mergedSlots = [];

    // Per slot: the entity, 0 for a free slot, its position and cell, and its index in the cell.
    private ulong[] slotEntityIds = new ulong[InitialSlotCapacity];
    private float[] slotX = new float[InitialSlotCapacity];
    private float[] slotZ = new float[InitialSlotCapacity];
    private long[] slotCells = new long[InitialSlotCapacity];
    private int[] slotIndexInCell = new int[InitialSlotCapacity];

    // Per slot, the stamp of the last synchronization that listed it, and for the refresh that
    // runs: whether the entity was visible and near before it, and whether it is visible and near
    // now. A slot is marked when its stamp equals the current one, so nothing is cleared.
    private uint[] synchronizedStamps = new uint[InitialSlotCapacity];
    private uint[] wasVisibleStamps = new uint[InitialSlotCapacity];
    private bool[] wasNear = new bool[InitialSlotCapacity];
    private uint[] visibleStamps = new uint[InitialSlotCapacity];
    private bool[] near = new bool[InitialSlotCapacity];
    private int usedSlots;
    private uint synchronizeStamp;
    private uint refreshStamp;

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
            || config.NearExitRadius < config.NearRadius)
        {
            throw new ArgumentException("Interest management configuration is invalid.", nameof(config));
        }

        cellSize = config.CellSize;
        RefreshIntervalSnapshots = Math.Max(1, config.RefreshIntervalSnapshots);
        enterRadiusSquared = config.EnterRadius * config.EnterRadius;
        exitRadiusSquared = config.ExitRadius * config.ExitRadius;
        nearRadiusSquared = config.NearRadius * config.NearRadius;
        nearExitRadiusSquared = config.NearExitRadius * config.NearExitRadius;
        cellSkipDistanceSquared = (config.ExitRadius + 1f) * (config.ExitRadius + 1f);
        cellRadius = (int)Math.Ceiling(config.ExitRadius / config.CellSize);
    }

    // Each connection's visible set is refreshed every this many snapshots.
    public int RefreshIntervalSnapshots { get; }

    public int EntityCount => slotByEntityId.Count;

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
                        state.OrderedNear.Insert(~index, distanceSquared <= nearRadiusSquared);
                        state.OrderedSlots.Insert(~index, entitySlot);
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
            return new SimulationInterestUpdate(EmptyOrderedVisible, EmptyOrderedVisible, EmptyVisible);
        }

        if (!interestsByConnection.TryGetValue(connectionId, out var state))
        {
            state = new ConnectionInterestState { Version = ++visibleVersion };
            interestsByConnection.Add(connectionId, state);
        }

        BindControlledEntity(connectionId, state, controlledEntityId);
        var stamp = NextRefreshStamp();
        var orderedIds = CollectionsMarshal.AsSpan(state.OrderedVisible);
        var orderedNear = CollectionsMarshal.AsSpan(state.OrderedNear);
        var orderedSlots = CollectionsMarshal.AsSpan(state.OrderedSlots);
        for (var index = 0; index < orderedIds.Length; index++)
        {
            var slot = ResolveSlot(orderedIds[index], orderedSlots[index]);
            orderedSlots[index] = slot;
            if (slot >= 0)
            {
                wasVisibleStamps[slot] = stamp;
                wasNear[slot] = orderedNear[index];
            }
        }

        // Within the enter radius an entity is visible either way, so only the band up to the
        // exit radius, and the band up to the near exit radius, need what was seen before.
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

        if (state.Entered.Count == 0 && state.Exited.Count == 0)
        {
            // The same entities: only the tiers may change.
            for (var index = 0; index < orderedIds.Length; index++)
            {
                orderedNear[index] = near[orderedSlots[index]];
            }
        }
        else
        {
            state.Entered.Sort();
            ApplyMembershipChange(state, stamp);
        }

        return new SimulationInterestUpdate(state.Entered, state.Exited, state.Visible);
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

    public bool IsVisibleToAnyConnection(ulong entityId)
    {
        foreach (var state in interestsByConnection.Values)
        {
            if (state.Visible.Contains(entityId))
            {
                return true;
            }
        }

        return false;
    }

    public bool RemoveConnection(int connectionId)
    {
        if (!interestsByConnection.Remove(connectionId, out var state))
        {
            return false;
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

        if (connectionIdsByControlledEntityId.Remove(entityId, out var connectionId)
            && interestsByConnection.TryGetValue(connectionId, out var controlledState)
            && controlledState.ControlledEntityId == entityId)
        {
            controlledState.ControlledEntityId = 0;
        }

        var affectedConnections = new List<int>();
        foreach (var pair in interestsByConnection)
        {
            var state = pair.Value;
            if (!state.Visible.Remove(entityId))
            {
                continue;
            }

            var orderedIndex = state.OrderedVisible.BinarySearch(entityId);
            if (orderedIndex >= 0)
            {
                state.OrderedVisible.RemoveAt(orderedIndex);
                state.OrderedNear.RemoveAt(orderedIndex);
                state.OrderedSlots.RemoveAt(orderedIndex);
                state.Version = ++visibleVersion;
            }

            affectedConnections.Add(pair.Key);
        }

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
    }

    // The new visible lists: the entities that stayed, in their order, merged with the ones that
    // entered, each with its tier. The visible set changes only by what entered and left.
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
        mergedSlots.Clear();
        var stayed = 0;
        var entered = 0;
        var orderedIds = state.OrderedVisible;
        var orderedSlots = state.OrderedSlots;
        while (stayed < orderedIds.Count || entered < state.Entered.Count)
        {
            if (stayed < orderedIds.Count)
            {
                var slot = orderedSlots[stayed];
                if (slot < 0 || visibleStamps[slot] != stamp)
                {
                    stayed++;
                    continue;
                }

                if (entered >= state.Entered.Count || orderedIds[stayed] < state.Entered[entered])
                {
                    AddMerged(orderedIds[stayed], slot);
                    stayed++;
                    continue;
                }
            }

            var enteredId = state.Entered[entered++];
            AddMerged(enteredId, slotByEntityId[enteredId]);
        }

        state.OrderedVisible.Clear();
        state.OrderedVisible.AddRange(mergedIds);
        state.OrderedNear.Clear();
        state.OrderedNear.AddRange(mergedNear);
        state.OrderedSlots.Clear();
        state.OrderedSlots.AddRange(mergedSlots);
        state.Version = ++visibleVersion;
    }

    private void AddMerged(ulong entityId, int slot)
    {
        mergedIds.Add(entityId);
        mergedNear.Add(near[slot]);
        mergedSlots.Add(slot);
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
        }

        state.ControlledEntityId = controlledEntityId;
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
        Array.Resize(ref visibleStamps, capacity);
        Array.Resize(ref near, capacity);
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

    private sealed class ConnectionInterestState
    {
        public ulong ControlledEntityId { get; set; }

        public HashSet<ulong> Visible { get; } = [];

        // The visible entities ascending by id, and parallel to them their tier and their slot,
        // which a refresh checks against the entity before it trusts it.
        public List<ulong> OrderedVisible { get; } = [];

        public List<bool> OrderedNear { get; } = [];

        public List<int> OrderedSlots { get; } = [];

        // The version of OrderedVisible.
        public ulong Version { get; set; }

        public List<ulong> Entered { get; } = [];

        public List<ulong> Exited { get; } = [];
    }
}
