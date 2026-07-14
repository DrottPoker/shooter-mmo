namespace SimulationWorker.Sessions;

public static class BoundedAsync
{
    public static Task ForEachAsync<T>(
        IEnumerable<T> items,
        int maximumConcurrency,
        Func<T, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(operation);
        if (maximumConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        }

        return Parallel.ForEachAsync(
            items,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maximumConcurrency,
                CancellationToken = cancellationToken
            },
            async (item, token) => await operation(item, token));
    }
}
