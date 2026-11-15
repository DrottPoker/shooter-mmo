using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mmorpg.SimulationWorker.Realtime;

// Appends one JSON object per metrics interval to SimulationWorker:PerformanceReportPath, for
// tools/Test-Load.ps1 and the stack stress generator. The file is diagnostics, not a contract
// for anything but the load tooling. A failed write is logged and never stops the worker.
internal sealed class RealtimePerformanceReportWriter
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly string path;
    private readonly ILogger logger;

    private RealtimePerformanceReportWriter(string path, ILogger logger)
    {
        this.path = path;
        this.logger = logger;
    }

    // Null when no path is configured, so an unset setting costs nothing.
    public static RealtimePerformanceReportWriter? Create(string? path, ILogger logger)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : new RealtimePerformanceReportWriter(path, logger);
    }

    public void Append(RealtimeMetricsInterval interval)
    {
        var line = JsonSerializer.Serialize(RealtimePerformanceReportEntry.From(interval), SerializerOptions);
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Readers may hold the file open while the worker appends.
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "[SIMULATION] Could not append the metrics interval to the performance report {PerformanceReportPath}.",
                path);
        }
    }
}

internal sealed record RealtimeDurationEntry(
    long Samples,
    double AverageMs,
    double P95Ms,
    double P99Ms,
    double MaximumMs)
{
    public static RealtimeDurationEntry? From(RealtimeDurationSummary? summary)
    {
        return summary is null
            ? null
            : new RealtimeDurationEntry(
                summary.Samples,
                summary.AverageMilliseconds,
                summary.P95UpperBoundMilliseconds,
                summary.P99UpperBoundMilliseconds,
                summary.MaximumMilliseconds);
    }
}

// One line of the performance file. The p95 and p99 values are the upper bounds of the
// histogram buckets the worker counts in, as in the log line. Traffic, drop, rejection, GC, loop
// failure, and Mob route plan values count this interval only; the other Mob numbers are the
// current gauges, and all of them are null when the worker has no combat metrics.
internal sealed record RealtimePerformanceReportEntry(
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
    RealtimeDurationEntry? Tick,
    RealtimeDurationEntry? TickLag,
    long TickOverruns,
    long TickResynchronizations,
    RealtimeDurationEntry? NetworkPoll,
    RealtimeDurationEntry? CompletedOperations,
    RealtimeDurationEntry? JoinQueueDelay,
    RealtimeDurationEntry? JoinFinalization,
    RealtimeDurationEntry? CollisionStreaming,
    RealtimeDurationEntry? Movement,
    RealtimeDurationEntry? Interest,
    RealtimeDurationEntry? SnapshotBroadcast,
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
    long TotalManagedMemoryBytes,
    long LoopStepFailures,
    long LoopIsolatedFailures)
{
    public long? Mobs { get; init; }

    public long? EngagedMobs { get; init; }

    public long? MovingMobs { get; init; }

    public long? MobPathQueue { get; init; }

    public long? MobPathPlans { get; init; }

    // Every tick phase that did its work in the interval, by its name in tick order, timed only
    // on the ticks on which it did its work.
    public IReadOnlyDictionary<string, RealtimeDurationEntry> Phases { get; init; } =
        new Dictionary<string, RealtimeDurationEntry>();

    // Every step of a loop iteration outside the tick, by its name.
    public IReadOnlyDictionary<string, RealtimeDurationEntry> LoopSteps { get; init; } =
        new Dictionary<string, RealtimeDurationEntry>();

    // The garbage collection pauses of the interval.
    public double GcPauseMs { get; init; }

    // The share of the interval that the loop thread spent in its ticks and loop steps; the rest
    // it waited for the next tick or poll.
    public double LoopBusyPercent { get; init; }

    public static RealtimePerformanceReportEntry From(RealtimeMetricsInterval interval)
    {
        var network = interval.Delta;
        var performance = interval.Performance;
        var mobs = interval.Mobs;
        return new RealtimePerformanceReportEntry(
            interval.StartedAtUtc,
            interval.EndedAtUtc,
            interval.Seconds,
            network.ActiveRealPlayers,
            network.ActiveSyntheticBots,
            network.ActivePeers,
            network.UnauthenticatedPeers,
            network.ActiveEntities,
            interval.MachineCpuPercent,
            interval.SingleCoreCpuPercent,
            interval.WorkingSetBytes,
            RealtimeDurationEntry.From(performance.SimulationTick),
            RealtimeDurationEntry.From(performance.SimulationTickLag),
            performance.TickOverruns,
            performance.TickResynchronizations,
            RealtimeDurationEntry.From(performance.NetworkPoll),
            RealtimeDurationEntry.From(performance.CompletedOperations),
            RealtimeDurationEntry.From(performance.JoinQueueDelay),
            RealtimeDurationEntry.From(performance.JoinFinalization),
            RealtimeDurationEntry.From(performance.CollisionStreaming),
            RealtimeDurationEntry.From(performance.MovementSimulation),
            RealtimeDurationEntry.From(performance.InterestRefresh),
            RealtimeDurationEntry.From(performance.SnapshotBroadcast),
            performance.CompletedOperationBacklog,
            performance.MaximumCompletedOperationBacklog,
            performance.CorpseMutationBacklog,
            performance.MaximumCorpseMutationBacklog,
            performance.ActiveCorpseMutations,
            network.DroppedSnapshotPackets,
            network.BackpressureDroppedSnapshotPackets,
            performance.SnapshotEntityUpdatesDeferred,
            network.SnapshotEntityRecords,
            performance.SnapshotVisibilityGroups,
            performance.SnapshotPacketsEncoded,
            performance.SnapshotPacketsSent,
            network.QuotaRejectedPackets,
            network.ReceivedPackets,
            network.ReceivedBytes,
            network.SentPackets,
            network.SentBytes,
            network.SnapshotBytes,
            network.AcceptedJoins,
            network.RejectedJoins,
            network.SpawnPackets,
            network.DespawnPackets,
            performance.ProcessAllocatedBytes,
            performance.Generation0Collections,
            performance.Generation1Collections,
            performance.Generation2Collections,
            performance.ManagedHeapSizeBytes,
            performance.ManagedHeapFragmentedBytes,
            performance.TotalManagedMemoryBytes,
            performance.LoopStepFailures,
            performance.LoopIsolatedFailures)
        {
            Mobs = mobs?.Mobs,
            EngagedMobs = mobs?.EngagedMobs,
            MovingMobs = mobs?.MovingMobs,
            MobPathQueue = mobs?.MobPathQueue,
            MobPathPlans = mobs?.MobPathPlans,
            Phases = ToEntries(performance.TickPhases),
            LoopSteps = ToEntries(performance.LoopSteps),
            GcPauseMs = performance.GcPauseMilliseconds,
            LoopBusyPercent = interval.LoopBusyPercent
        };
    }

    // In the order the worker timed them, which JSON keeps.
    private static Dictionary<string, RealtimeDurationEntry> ToEntries(
        IReadOnlyList<RealtimeNamedDurationSummary> durations)
    {
        var entries = new Dictionary<string, RealtimeDurationEntry>(durations.Count, StringComparer.Ordinal);
        foreach (var duration in durations)
        {
            entries[duration.Name] = RealtimeDurationEntry.From(duration.Summary)!;
        }

        return entries;
    }
}
