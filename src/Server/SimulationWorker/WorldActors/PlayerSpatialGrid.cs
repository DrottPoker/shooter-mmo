using Mmorpg.SimulationWorker.Entities;

namespace Mmorpg.SimulationWorker.WorldActors;

// The planar positions of every player in square cells, rebuilt once per tick. Mob
// activity and detection ask it for nearby players instead of comparing every Mob with
// every player. Loop-confined.
public sealed class PlayerSpatialGrid
{
    public const float DefaultCellSize = 16f;

    private readonly Dictionary<long, List<PlayerSimulationEntity>> cells = [];
    private readonly Stack<List<PlayerSimulationEntity>> freeLists = new();
    private readonly float cellSize;

    public PlayerSpatialGrid(float cellSize = DefaultCellSize)
    {
        if (!float.IsFinite(cellSize) || cellSize <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        }

        this.cellSize = cellSize;
    }

    public int Count { get; private set; }

    public static PlayerSpatialGrid From(IReadOnlyList<PlayerSimulationEntity> players)
    {
        var grid = new PlayerSpatialGrid();
        grid.Rebuild(players);
        return grid;
    }

    public void Rebuild(IReadOnlyList<PlayerSimulationEntity> players)
    {
        ArgumentNullException.ThrowIfNull(players);
        foreach (var list in cells.Values)
        {
            list.Clear();
            freeLists.Push(list);
        }

        cells.Clear();
        Count = 0;
        for (var index = 0; index < players.Count; index++)
        {
            var player = players[index];
            var state = player.Movement.State;
            var key = KeyOf(CellOf(state.PositionX), CellOf(state.PositionZ));
            if (!cells.TryGetValue(key, out var list))
            {
                list = freeLists.Count > 0 ? freeLists.Pop() : [];
                cells.Add(key, list);
            }

            list.Add(player);
            Count++;
        }
    }

    // Whether any player stands within the planar range.
    public bool AnyWithin(float x, float z, float range)
    {
        return FindNearest(x, z, range, null) is not null;
    }

    // The nearest player within the planar range that the filter accepts, or null. Ties
    // go to the lower entity id, so the choice is reproducible.
    public PlayerSimulationEntity? FindNearest(
        float x,
        float z,
        float range,
        Func<PlayerSimulationEntity, bool>? accept)
    {
        if (Count == 0 || !float.IsFinite(range) || range < 0f)
        {
            return null;
        }

        var rangeSquared = range * range;
        PlayerSimulationEntity? nearest = null;
        var nearestDistanceSquared = float.MaxValue;
        var minimumCellX = CellOf(x - range);
        var maximumCellX = CellOf(x + range);
        var minimumCellZ = CellOf(z - range);
        var maximumCellZ = CellOf(z + range);
        for (var cellX = minimumCellX; cellX <= maximumCellX; cellX++)
        {
            for (var cellZ = minimumCellZ; cellZ <= maximumCellZ; cellZ++)
            {
                if (!cells.TryGetValue(KeyOf(cellX, cellZ), out var list))
                {
                    continue;
                }

                for (var index = 0; index < list.Count; index++)
                {
                    var player = list[index];
                    var state = player.Movement.State;
                    var deltaX = state.PositionX - x;
                    var deltaZ = state.PositionZ - z;
                    var distanceSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
                    if (distanceSquared > rangeSquared
                        || distanceSquared > nearestDistanceSquared
                        || (distanceSquared == nearestDistanceSquared
                            && nearest is not null
                            && player.NetworkEntityId > nearest.NetworkEntityId)
                        || (accept is not null && !accept(player)))
                    {
                        continue;
                    }

                    nearest = player;
                    nearestDistanceSquared = distanceSquared;
                }
            }
        }

        return nearest;
    }

    private int CellOf(float coordinate)
    {
        return (int)MathF.Floor(coordinate / cellSize);
    }

    private static long KeyOf(int cellX, int cellZ)
    {
        return ((long)cellX << 32) | (uint)cellZ;
    }
}
