namespace ShooterMmo.Tools.StackStressGenerator;

public sealed record StressHttpOperationSummary(
    long Requests,
    long Successes,
    long Failures,
    StressLatencySummary? Latency,
    IReadOnlyDictionary<int, long> StatusCodes,
    IReadOnlyDictionary<string, long> FailureCodes);

public sealed record StressFullStackSummary(
    string RunId,
    int RegisteredBots,
    int FixtureAccounts,
    int InventoryFixtureBots,
    int ProvisionedBots,
    int LoggedInBots,
    int AdmittedBots,
    int LoggedOutBots,
    Guid? LootHotspotCorpseId,
    IReadOnlyDictionary<string, StressHttpOperationSummary> Operations);

public sealed class FullStackStressMetrics(int seed)
{
    private readonly object gate = new();
    private readonly Dictionary<string, OperationMetrics> operations =
        new(StringComparer.Ordinal);

    public void Record(
        string operation,
        double elapsedMilliseconds,
        int statusCode,
        string? failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        lock (gate)
        {
            if (!operations.TryGetValue(operation, out var metrics))
            {
                metrics = new OperationMetrics(
                    new StressLatencyAccumulator(unchecked(seed + operations.Count + 100)));
                operations.Add(operation, metrics);
            }

            metrics.Latency.Record(elapsedMilliseconds);
            metrics.Requests++;
            if (statusCode is >= 200 and <= 299 && failureCode is null)
            {
                metrics.Successes++;
            }
            else
            {
                metrics.Failures++;
            }

            metrics.StatusCodes[statusCode] =
                metrics.StatusCodes.GetValueOrDefault(statusCode) + 1;
            if (!string.IsNullOrWhiteSpace(failureCode))
            {
                metrics.FailureCodes[failureCode] =
                    metrics.FailureCodes.GetValueOrDefault(failureCode) + 1;
            }
        }
    }

    public IReadOnlyDictionary<string, StressHttpOperationSummary> Capture()
    {
        lock (gate)
        {
            return operations
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(
                    pair => pair.Key,
                    pair => new StressHttpOperationSummary(
                        pair.Value.Requests,
                        pair.Value.Successes,
                        pair.Value.Failures,
                        pair.Value.Latency.CaptureSummary(),
                        new SortedDictionary<int, long>(pair.Value.StatusCodes),
                        new SortedDictionary<string, long>(
                            pair.Value.FailureCodes,
                            StringComparer.Ordinal)),
                    StringComparer.Ordinal);
        }
    }

    private sealed record OperationMetrics(StressLatencyAccumulator Latency)
    {
        public long Requests { get; set; }

        public long Successes { get; set; }

        public long Failures { get; set; }

        public Dictionary<int, long> StatusCodes { get; } = [];

        public Dictionary<string, long> FailureCodes { get; } =
            new(StringComparer.Ordinal);
    }
}
