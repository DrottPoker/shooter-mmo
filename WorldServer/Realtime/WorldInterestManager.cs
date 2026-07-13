using WorldServer.Config;

namespace WorldServer.Realtime;

public readonly record struct WorldInterestEntity(ulong EntityId, float X, float Z);

public sealed record WorldInterestUpdate(
    IReadOnlyList<ulong> Entered,
    IReadOnlyList<ulong> Exited,
    IReadOnlySet<ulong> Visible);

public sealed class WorldInterestManager
{
    private readonly float cellSize;
    private readonly float enterRadiusSquared;
    private readonly float exitRadiusSquared;
    private readonly Dictionary<ulong, WorldInterestEntity> entities = [];
    private readonly Dictionary<long, List<ulong>> entityIdsByCell = [];
    private readonly Dictionary<int, HashSet<ulong>> visibleEntityIdsByConnection = [];

    public WorldInterestManager(InterestManagementConfig config)
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
    }

    public void Rebuild(IEnumerable<WorldInterestEntity> currentEntities)
    {
        ArgumentNullException.ThrowIfNull(currentEntities);
        entities.Clear();
        entityIdsByCell.Clear();
        foreach (var entity in currentEntities)
        {
            if (entity.EntityId == 0 || !IsFinite(entity.X) || !IsFinite(entity.Z))
            {
                throw new ArgumentException("Interest entities must have a nonzero id and finite positions.", nameof(currentEntities));
            }

            entities[entity.EntityId] = entity;
            var key = CellKey(ToCell(entity.X), ToCell(entity.Z));
            if (!entityIdsByCell.TryGetValue(key, out var cellEntities))
            {
                cellEntities = [];
                entityIdsByCell.Add(key, cellEntities);
            }

            cellEntities.Add(entity.EntityId);
        }
    }

    public WorldInterestUpdate Refresh(int connectionId, ulong controlledEntityId)
    {
        if (!entities.TryGetValue(controlledEntityId, out var observer))
        {
            RemoveConnection(connectionId);
            return new WorldInterestUpdate([], [], new HashSet<ulong>());
        }

        if (!visibleEntityIdsByConnection.TryGetValue(connectionId, out var previous))
        {
            previous = [];
            visibleEntityIdsByConnection.Add(connectionId, previous);
        }

        var next = new HashSet<ulong> { controlledEntityId };
        AddNearbyEntities(observer, previous, next);
        var entered = next.Where(entityId => !previous.Contains(entityId)).Order().ToArray();
        var exited = previous.Where(entityId => !next.Contains(entityId)).Order().ToArray();
        visibleEntityIdsByConnection[connectionId] = next;
        return new WorldInterestUpdate(entered, exited, next);
    }

    public IReadOnlySet<ulong> GetVisible(int connectionId)
    {
        return visibleEntityIdsByConnection.TryGetValue(connectionId, out var visible)
            ? visible
            : new HashSet<ulong>();
    }

    public bool RemoveConnection(int connectionId)
    {
        return visibleEntityIdsByConnection.Remove(connectionId);
    }

    public IReadOnlyList<int> ForgetEntity(ulong entityId)
    {
        entities.Remove(entityId);
        var affectedConnections = new List<int>();
        foreach (var pair in visibleEntityIdsByConnection)
        {
            if (pair.Value.Remove(entityId))
            {
                affectedConnections.Add(pair.Key);
            }
        }

        return affectedConnections;
    }

    public void Clear()
    {
        entities.Clear();
        entityIdsByCell.Clear();
        visibleEntityIdsByConnection.Clear();
    }

    private void AddNearbyEntities(
        WorldInterestEntity observer,
        IReadOnlySet<ulong> previous,
        ISet<ulong> next)
    {
        var cellRadius = (int)Math.Ceiling(Math.Sqrt(exitRadiusSquared) / cellSize);
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
}
