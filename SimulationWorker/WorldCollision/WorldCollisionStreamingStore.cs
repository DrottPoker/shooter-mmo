using ShooterMmo.GameSimulation;
using SimulationWorker.Config;

namespace SimulationWorker.WorldCollision;

public sealed record WorldCollisionChunkSource(
    int X,
    int Z,
    string ResourceName,
    string FilePath,
    string Sha256);

public sealed class WorldCollisionStreamingStore
{
    private readonly Dictionary<CollisionChunkCoordinate, WorldCollisionChunkSource> sources;
    private readonly HashSet<CollisionChunkCoordinate> loadedCoordinates = [];
    private readonly CollisionChunkStreamingPlanner planner;

    public WorldCollisionStreamingStore(
        string worldId,
        string revision,
        float chunkSize,
        IEnumerable<WorldCollisionChunkSource> chunkSources,
        CollisionStreamingConfig config)
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
    }

    public ChunkedStaticCollisionWorld CollisionWorld { get; }

    public int AvailableChunkCount => sources.Count;

    public int LoadedChunkCount => loadedCoordinates.Count;

    public void Refresh(IEnumerable<SimulationVector3> anchors)
    {
        var anchorSnapshot = anchors.ToArray();
        var required = planner.GetRequiredChunks(anchorSnapshot);
        var retained = planner.GetRetainedChunks(anchorSnapshot);
        foreach (var coordinate in required)
        {
            TryLoad(coordinate);
        }

        foreach (var coordinate in loadedCoordinates.ToArray())
        {
            if (!retained.Contains(coordinate))
            {
                CollisionWorld.UnloadChunk(coordinate.X, coordinate.Z);
                loadedCoordinates.Remove(coordinate);
            }
        }
    }

    public void LoadAll()
    {
        foreach (var coordinate in sources.Keys)
        {
            TryLoad(coordinate);
        }
    }

    private void TryLoad(CollisionChunkCoordinate coordinate)
    {
        if (loadedCoordinates.Contains(coordinate)
            || !sources.TryGetValue(coordinate, out var source))
        {
            return;
        }

        var bytes = File.ReadAllBytes(source.FilePath);
        var actualHash = CollisionWorldCompiler.ComputeSha256Hex(bytes);
        if (!string.Equals(actualHash, source.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Collision chunk '{source.ResourceName}' failed checksum validation while streaming.");
        }

        CollisionChunk chunk;
        try
        {
            chunk = CollisionChunkCodec.Decode(bytes);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidOperationException(
                $"Collision chunk '{source.ResourceName}' is invalid.",
                exception);
        }

        if (chunk.X != source.X || chunk.Z != source.Z)
        {
            throw new InvalidOperationException(
                $"Collision chunk '{source.ResourceName}' coordinates do not match its manifest entry.");
        }

        if (!CollisionWorld.TryLoadChunk(chunk))
        {
            throw new InvalidOperationException(
                $"Collision chunk '{source.ResourceName}' could not be loaded into the collision world.");
        }

        loadedCoordinates.Add(coordinate);
    }
}
