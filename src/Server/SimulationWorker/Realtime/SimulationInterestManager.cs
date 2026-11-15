using Mmorpg.SimulationWorker.Config;

namespace Mmorpg.SimulationWorker.Realtime;

public readonly record struct SimulationInterestEntity(ulong EntityId, float X, float Z);

public sealed record SimulationInterestUpdate(
    IReadOnlyList<ulong> Entered,
    IReadOnlyList<ulong> Exited,
    IReadOnlySet<ulong> Visible);

public sealed class SimulationInterestManager
{
    private static readonly IReadOnlySet<ulong> EmptyVisible = new HashSet<ulong>();
    private static readonly IReadOnlyList<ulong> EmptyOrderedVisible = Array.Empty<ulong>();
    private static readonly IReadOnlyList<bool> EmptyOrderedNear = Array.Empty<bool>();

    private readonly float cellSize;
    private readonly float enterRadiusSquared;
    private readonly float exitRadiusSquared;
    private readonly float nearRadiusSquared;
    private readonly float nearExitRadiusSquared;
    private readonly int cellRadius;
    private readonly Dictionary<ulong, SimulationInterestEntity> entities = [];
    // Cells hold the entities themselves, so the visibility test reads positions without a
    // dictionary lookup per candidate.
    private readonly Dictionary<long, List<SimulationInterestEntity>> entitiesByCell = [];
    private readonly Stack<List<SimulationInterestEntity>> cellListPool = [];
    private readonly Dictionary<int, ConnectionInterestState> interestsByConnection = [];
    private readonly Dictionary<ulong, int> connectionIdsByControlledEntityId = [];

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
        cellRadius = (int)Math.Ceiling(config.ExitRadius / config.CellSize);
    }

    // Each connection's visible set is refreshed every this many snapshots.
    public int RefreshIntervalSnapshots { get; }

    public void Rebuild(IEnumerable<SimulationInterestEntity> currentEntities)
    {
        ArgumentNullException.ThrowIfNull(currentEntities);
        entities.Clear();
        foreach (var cellEntities in entitiesByCell.Values)
        {
            cellEntities.Clear();
            cellListPool.Push(cellEntities);
        }

        entitiesByCell.Clear();
        foreach (var entity in currentEntities)
        {
            ValidateEntity(entity, nameof(currentEntities));
            AddToSpatialIndex(entity);
        }
    }

    public IReadOnlyList<int> AddEntity(SimulationInterestEntity entity)
    {
        ValidateEntity(entity, nameof(entity));
        if (entities.ContainsKey(entity.EntityId))
        {
            throw new InvalidOperationException(
                $"Interest entity {entity.EntityId} is already registered.");
        }

        AddToSpatialIndex(entity);
        var enteredConnections = new List<int>();
        VisitNearbyEntities(entity, candidate =>
        {
            if (candidate.EntityId == entity.EntityId
                || !connectionIdsByControlledEntityId.TryGetValue(
                    candidate.EntityId,
                    out var connectionId)
                || !interestsByConnection.TryGetValue(connectionId, out var state)
                || !IsWithinRadius(
                    candidate,
                    entity,
                    enterRadiusSquared)
                || !state.Visible.Add(entity.EntityId))
            {
                return;
            }

            var near = IsWithinRadius(candidate, entity, nearRadiusSquared);
            if (near)
            {
                state.Near.Add(entity.EntityId);
            }

            var index = state.OrderedVisible.BinarySearch(entity.EntityId);
            if (index < 0)
            {
                state.OrderedVisible.Insert(~index, entity.EntityId);
                state.OrderedNear.Insert(~index, near);
            }

            enteredConnections.Add(connectionId);
        });
        enteredConnections.Sort();
        return enteredConnections;
    }

    public void UpdateEntity(SimulationInterestEntity entity)
    {
        ValidateEntity(entity, nameof(entity));
        if (!entities.TryGetValue(entity.EntityId, out var previous))
        {
            throw new InvalidOperationException(
                $"Interest entity {entity.EntityId} is not registered.");
        }

        var previousCell = CellKey(ToCell(previous.X), ToCell(previous.Z));
        var nextCell = CellKey(ToCell(entity.X), ToCell(entity.Z));
        RemoveFromCell(previousCell, entity.EntityId);
        AddToCell(nextCell, entity);
        entities[entity.EntityId] = entity;
    }

    public SimulationInterestUpdate Refresh(int connectionId, ulong controlledEntityId)
    {
        if (!entities.TryGetValue(controlledEntityId, out var observer))
        {
            RemoveConnection(connectionId);
            return new SimulationInterestUpdate([], [], EmptyVisible);
        }

        if (!interestsByConnection.TryGetValue(connectionId, out var state))
        {
            state = new ConnectionInterestState();
            interestsByConnection.Add(connectionId, state);
        }

        BindControlledEntity(connectionId, state, controlledEntityId);

        state.Candidates.Clear();
        state.Candidates.Add(controlledEntityId);
        state.NextNear.Clear();
        AddNearbyEntities(observer, state);
        (state.Near, state.NextNear) = (state.NextNear, state.Near);

        state.Entered.Clear();
        foreach (var entityId in state.Candidates)
        {
            if (!state.Visible.Contains(entityId))
            {
                state.Entered.Add(entityId);
            }
        }

        state.Exited.Clear();
        foreach (var entityId in state.Visible)
        {
            if (!state.Candidates.Contains(entityId))
            {
                state.Exited.Add(entityId);
            }
        }

        if (state.Entered.Count > 0 || state.Exited.Count > 0)
        {
            state.Entered.Sort();
            state.Exited.Sort();
            state.Visible.Clear();
            state.Visible.UnionWith(state.Candidates);
            state.OrderedVisible.Clear();
            state.OrderedVisible.AddRange(state.Visible);
            state.OrderedVisible.Sort();
        }

        // The tiers may change without a change of the visible set.
        state.OrderedNear.Clear();
        foreach (var entityId in state.OrderedVisible)
        {
            state.OrderedNear.Add(state.Near.Contains(entityId));
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

    public bool IsNear(int connectionId, ulong entityId)
    {
        return interestsByConnection.TryGetValue(connectionId, out var state)
            && state.Near.Contains(entityId);
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
        if (entities.Remove(entityId, out var entity))
        {
            RemoveFromCell(
                CellKey(ToCell(entity.X), ToCell(entity.Z)),
                entityId);
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
            state.Candidates.Remove(entityId);
            state.Near.Remove(entityId);
            if (!state.Visible.Remove(entityId))
            {
                continue;
            }

            var orderedIndex = state.OrderedVisible.BinarySearch(entityId);
            if (orderedIndex >= 0)
            {
                state.OrderedVisible.RemoveAt(orderedIndex);
                state.OrderedNear.RemoveAt(orderedIndex);
            }

            affectedConnections.Add(pair.Key);
        }

        return affectedConnections;
    }

    public void Clear()
    {
        entities.Clear();
        foreach (var cellEntities in entitiesByCell.Values)
        {
            cellEntities.Clear();
            cellListPool.Push(cellEntities);
        }

        entitiesByCell.Clear();
        interestsByConnection.Clear();
        connectionIdsByControlledEntityId.Clear();
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

    private void AddToSpatialIndex(SimulationInterestEntity entity)
    {
        entities.Add(entity.EntityId, entity);
        AddToCell(CellKey(ToCell(entity.X), ToCell(entity.Z)), entity);
    }

    private void AddToCell(long cellKey, SimulationInterestEntity entity)
    {
        if (!entitiesByCell.TryGetValue(cellKey, out var cellEntities))
        {
            cellEntities = cellListPool.TryPop(out var pooled) ? pooled : [];
            entitiesByCell.Add(cellKey, cellEntities);
        }

        cellEntities.Add(entity);
    }

    private void RemoveFromCell(long cellKey, ulong entityId)
    {
        if (!entitiesByCell.TryGetValue(cellKey, out var cellEntities))
        {
            return;
        }

        for (var index = 0; index < cellEntities.Count; index++)
        {
            if (cellEntities[index].EntityId == entityId)
            {
                // The order inside a cell does not matter, so the last entry fills the gap.
                cellEntities[index] = cellEntities[^1];
                cellEntities.RemoveAt(cellEntities.Count - 1);
                break;
            }
        }

        if (cellEntities.Count == 0)
        {
            entitiesByCell.Remove(cellKey);
            cellListPool.Push(cellEntities);
        }
    }

    private void VisitNearbyEntities(
        SimulationInterestEntity observer,
        Action<SimulationInterestEntity> visitor)
    {
        var observerCellX = ToCell(observer.X);
        var observerCellZ = ToCell(observer.Z);
        for (var z = observerCellZ - cellRadius; z <= observerCellZ + cellRadius; z++)
        {
            for (var x = observerCellX - cellRadius; x <= observerCellX + cellRadius; x++)
            {
                if (!entitiesByCell.TryGetValue(CellKey(x, z), out var candidates))
                {
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    visitor(candidate);
                }
            }
        }
    }

    private static bool IsWithinRadius(
        SimulationInterestEntity left,
        SimulationInterestEntity right,
        float radiusSquared)
    {
        var deltaX = right.X - left.X;
        var deltaZ = right.Z - left.Z;
        return ((deltaX * deltaX) + (deltaZ * deltaZ)) <= radiusSquared;
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

    // Adds the entities the observer sees to the candidates and the near ones to the next near
    // tier: within the near radius, or within the near exit radius while already near.
    private void AddNearbyEntities(SimulationInterestEntity observer, ConnectionInterestState state)
    {
        var previous = state.Visible;
        var next = state.Candidates;
        var observerCellX = ToCell(observer.X);
        var observerCellZ = ToCell(observer.Z);
        for (var z = observerCellZ - cellRadius; z <= observerCellZ + cellRadius; z++)
        {
            for (var x = observerCellX - cellRadius; x <= observerCellX + cellRadius; x++)
            {
                if (!entitiesByCell.TryGetValue(CellKey(x, z), out var candidates))
                {
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    var deltaX = candidate.X - observer.X;
                    var deltaZ = candidate.Z - observer.Z;
                    var distanceSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
                    // Within the enter radius the entity is visible either way, so only the
                    // band up to the exit radius needs the previous set.
                    if (distanceSquared <= enterRadiusSquared
                        || (distanceSquared <= exitRadiusSquared
                            && previous.Contains(candidate.EntityId)))
                    {
                        next.Add(candidate.EntityId);
                        if (distanceSquared <= nearRadiusSquared
                            || (distanceSquared <= nearExitRadiusSquared
                                && state.Near.Contains(candidate.EntityId)))
                        {
                            state.NextNear.Add(candidate.EntityId);
                        }
                    }
                }
            }
        }
    }

    private int ToCell(float position)
    {
        return (int)Math.Floor(position / cellSize);
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

        public HashSet<ulong> Candidates { get; } = [];

        public List<ulong> OrderedVisible { get; } = [];

        // The near tier, and parallel to OrderedVisible whether each entity is in it.
        public HashSet<ulong> Near { get; set; } = [];

        public HashSet<ulong> NextNear { get; set; } = [];

        public List<bool> OrderedNear { get; } = [];

        public List<ulong> Entered { get; } = [];

        public List<ulong> Exited { get; } = [];
    }
}
