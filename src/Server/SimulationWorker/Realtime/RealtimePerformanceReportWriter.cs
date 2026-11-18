using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    // For a duration watched for garbage collection pauses, the tick and every tick phase: the
    // samples a pause fell into, the pause time inside them, and the p99 bucket and the longest
    // sample once each sample's pauses are taken out.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? GcPausedSamples { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? GcPauseMs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? P99WithoutGcPausesMs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaximumWithoutGcPausesMs { get; init; }

    public static RealtimeDurationEntry? From(RealtimeDurationSummary? summary)
    {
        return summary is null
            ? null
            : new RealtimeDurationEntry(
                summary.Samples,
                summary.AverageMilliseconds,
                summary.P95UpperBoundMilliseconds,
                summary.P99UpperBoundMilliseconds,
                summary.MaximumMilliseconds)
            {
                GcPausedSamples = summary.GcPauses?.PausedSamples,
                GcPauseMs = summary.GcPauses?.PauseMilliseconds,
                P99WithoutGcPausesMs = summary.GcPauses?.P99UpperBoundWithoutPausesMilliseconds,
                MaximumWithoutGcPausesMs = summary.GcPauses?.MaximumWithoutPausesMilliseconds
            };
    }
}

// The slowest tick of an interval: its duration, the garbage collection pause inside it, the
// spawn and despawn packets it sent, and the milliseconds of every tick phase and phase part that
// ran in it, by name.
internal sealed record RealtimeSlowestTickEntry(
    double Ms,
    double GcPauseMs,
    long SpawnPackets,
    long DespawnPackets,
    IReadOnlyDictionary<string, double> Phases,
    IReadOnlyDictionary<string, double> PhaseParts)
{
    // The loop thread's CPU time inside the tick; Ms minus it is the time the thread did not
    // run. Left out without a CPU clock.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CpuMs { get; init; }

    public static RealtimeSlowestTickEntry? From(RealtimeSlowestTick? tick)
    {
        return tick is null
            ? null
            : new RealtimeSlowestTickEntry(
                tick.Milliseconds,
                tick.GcPauseMilliseconds,
                tick.SpawnPackets,
                tick.DespawnPackets,
                ToEntries(tick.Phases),
                ToEntries(tick.PhaseParts))
            {
                CpuMs = tick.CpuMilliseconds
            };
    }

    // In the order the worker timed them, which JSON keeps.
    private static Dictionary<string, double> ToEntries(IReadOnlyList<RealtimeNamedMilliseconds> timings)
    {
        var entries = new Dictionary<string, double>(timings.Count, StringComparer.Ordinal);
        foreach (var timing in timings)
        {
            entries[timing.Name] = timing.Milliseconds;
        }

        return entries;
    }
}

// The messages of one kind in an interval and their bytes.
internal sealed record RealtimeMessageCountEntry(long Messages, long Bytes);

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
    long SnapshotRemoteRecipients,
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

    // The parts of tick phases that time themselves, by "<phase>: <part>", in the order the
    // worker timed them first.
    public IReadOnlyDictionary<string, RealtimeDurationEntry> PhaseParts { get; init; } =
        new Dictionary<string, RealtimeDurationEntry>();

    // The slowest tick of the interval with its phases and parts; null without a tick.
    public RealtimeSlowestTickEntry? SlowestTick { get; init; }

    // The garbage collection pauses of the interval.
    public double GcPauseMs { get; init; }

    // The pauses one by one, from the runtime's GC events: their number, by the generation of the
    // collection that paused, and the longest; null when the worker does not watch them.
    public long? GcPauses { get; init; }

    public long? GcGeneration0Pauses { get; init; }

    public long? GcGeneration1Pauses { get; init; }

    public long? GcGeneration2Pauses { get; init; }

    public double? GcLongestPauseMs { get; init; }

    // Tick overruns that stay longer than the tick interval without their GC pauses.
    public long TickOverrunsWithoutGcPauses { get; init; }

    // The active durable and live corpses at the end of the interval; null without corpse
    // metrics.
    public long? DurableCorpses { get; init; }

    public long? LiveCorpses { get; init; }

    // The steps that players and Mobs skipped in the interval because their collision chunks
    // still loaded.
    public long CollisionPendingSteps { get; init; }

    // The share of the interval that the loop thread spent in its ticks and loop steps; the rest
    // it waited for the next tick or poll.
    public double LoopBusyPercent { get; init; }

    // The loop thread's CPU time summed over the interval's ticks, and per tick the time it did
    // not run, the tick's wall time minus its CPU time, such as when other work preempted it.
    // Null without a CPU clock.
    public double? TickCpuMs { get; init; }

    public RealtimeDurationEntry? TickNotRunning { get; init; }

    // The messages handed to the transport and the messages received in the interval, by their
    // RealtimeMessageType name, only kinds with a message, in ascending type byte order.
    public IReadOnlyDictionary<string, RealtimeMessageCountEntry> SentByMessage { get; init; } =
        new Dictionary<string, RealtimeMessageCountEntry>();

    public IReadOnlyDictionary<string, RealtimeMessageCountEntry> ReceivedByMessage { get; init; } =
        new Dictionary<string, RealtimeMessageCountEntry>();

    // The UDP datagrams of the interval, which carry the messages above together with
    // LiteNetLib's own packets, and their bytes, the sealing layer's included; the bytes that the
    // sealing layer added in both directions; and the received datagrams it dropped.
    public long DatagramsIn { get; init; }

    public long DatagramBytesIn { get; init; }

    public long DatagramsOut { get; init; }

    public long DatagramBytesOut { get; init; }

    public long SealingOverheadBytes { get; init; }

    public long DroppedDatagrams { get; init; }

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
            performance.SnapshotRemoteRecipients,
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
            PhaseParts = ToEntries(performance.TickPhaseParts),
            SlowestTick = RealtimeSlowestTickEntry.From(performance.SlowestTick),
            GcPauseMs = performance.GcPauseMilliseconds,
            GcPauses = performance.GcPauses?.Pauses,
            GcGeneration0Pauses = performance.GcPauses?.Generation0Pauses,
            GcGeneration1Pauses = performance.GcPauses?.Generation1Pauses,
            GcGeneration2Pauses = performance.GcPauses?.Generation2Pauses,
            GcLongestPauseMs = performance.GcPauses?.LongestPauseMilliseconds,
            TickOverrunsWithoutGcPauses = performance.TickOverrunsWithoutGcPauses,
            DurableCorpses = interval.Corpses?.DurableCorpses,
            LiveCorpses = interval.Corpses?.LiveCorpses,
            CollisionPendingSteps = performance.CollisionPendingSteps,
            LoopBusyPercent = interval.LoopBusyPercent,
            TickCpuMs = performance.SimulationTickCpu is { } cpu ? cpu.AverageMilliseconds * cpu.Samples : null,
            TickNotRunning = RealtimeDurationEntry.From(performance.SimulationTickNotRunning),
            SentByMessage = ToEntries(network.SentByMessage),
            ReceivedByMessage = ToEntries(network.ReceivedByMessage),
            DatagramsIn = network.DatagramsReceived,
            DatagramBytesIn = network.DatagramBytesReceived,
            DatagramsOut = network.DatagramsSent,
            DatagramBytesOut = network.DatagramBytesSent,
            SealingOverheadBytes = network.SealingOverheadBytes,
            DroppedDatagrams = network.DroppedDatagrams
        };
    }

    // In the order of the list, ascending by type byte, which JSON keeps.
    private static Dictionary<string, RealtimeMessageCountEntry> ToEntries(
        IReadOnlyList<RealtimeMessageKindTraffic> kinds)
    {
        var entries = new Dictionary<string, RealtimeMessageCountEntry>(kinds.Count, StringComparer.Ordinal);
        foreach (var kind in kinds)
        {
            entries[kind.Name] = new RealtimeMessageCountEntry(kind.Messages, kind.Bytes);
        }

        return entries;
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
