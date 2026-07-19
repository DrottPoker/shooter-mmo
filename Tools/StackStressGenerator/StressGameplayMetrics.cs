namespace ShooterMmo.Tools.StackStressGenerator;

public sealed record StressGameplayOperationSummary(
    long Requests,
    long Successes,
    long ExpectedRejections,
    long UnexpectedRejections,
    long Timeouts,
    StressLatencySummary? Latency,
    IReadOnlyDictionary<string, long> ResultCodes)
{
    public long IncompleteRequests => Math.Max(
        0,
        Requests - Successes - ExpectedRejections - UnexpectedRejections - Timeouts);
}

public sealed record StressGameplaySummary(
    string Workload,
    Guid? LootHotspotCorpseId,
    long InventoryRefreshFailures,
    IReadOnlyDictionary<string, StressGameplayOperationSummary> Operations)
{
    public long UnexpectedFailures => InventoryRefreshFailures
        + Operations.Values.Sum(operation =>
            operation.UnexpectedRejections
            + operation.Timeouts
            + operation.IncompleteRequests);
}

public sealed class StressGameplayMetrics(int seed)
{
    private readonly object gate = new();
    private readonly Dictionary<string, OperationMetrics> operations =
        new(StringComparer.Ordinal);
    private long inventoryRefreshFailures;

    public void RecordRequest(string operation)
    {
        lock (gate)
        {
            GetOrCreate(operation).Requests++;
        }
    }

    public void RecordResult(
        string operation,
        double elapsedMilliseconds,
        bool succeeded,
        string? resultCode,
        bool expectedRejection)
    {
        lock (gate)
        {
            var metrics = GetOrCreate(operation);
            metrics.Latency.Record(elapsedMilliseconds);
            if (succeeded)
            {
                metrics.Successes++;
                metrics.ResultCodes["success"] = metrics.ResultCodes.GetValueOrDefault("success") + 1;
            }
            else if (expectedRejection)
            {
                metrics.ExpectedRejections++;
            }
            else
            {
                metrics.UnexpectedRejections++;
            }

            if (!succeeded)
            {
                var code = string.IsNullOrWhiteSpace(resultCode)
                    ? "unknown_rejection"
                    : resultCode;
                metrics.ResultCodes[code] = metrics.ResultCodes.GetValueOrDefault(code) + 1;
            }
        }
    }

    public void RecordTimeout(string operation)
    {
        lock (gate)
        {
            var metrics = GetOrCreate(operation);
            metrics.Timeouts++;
            metrics.ResultCodes["operation_timeout"] =
                metrics.ResultCodes.GetValueOrDefault("operation_timeout") + 1;
        }
    }

    public void RecordInventoryRefreshFailure()
    {
        Interlocked.Increment(ref inventoryRefreshFailures);
    }

    public StressGameplaySummary Capture(
        StressWorkloadProfile workload,
        Guid? lootHotspotCorpseId)
    {
        lock (gate)
        {
            return new StressGameplaySummary(
                StressGeneratorOptions.FormatWorkload(workload),
                lootHotspotCorpseId,
                Volatile.Read(ref inventoryRefreshFailures),
                operations
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        pair => pair.Key,
                        pair => new StressGameplayOperationSummary(
                            pair.Value.Requests,
                            pair.Value.Successes,
                            pair.Value.ExpectedRejections,
                            pair.Value.UnexpectedRejections,
                            pair.Value.Timeouts,
                            pair.Value.Latency.CaptureSummary(),
                            new SortedDictionary<string, long>(
                                pair.Value.ResultCodes,
                                StringComparer.Ordinal)),
                        StringComparer.Ordinal));
        }
    }

    private OperationMetrics GetOrCreate(string operation)
    {
        if (!operations.TryGetValue(operation, out var metrics))
        {
            metrics = new OperationMetrics(
                new StressLatencyAccumulator(unchecked(seed + operations.Count + 500)));
            operations.Add(operation, metrics);
        }

        return metrics;
    }

    private sealed record OperationMetrics(StressLatencyAccumulator Latency)
    {
        public long Requests { get; set; }

        public long Successes { get; set; }

        public long ExpectedRejections { get; set; }

        public long UnexpectedRejections { get; set; }

        public long Timeouts { get; set; }

        public Dictionary<string, long> ResultCodes { get; } =
            new(StringComparer.Ordinal);
    }
}
