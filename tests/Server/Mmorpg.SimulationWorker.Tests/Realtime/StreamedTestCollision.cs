using System.Collections.Concurrent;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.WorldCollision;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The ground of the test World baked into 4 m chunk files and streamed by a real store, whose
// chunk reads a test can hold back and observe. The chunks around the spawn load before the loop
// starts, as the worker loads them. With the default radius of 2, the place (12, 0, 12) lies
// three chunks beyond what the spawn keeps loaded.
internal sealed class StreamedTestCollision : IDisposable
{
    public const float ChunkSize = 4f;

    private readonly string root = Path.Combine(Path.GetTempPath(), "shooter-mmo-streamed-" + Guid.NewGuid().ToString("N"));
    private readonly ManualResetEventSlim readsAllowed = new(true);
    private readonly ConcurrentQueue<ChunkRead> reads = new();

    public StreamedTestCollision(SimulationVector3 spawn)
    {
        var bake = CollisionWorldCompiler.Compile(new CollisionWorldAuthoringDocument
        {
            FormatVersion = CollisionDataFormat.Version,
            WorldId = RealtimeServerHarness.WorldId,
            ChunkSize = ChunkSize,
            Boxes =
            [
                new CollisionBoxAuthoringEntry
                {
                    SourcePath = "Test/Ground#BoxCollider0",
                    LayerMask = CollisionLayers.CharacterMovement,
                    CenterY = -0.25f,
                    HalfExtentX = 15f,
                    HalfExtentY = 0.25f,
                    HalfExtentZ = 15f,
                    RotationW = 1f
                }
            ]
        });
        Directory.CreateDirectory(root);
        var sources = new List<WorldCollisionChunkSource>();
        foreach (var chunk in bake.Chunks)
        {
            var entry = chunk.ManifestEntry;
            var path = Path.Combine(root, entry.ResourceName + CollisionDataFormat.ChunkFileExtension);
            File.WriteAllBytes(path, chunk.Data);
            sources.Add(new WorldCollisionChunkSource(entry.X, entry.Z, entry.ResourceName, path, entry.Sha256));
        }

        Store = new WorldCollisionStreamingStore(
            bake.Manifest.WorldId,
            bake.Manifest.Revision,
            ChunkSize,
            sources,
            CollisionStreamingConfig.Default,
            Read);
        Store.LoadBeforeStart([spawn]);
    }

    // A place on the ground whose chunks the spawn does not keep loaded.
    public static SimulationVector3 FarPlace => new(12f, 0f, 12f);

    public WorldCollisionStreamingStore Store { get; }

    // Every chunk read with the name of the thread that read it.
    public IReadOnlyCollection<ChunkRead> Reads => reads;

    // Holds every chunk read until ReleaseReads.
    public void HoldReads() => readsAllowed.Reset();

    public void ReleaseReads() => readsAllowed.Set();

    public void Dispose()
    {
        readsAllowed.Set();
        Store.Dispose();
        readsAllowed.Dispose();
        Directory.Delete(root, true);
    }

    private CollisionChunk Read(WorldCollisionChunkSource source)
    {
        if (!readsAllowed.Wait(RealtimeServerHarness.PollTimeout))
        {
            throw new TimeoutException("The test held a chunk read too long.");
        }

        reads.Enqueue(new ChunkRead(source.X, source.Z, Environment.CurrentManagedThreadId, Thread.CurrentThread.Name));
        return WorldCollisionChunkReader.Read(source);
    }
}

internal sealed record ChunkRead(int X, int Z, int ThreadId, string? ThreadName);
