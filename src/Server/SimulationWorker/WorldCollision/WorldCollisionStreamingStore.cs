using System.Runtime.ExceptionServices;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;

namespace Mmorpg.SimulationWorker.WorldCollision;

public sealed record WorldCollisionChunkSource(
    int X,
    int Z,
    string ResourceName,
    string FilePath,
    string Sha256);

// The collision chunks of the worker's World, loaded around anchors such as the players. Before
// the loop starts, LoadBeforeStart and LoadAll read synchronously. On the loop, Refresh only
// starts loads for chunks that became required, takes finished chunks in from the background
// loader, and unloads what left the retain radius; the plan of required and retained chunks
// changes only when an anchor entered another chunk, appeared, or left. A chunk that failed to
// load fails every refresh while it is required, and is tried again once per refresh.
// Loop-confined once the loop runs, except the loader, which hands over through its queue.
public sealed class WorldCollisionStreamingStore : IDisposable
{
    private readonly Dictionary<CollisionChunkCoordinate, WorldCollisionChunkSource> sources;
    private readonly Func<WorldCollisionChunkSource, CollisionChunk> read;
    private readonly WorldCollisionChunkLoader loader;
    private readonly CollisionChunkStreamingPlanner planner;
    private readonly HashSet<CollisionChunkCoordinate> loadedCoordinates = [];
    private readonly HashSet<CollisionChunkCoordinate> loadingCoordinates = [];
    private readonly Dictionary<CollisionChunkCoordinate, ExceptionDispatchInfo> failedCoordinates = [];

    // Chunks whose own chunk and eight neighbours are loaded or have no chunk at all. A step
    // touches only these, so one lookup decides whether a mover may step.
    private readonly HashSet<CollisionChunkCoordinate> loadedAreas = [];
    private readonly HashSet<CollisionChunkCoordinate> required = [];
    private readonly HashSet<CollisionChunkCoordinate> retained = [];
    private readonly List<CollisionChunkCoordinate> scratch = [];
    private List<CollisionChunkCoordinate> anchorChunks = [];
    private List<CollisionChunkCoordinate> plannedAnchorChunks = [];
    private bool planned;

    public WorldCollisionStreamingStore(
        string worldId,
        string revision,
        float chunkSize,
        IEnumerable<WorldCollisionChunkSource> chunkSources,
        CollisionStreamingConfig config,
        Func<WorldCollisionChunkSource, CollisionChunk>? read = null)
    {
        ArgumentNullException.ThrowIfNull(chunkSources);
        ArgumentNullException.ThrowIfNull(config);
        sources = chunkSources.ToDictionary(
            source => new CollisionChunkCoordinate(source.X, source.Z));
        if (sources.Count == 0)
        {
            throw new ArgumentException("Collision streaming requires at least one chunk source.", nameof(chunkSources));
        }

        CollisionWorld = new ChunkedStaticCollisionWorld(worldId, revision, chunkSize);
        planner = new CollisionChunkStreamingPlanner(
            chunkSize,
            config.LoadRadiusChunks,
            config.UnloadRadiusChunks);
        this.read = read ?? WorldCollisionChunkReader.Read;
        loader = new WorldCollisionChunkLoader(this.read);
    }

    public ChunkedStaticCollisionWorld CollisionWorld { get; }

    public int AvailableChunkCount => sources.Count;

    public int LoadedChunkCount => loadedCoordinates.Count;

    public int LoadingChunkCount => loadingCoordinates.Count;

    // How often the required and retained chunks were planned, for tests.
    public long PlanCount { get; private set; }

    // Loads the chunks around the anchors synchronously, before the loop starts.
    public void LoadBeforeStart(IEnumerable<SimulationVector3> anchors)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        anchorChunks.Clear();
        foreach (var anchor in anchors)
        {
            anchorChunks.Add(planner.ToChunk(anchor));
        }

        planner.FillChunks(anchorChunks, required, retained);
        foreach (var coordinate in required)
        {
            LoadNow(coordinate);
        }
    }

    // Loads every chunk synchronously, before the loop starts.
    public void LoadAll()
    {
        foreach (var coordinate in sources.Keys)
        {
            LoadNow(coordinate);
        }
    }

    // Loop-side, once per tick: plans when an anchor changed its chunk, takes finished chunks in,
    // and throws the failure of a required chunk that could not be loaded.
    public void Refresh(IReadOnlyList<SimulationVector3> anchors)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        anchorChunks.Clear();
        for (var index = 0; index < anchors.Count; index++)
        {
            anchorChunks.Add(planner.ToChunk(anchors[index]));
        }

        if (!planned || !SameChunks(anchorChunks, plannedAnchorChunks))
        {
            (anchorChunks, plannedAnchorChunks) = (plannedAnchorChunks, anchorChunks);
            Plan();
        }

        TakeFinishedChunks();
        if (failedCoordinates.Count > 0)
        {
            RetryAndThrowFailure();
        }
    }

    // Whether the chunk at the position and its eight neighbours are loaded, or have no chunk.
    // One lookup once an area was found loaded, until a chunk of it unloads.
    public bool IsAreaLoaded(SimulationVector3 position)
    {
        var center = planner.ToChunk(position);
        if (loadedAreas.Contains(center))
        {
            return true;
        }

        for (var z = center.Z - 1; z <= center.Z + 1; z++)
        {
            for (var x = center.X - 1; x <= center.X + 1; x++)
            {
                var coordinate = new CollisionChunkCoordinate(x, z);
                if (sources.ContainsKey(coordinate) && !loadedCoordinates.Contains(coordinate))
                {
                    return false;
                }
            }
        }

        loadedAreas.Add(center);
        return true;
    }

    public void Dispose()
    {
        loader.Dispose();
    }

    private void Plan()
    {
        PlanCount++;
        planned = true;
        planner.FillChunks(plannedAnchorChunks, required, retained);
        foreach (var coordinate in required)
        {
            if (!loadedCoordinates.Contains(coordinate)
                && !loadingCoordinates.Contains(coordinate)
                && !failedCoordinates.ContainsKey(coordinate)
                && sources.TryGetValue(coordinate, out var source))
            {
                loadingCoordinates.Add(coordinate);
                loader.Request(source);
            }
        }

        scratch.Clear();
        foreach (var coordinate in loadedCoordinates)
        {
            if (!retained.Contains(coordinate))
            {
                scratch.Add(coordinate);
            }
        }

        foreach (var coordinate in scratch)
        {
            Unload(coordinate);
        }

        // A failure counts while its chunk is required; a later need loads it afresh.
        if (failedCoordinates.Count > 0)
        {
            scratch.Clear();
            foreach (var coordinate in failedCoordinates.Keys)
            {
                if (!required.Contains(coordinate))
                {
                    scratch.Add(coordinate);
                }
            }

            foreach (var coordinate in scratch)
            {
                failedCoordinates.Remove(coordinate);
            }
        }
    }

    private void TakeFinishedChunks()
    {
        while (loader.TryTake(out var load))
        {
            var coordinate = new CollisionChunkCoordinate(load.Source.X, load.Source.Z);
            loadingCoordinates.Remove(coordinate);
            if (load.Failure is not null)
            {
                if (required.Contains(coordinate))
                {
                    failedCoordinates[coordinate] = load.Failure;
                }

                continue;
            }

            // A chunk that left the retain radius while it loaded is dropped.
            if (!retained.Contains(coordinate) || loadedCoordinates.Contains(coordinate))
            {
                continue;
            }

            try
            {
                Insert(load.Source, load.Chunk!);
                failedCoordinates.Remove(coordinate);
            }
            catch (Exception exception)
            {
                failedCoordinates[coordinate] = ExceptionDispatchInfo.Capture(exception);
            }
        }
    }

    private void RetryAndThrowFailure()
    {
        ExceptionDispatchInfo? first = null;
        foreach (var (coordinate, failure) in failedCoordinates)
        {
            first ??= failure;
            if (loadingCoordinates.Add(coordinate))
            {
                loader.Request(sources[coordinate]);
            }
        }

        first?.Throw();
    }

    private void LoadNow(CollisionChunkCoordinate coordinate)
    {
        if (loadedCoordinates.Contains(coordinate)
            || !sources.TryGetValue(coordinate, out var source))
        {
            return;
        }

        Insert(source, read(source));
    }

    private void Insert(WorldCollisionChunkSource source, CollisionChunk chunk)
    {
        if (!CollisionWorld.TryLoadChunk(chunk))
        {
            throw new InvalidOperationException(
                $"Collision chunk '{source.ResourceName}' could not be loaded into the collision world.");
        }

        loadedCoordinates.Add(new CollisionChunkCoordinate(source.X, source.Z));
    }

    private void Unload(CollisionChunkCoordinate coordinate)
    {
        CollisionWorld.UnloadChunk(coordinate.X, coordinate.Z);
        loadedCoordinates.Remove(coordinate);
        for (var z = coordinate.Z - 1; z <= coordinate.Z + 1; z++)
        {
            for (var x = coordinate.X - 1; x <= coordinate.X + 1; x++)
            {
                loadedAreas.Remove(new CollisionChunkCoordinate(x, z));
            }
        }
    }

    private static bool SameChunks(
        List<CollisionChunkCoordinate> current,
        List<CollisionChunkCoordinate> previous)
    {
        if (current.Count != previous.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            if (!current[index].Equals(previous[index]))
            {
                return false;
            }
        }

        return true;
    }
}
