using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Mmorpg.GameSimulation;

namespace Mmorpg.SimulationWorker.WorldCollision;

// One chunk the loader finished: its decoded chunk, or why it could not be loaded.
public readonly record struct WorldCollisionChunkLoad(
    WorldCollisionChunkSource Source,
    CollisionChunk? Chunk,
    ExceptionDispatchInfo? Failure);

// Reads a chunk file, checks its hash, and decodes it. Runs on the loader's task, or before the
// loop starts.
public static class WorldCollisionChunkReader
{
    public static CollisionChunk Read(WorldCollisionChunkSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
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

        return chunk;
    }
}

// The one piece of work beside the realtime loop that prepares simulation data: it reads,
// hashes, and decodes the requested chunks on one background task, in request order, and hands
// each finished, immutable chunk to the loop through a queue that only the loop drains. It never
// touches loop state. The task starts with the first request.
public sealed class WorldCollisionChunkLoader : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<WorldCollisionChunkSource, CollisionChunk> read;
    private readonly Channel<WorldCollisionChunkSource> requests =
        Channel.CreateUnbounded<WorldCollisionChunkSource>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<WorldCollisionChunkLoad> finished = new();
    private readonly object startGate = new();
    private Task? worker;

    public WorldCollisionChunkLoader(Func<WorldCollisionChunkSource, CollisionChunk> read)
    {
        this.read = read ?? throw new ArgumentNullException(nameof(read));
    }

    public void Request(WorldCollisionChunkSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureStarted();
        if (!requests.Writer.TryWrite(source))
        {
            throw new ObjectDisposedException(nameof(WorldCollisionChunkLoader));
        }
    }

    // Loop-side: the next finished chunk, if any.
    public bool TryTake(out WorldCollisionChunkLoad load)
    {
        return finished.TryDequeue(out load);
    }

    public void Dispose()
    {
        requests.Writer.TryComplete();
        Task? running;
        lock (startGate)
        {
            running = worker;
        }

        // The current read finishes; the requests after it are dropped with the loop.
        running?.Wait(StopTimeout);
    }

    private void EnsureStarted()
    {
        if (Volatile.Read(ref worker) is not null)
        {
            return;
        }

        lock (startGate)
        {
            worker ??= Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        var reader = requests.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var source))
            {
                finished.Enqueue(Load(source));
            }
        }
    }

    private WorldCollisionChunkLoad Load(WorldCollisionChunkSource source)
    {
        try
        {
            return new WorldCollisionChunkLoad(source, read(source), null);
        }
        catch (Exception exception)
        {
            return new WorldCollisionChunkLoad(source, null, ExceptionDispatchInfo.Capture(exception));
        }
    }
}
