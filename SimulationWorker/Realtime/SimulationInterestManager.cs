using SimulationWorker.Config;

namespace SimulationWorker.Realtime;

public readonly record struct SimulationInterestEntity(ulong EntityId, float X, float Z);

public sealed record SimulationInterestUpdate(
    IReadOnlyList<ulong> Entered,
    IReadOnlyList<ulong> Exited,
    IReadOnlySet<ulong> Visible);

public sealed class SimulationInterestManager
{
    private static readonly IReadOnlySet<ulong> EmptyVisible = new HashSet<ulong>();
    private static readonly IReadOnlyList<ulong> EmptyOrderedVisible = Array.Empty<ulong>();

    private readonly float cellSize;
    private readonly float enterRadiusSquared;
    private readonly float exitRadiusSquared;
    private readonly int cellRadius;
    private readonly Dictionary<ulong, SimulationInterestEntity> entities = [];
    private readonly Dictionary<long, List<ulong>> entityIdsByCell = [];
    private readonly Stack<List<ulong>> cellListPool = [];
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
            || config.ExitRadius / config.CellSize > 64f)
        {
            throw new ArgumentException("Interest management configuration is invalid.", nameof(config));
        }

        cellSize = config.CellSize;
        enterRadiusSquared = config.EnterRadius * config.EnterRadius;
        exitRadiusSquared = config.ExitRadius * config.ExitRadius;
        cellRadius = (int)Math.Ceiling(config.ExitRadius / config.CellSize);
    }

    public void Rebuild(IEnumerable<SimulationInterestEntity> currentEntities)
    {
        ArgumentNullException.ThrowIfNull(currentEntities);
        entities.Clear();
        foreach (var cellEntities in entityIdsByCell.Values)
        {
            cellEntities.Clear();
            cellListPool.Push(cellEntities);
        }

        entityIdsByCell.Clear();
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
        VisitNearbyEntityIds(entity, candidateEntityId =>
        {
            if (candidateEntityId == entity.EntityId
                || !connectionIdsByControlledEntityId.TryGetValue(
                    candidateEntityId,
                    out var connectionId)
                || !interestsByConnection.TryGetValue(connectionId, out var state)
                || !IsWithinRadius(
                    entities[candidateEntityId],
                    entity,
                    enterRadiusSquared)
                || !state.Visible.Add(entity.EntityId))
            {
                return;
            }

            InsertOrdered(state.OrderedVisible, entity.EntityId);
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
        if (previousCell != nextCell)
        {
            RemoveFromCell(previousCell, entity.EntityId);
            AddToCell(nextCell, entity.EntityId);
        }

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
        AddNearbyEntities(observer, state.Visible, state.Candidates);

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
            if (!state.Visible.Remove(entityId))
            {
                continue;
            }

            var orderedIndex = state.OrderedVisible.BinarySearch(entityId);
            if (orderedIndex >= 0)
            {
                state.OrderedVisible.RemoveAt(orderedIndex);
            }

            affectedConnections.Add(pair.Key);
        }

        return affectedConnections;
    }

    public void Clear()
    {
        entities.Clear();
        foreach (var cellEntities in entityIdsByCell.Values)
        {
            cellEntities.Clear();
            cellListPool.Push(cellEntities);
        }

        entityIdsByCell.Clear();
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
        AddToCell(CellKey(ToCell(entity.X), ToCell(entity.Z)), entity.EntityId);
    }

    private void AddToCell(long cellKey, ulong entityId)
    {
        if (!entityIdsByCell.TryGetValue(cellKey, out var cellEntities))
        {
            cellEntities = cellListPool.TryPop(out var pooled) ? pooled : [];
            entityIdsByCell.Add(cellKey, cellEntities);
        }

        cellEntities.Add(entityId);
    }

    private void RemoveFromCell(long cellKey, ulong entityId)
    {
        if (!entityIdsByCell.TryGetValue(cellKey, out var cellEntities))
        {
            return;
        }

        cellEntities.Remove(entityId);
        if (cellEntities.Count == 0)
        {
            entityIdsByCell.Remove(cellKey);
            cellListPool.Push(cellEntities);
        }
    }

    private void VisitNearbyEntityIds(
        SimulationInterestEntity observer,
        Action<ulong> visitor)
    {
        var observerCellX = ToCell(observer.X);
        var observerCellZ = ToCell(observer.Z);
        for (var z = observerCellZ - cellRadius; z <= observerCellZ + cellRadius; z++)
        {
            for (var x = observerCellX - cellRadius; x <= observerCellX + cellRadius; x++)
            {
                if (!entityIdsByCell.TryGetValue(CellKey(x, z), out var candidates))
                {
                    continue;
                }

                foreach (var entityId in candidates)
                {
                    visitor(entityId);
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

    private static void InsertOrdered(List<ulong> values, ulong value)
    {
        var index = values.BinarySearch(value);
        if (index < 0)
        {
            values.Insert(~index, value);
        }
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

    private void AddNearbyEntities(
        SimulationInterestEntity observer,
        IReadOnlySet<ulong> previous,
        ISet<ulong> next)
    {
        var observerCellX = ToCell(observer.X);
        var observerCellZ = ToCell(observer.Z);
        for (var z = observerCellZ - cellRadius; z <= observerCellZ + cellRadius; z++)
        {
            for (var x = observerCellX - cellRadius; x <= observerCellX + cellRadius; x++)
            {
                if (!entityIdsByCell.TryGetValue(CellKey(x, z), out var candidates))
                {
                    continue;
                }

                foreach (var entityId in candidates)
                {
                    var candidate = entities[entityId];
                    var deltaX = candidate.X - observer.X;
                    var deltaZ = candidate.Z - observer.Z;
                    var distanceSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
                    var allowedRadiusSquared = previous.Contains(entityId)
                        ? exitRadiusSquared
                        : enterRadiusSquared;
                    if (distanceSquared <= allowedRadiusSquared)
                    {
                        next.Add(entityId);
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

        public List<ulong> Entered { get; } = [];

        public List<ulong> Exited { get; } = [];
    }
}
