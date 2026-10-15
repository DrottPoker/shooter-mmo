using System.Collections.Concurrent;

namespace Mmorpg.SimulationWorker.Realtime;

// The only channel from off-loop operations back to the realtime loop. Any thread may
// post; only the loop dequeues, in one FIFO order across every peer and feature.
// Each completion names the handler that finalizes it, so no type registry is needed.
internal sealed class RealtimeCompletionQueue
{
    private readonly ConcurrentQueue<RealtimeCompletion> completedOperations = new();

    public int Count => completedOperations.Count;

    public void Post(IRealtimeCompletionHandler handler, RealtimeOperationResult operation)
    {
        completedOperations.Enqueue(new RealtimeCompletion(operation, handler));
    }

    public bool TryDequeue(out RealtimeCompletion completion)
    {
        return completedOperations.TryDequeue(out completion);
    }
}

internal readonly record struct RealtimeCompletion(
    RealtimeOperationResult Operation,
    IRealtimeCompletionHandler Handler);
