using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Mmorpg.SimulationWorker.Realtime;

// The only channel from off-loop operations back to the realtime loop. Any thread may
// post; only the loop dequeues, in one FIFO order across every peer and feature.
internal sealed class RealtimeCompletionQueue
{
    private readonly ConcurrentQueue<RealtimeOperationResult> completedOperations = new();

    public int Count => completedOperations.Count;

    public void Post(RealtimeOperationResult operation)
    {
        completedOperations.Enqueue(operation);
    }

    public bool TryDequeue([NotNullWhen(true)] out RealtimeOperationResult? operation)
    {
        return completedOperations.TryDequeue(out operation);
    }
}
