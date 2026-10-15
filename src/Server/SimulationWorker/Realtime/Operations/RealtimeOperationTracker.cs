namespace Mmorpg.SimulationWorker.Realtime;

// Tracks in-flight authority operations so shutdown can wait for them. The loop adds
// operations and any thread may complete them, so the set is guarded by a lock.
internal sealed class RealtimeOperationTracker
{
    private readonly HashSet<Task> activeOperations = [];
    private readonly object activeOperationsLock = new();

    public void Track(Task operation)
    {
        lock (activeOperationsLock)
        {
            activeOperations.Add(operation);
        }

        _ = operation.ContinueWith(
            completedOperation =>
            {
                lock (activeOperationsLock)
                {
                    activeOperations.Remove(completedOperation);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task WaitForAllAsync()
    {
        Task[] pendingOperations;
        lock (activeOperationsLock)
        {
            pendingOperations = activeOperations.ToArray();
        }

        if (pendingOperations.Length > 0)
        {
            await Task.WhenAll(pendingOperations);
        }
    }
}
