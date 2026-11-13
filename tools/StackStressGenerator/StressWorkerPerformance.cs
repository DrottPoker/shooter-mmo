using System.Text.Json;

namespace Mmorpg.Tools.StackStressGenerator;

// One line of the worker's performance file, SimulationWorker:PerformanceReportPath. The worker
// writes it in RealtimePerformanceReportWriter; the file is diagnostics for this tool only.
// Durations are milliseconds, and p95 and p99 are histogram bucket upper bounds.
public sealed record StressWorkerDuration(
    long Samples,
    double AverageMs,
    double P95Ms,
    double P99Ms,
    double MaximumMs);

public sealed record StressWorkerInterval(
    DateTime IntervalStart,
    DateTime IntervalEnd,
    double IntervalSeconds,
    long Players,
    long Bots,
    long Peers,
    long UnauthenticatedPeers,
    long Entities,
    double CpuMachinePercent,
    double CpuSingleCorePercent,
    long WorkingSetBytes,
    StressWorkerDuration? Tick,
    StressWorkerDuration? TickLag,
    long TickOverruns,
    long TickResynchronizations,
    StressWorkerDuration? NetworkPoll,
    StressWorkerDuration? CompletedOperations,
    StressWorkerDuration? JoinQueueDelay,
    StressWorkerDuration? JoinFinalization,
    StressWorkerDuration? CollisionStreaming,
    StressWorkerDuration? Movement,
    StressWorkerDuration? Interest,
    StressWorkerDuration? SnapshotBroadcast,
    int CompletedOperationBacklog,
    int MaximumCompletedOperationBacklog,
    int CorpseMutationBacklog,
    int MaximumCorpseMutationBacklog,
    int ActiveCorpseMutations,
    long SnapshotDrops,
    long SnapshotBackpressureDrops,
    long SnapshotEntityUpdatesDeferred,
    long SnapshotEntityRecords,
    long SnapshotVisibilityGroups,
    long SnapshotPacketsEncoded,
    long SnapshotPacketsSent,
    long QuotaRejections,
    long PacketsIn,
    long BytesIn,
    long PacketsOut,
    long BytesOut,
    long SnapshotBytes,
    long JoinsAccepted,
    long JoinsRejected,
    long SpawnPackets,
    long DespawnPackets,
    long AllocatedBytes,
    int Generation0Collections,
    int Generation1Collections,
    int Generation2Collections,
    long ManagedHeapBytes,
    long ManagedHeapFragmentedBytes,
    long TotalManagedMemoryBytes);

// A duration over the folded intervals: the sample-weighted average, the worst interval
// average, p95, and p99, and the longest single sample.
public sealed record StressWorkerDurationFold(
    long Samples,
    double AverageMs,
    double WorstAverageMs,
    double WorstP95Ms,
    double WorstP99Ms,
    double MaximumMs);

// A counter: the total over the folded intervals and the largest single interval.
public sealed record StressWorkerCounterFold(long Total, long WorstInterval);

// A level such as CPU or players: the average over the folded intervals and the highest value.
public sealed record StressWorkerLevelFold(double Average, double Worst);

public sealed record StressWorkerPerformanceSummary(
    int IntervalsRead,
    int SteadyIntervals,
    DateTime? FirstSteadyIntervalStart,
    DateTime? LastSteadyIntervalEnd,
    double SteadySeconds,
    int RunIntervals,
    // Null when the worker intervals cover the whole run without a gap; else what is missing.
    string? RunCoverageGap,
    // Null when the steady worker intervals follow each other without a gap; else what is missing.
    string? SteadyCoverageGap,
    long RunSnapshotDrops,
    long RunQuotaRejections,
    IReadOnlyDictionary<string, StressWorkerDurationFold> Durations,
    IReadOnlyDictionary<string, StressWorkerCounterFold> Counters,
    IReadOnlyDictionary<string, StressWorkerLevelFold> Levels,
    string? Note);

public static class StressWorkerPerformance
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly (string Name, Func<StressWorkerInterval, StressWorkerDuration?> Value)[] DurationFields =
    [
        ("tick", interval => interval.Tick),
        ("tickLag", interval => interval.TickLag),
        ("networkPoll", interval => interval.NetworkPoll),
        ("completedOperations", interval => interval.CompletedOperations),
        ("joinQueueDelay", interval => interval.JoinQueueDelay),
        ("joinFinalization", interval => interval.JoinFinalization),
        ("collisionStreaming", interval => interval.CollisionStreaming),
        ("movement", interval => interval.Movement),
        ("interest", interval => interval.Interest),
        ("snapshotBroadcast", interval => interval.SnapshotBroadcast)
    ];

    private static readonly (string Name, Func<StressWorkerInterval, long> Value)[] CounterFields =
    [
        ("tickOverruns", interval => interval.TickOverruns),
        ("tickResynchronizations", interval => interval.TickResynchronizations),
        ("snapshotDrops", interval => interval.SnapshotDrops),
        ("snapshotBackpressureDrops", interval => interval.SnapshotBackpressureDrops),
        ("snapshotEntityUpdatesDeferred", interval => interval.SnapshotEntityUpdatesDeferred),
        ("snapshotEntityRecords", interval => interval.SnapshotEntityRecords),
        ("snapshotVisibilityGroups", interval => interval.SnapshotVisibilityGroups),
        ("snapshotPacketsEncoded", interval => interval.SnapshotPacketsEncoded),
        ("snapshotPacketsSent", interval => interval.SnapshotPacketsSent),
        ("quotaRejections", interval => interval.QuotaRejections),
        ("packetsIn", interval => interval.PacketsIn),
        ("bytesIn", interval => interval.BytesIn),
        ("packetsOut", interval => interval.PacketsOut),
        ("bytesOut", interval => interval.BytesOut),
        ("snapshotBytes", interval => interval.SnapshotBytes),
        ("joinsAccepted", interval => interval.JoinsAccepted),
        ("joinsRejected", interval => interval.JoinsRejected),
        ("spawnPackets", interval => interval.SpawnPackets),
        ("despawnPackets", interval => interval.DespawnPackets),
        ("allocatedBytes", interval => interval.AllocatedBytes),
        ("generation0Collections", interval => interval.Generation0Collections),
        ("generation1Collections", interval => interval.Generation1Collections),
        ("generation2Collections", interval => interval.Generation2Collections)
    ];

    private static readonly (string Name, Func<StressWorkerInterval, double> Value)[] LevelFields =
    [
        ("players", interval => interval.Players),
        ("bots", interval => interval.Bots),
        ("peers", interval => interval.Peers),
        ("entities", interval => interval.Entities),
        ("cpuSingleCorePercent", interval => interval.CpuSingleCorePercent),
        ("cpuMachinePercent", interval => interval.CpuMachinePercent),
        ("workingSetBytes", interval => interval.WorkingSetBytes),
        ("completedOperationBacklog", interval => interval.MaximumCompletedOperationBacklog),
        ("corpseMutationBacklog", interval => interval.MaximumCorpseMutationBacklog),
        ("managedHeapBytes", interval => interval.ManagedHeapBytes),
        ("totalManagedMemoryBytes", interval => interval.TotalManagedMemoryBytes)
    ];

    public static IReadOnlyList<StressWorkerInterval> Parse(IEnumerable<string> lines)
    {
        var intervals = new List<StressWorkerInterval>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                intervals.Add(JsonSerializer.Deserialize<StressWorkerInterval>(line, SerializerOptions)
                    ?? throw new InvalidDataException("An empty worker performance line."));
            }
            catch (JsonException)
            {
                // The worker may be writing the last line while it is read.
                continue;
            }
        }

        return intervals;
    }

    // Reads the file once an interval that ends after runEndedAt is in it, so the error counters
    // cover the leave too, or after the longest wait for it.
    public static async Task<IReadOnlyList<StressWorkerInterval>> ReadAfterRunAsync(
        string path,
        DateTime runEndedAt,
        TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + maximumWait;
        while (true)
        {
            var intervals = File.Exists(path) ? Parse(ReadShared(path)) : [];
            if ((intervals.Count > 0 && intervals[^1].IntervalEnd >= runEndedAt)
                || DateTime.UtcNow >= deadline)
            {
                return intervals;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
    }

    public static StressWorkerPerformanceSummary Fold(
        IReadOnlyList<StressWorkerInterval> intervals,
        DateTime runStartedAt,
        DateTime runEndedAt,
        DateTime? steadyStartedAt,
        DateTime? steadyEndedAt)
    {
        // A steady interval lies entirely inside the steady window; a run interval overlaps the run.
        var steady = steadyStartedAt is { } steadyStart && steadyEndedAt is { } steadyEnd
            ? intervals
                .Where(interval => interval.IntervalStart >= steadyStart && interval.IntervalEnd <= steadyEnd)
                .ToArray()
            : [];
        var run = intervals
            .Where(interval => interval.IntervalEnd > runStartedAt && interval.IntervalStart < runEndedAt)
            .ToArray();
        var runCoverageGap = run.Length == 0
            ? "No worker interval overlaps the run."
            : run[0].IntervalStart > runStartedAt + CoverageTolerance
                ? $"The worker intervals cover the run only from {run[0].IntervalStart:O}."
                : run[^1].IntervalEnd < runEndedAt
                    ? $"The worker intervals cover the run only until {run[^1].IntervalEnd:O}."
                    : FindGap(run);
        var steadyCoverageGap = steady.Length == 0 ? null : FindGap(steady);
        string? note = null;
        if (intervals.Count == 0)
        {
            note = "The worker performance file has no intervals.";
        }
        else if (steady.Length == 0)
        {
            note = "No worker interval lies entirely inside the steady window; lengthen the run or shorten NetworkMetricsLogSeconds.";
        }
        else if (steadyCoverageGap is not null || runCoverageGap is not null)
        {
            note = steadyCoverageGap ?? runCoverageGap;
        }

        return new StressWorkerPerformanceSummary(
            intervals.Count,
            steady.Length,
            steady.Length == 0 ? null : steady[0].IntervalStart,
            steady.Length == 0 ? null : steady[^1].IntervalEnd,
            steady.Sum(interval => interval.IntervalSeconds),
            run.Length,
            runCoverageGap,
            steadyCoverageGap,
            run.Sum(interval => interval.SnapshotDrops),
            run.Sum(interval => interval.QuotaRejections),
            DurationFields
                .Select(field => (field.Name, Fold: FoldDuration(steady.Select(field.Value))))
                .Where(pair => pair.Fold is not null)
                .ToDictionary(pair => pair.Name, pair => pair.Fold!, StringComparer.Ordinal),
            steady.Length == 0
                ? new Dictionary<string, StressWorkerCounterFold>(StringComparer.Ordinal)
                : CounterFields.ToDictionary(
                    field => field.Name,
                    field => new StressWorkerCounterFold(
                        steady.Sum(field.Value),
                        steady.Max(field.Value)),
                    StringComparer.Ordinal),
            steady.Length == 0
                ? new Dictionary<string, StressWorkerLevelFold>(StringComparer.Ordinal)
                : LevelFields.ToDictionary(
                    field => field.Name,
                    field => new StressWorkerLevelFold(
                        steady.Average(field.Value),
                        steady.Max(field.Value)),
                    StringComparer.Ordinal),
            note);
    }

    // The worker writes one interval right after the other, so a later start than the previous
    // end means a line is missing, for example after a failed write.
    private static readonly TimeSpan CoverageTolerance = TimeSpan.FromSeconds(1);

    private static string? FindGap(IReadOnlyList<StressWorkerInterval> ordered)
    {
        for (var index = 1; index < ordered.Count; index++)
        {
            if (ordered[index].IntervalStart - ordered[index - 1].IntervalEnd > CoverageTolerance)
            {
                return $"The worker intervals have a gap from {ordered[index - 1].IntervalEnd:O} to {ordered[index].IntervalStart:O}.";
            }
        }

        return null;
    }

    private static StressWorkerDurationFold? FoldDuration(IEnumerable<StressWorkerDuration?> source)
    {
        var values = source.OfType<StressWorkerDuration>().Where(value => value.Samples > 0).ToArray();
        if (values.Length == 0)
        {
            return null;
        }

        var samples = values.Sum(value => value.Samples);
        return new StressWorkerDurationFold(
            samples,
            values.Sum(value => value.AverageMs * value.Samples) / samples,
            values.Max(value => value.AverageMs),
            values.Max(value => value.P95Ms),
            values.Max(value => value.P99Ms),
            values.Max(value => value.MaximumMs));
    }

    private static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }
}
