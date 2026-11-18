using System.Globalization;
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

// The messages of one kind in a worker interval and their bytes.
public sealed record StressWorkerMessageCount(long Messages, long Bytes);

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
    long TotalManagedMemoryBytes)
{
    // The worker's Mob numbers: the living Mobs, the ones that chase, attack, or evade, the
    // ones that moved in the last tick, and the routes waiting for the planning budget, as
    // gauges, and the Mob routes planned in the interval. Null in a file of an older worker.
    public long? Mobs { get; init; }

    public long? EngagedMobs { get; init; }

    public long? MovingMobs { get; init; }

    public long? MobPathQueue { get; init; }

    public long? MobPathPlans { get; init; }

    // The loop steps that failed as a whole, and the failures that the loop isolated to one
    // player or peer. Null in a file of an older worker.
    public long? LoopStepFailures { get; init; }

    public long? LoopIsolatedFailures { get; init; }

    // Every tick phase that did its work in the interval and every step of a loop iteration
    // outside the tick, by name; the garbage collection pauses; and the share of the interval
    // that the loop thread was busy. Null in a file of an older worker.
    public IReadOnlyDictionary<string, StressWorkerDuration>? Phases { get; init; }

    public IReadOnlyDictionary<string, StressWorkerDuration>? LoopSteps { get; init; }

    // The parts of the tick phases that time themselves, by "<phase>: <part>"; the longest single
    // garbage collection pause; and the overruns that stay over the tick interval without their
    // pauses. Null in a file of an older worker.
    public IReadOnlyDictionary<string, StressWorkerDuration>? PhaseParts { get; init; }

    public double? GcLongestPauseMs { get; init; }

    public long? TickOverrunsWithoutGcPauses { get; init; }

    public double? GcPauseMs { get; init; }

    public double? LoopBusyPercent { get; init; }

    // The messages that the worker handed to the transport and received in the interval, by
    // their RealtimeMessageType name, or the type byte as text for a packet without a readable
    // type. Null in a file of an older worker.
    public IReadOnlyDictionary<string, StressWorkerMessageCount>? SentByMessage { get; init; }

    public IReadOnlyDictionary<string, StressWorkerMessageCount>? ReceivedByMessage { get; init; }

    // The UDP datagrams of the interval, which carry the messages together with LiteNetLib's own
    // packets, and their bytes; the bytes that the sealing layer added in both directions; and
    // the received datagrams it dropped. Null in a file of an older worker.
    public long? DatagramsIn { get; init; }

    public long? DatagramBytesIn { get; init; }

    public long? DatagramsOut { get; init; }

    public long? DatagramBytesOut { get; init; }

    public long? SealingOverheadBytes { get; init; }

    public long? DroppedDatagrams { get; init; }
}

// One message kind over the steady intervals: its messages and bytes, both per second, and both
// per second and player, by the same population as bytesOutPerBotPerSecond, the average peers;
// null per player without peers.
public sealed record StressWorkerMessageKind(
    string Name,
    long Messages,
    long Bytes,
    double MessagesPerSecond,
    double BytesPerSecond,
    double? MessagesPerPlayerPerSecond,
    double? BytesPerPlayerPerSecond);

// One phase of the worst interval: its samples, average, p99, and longest sample, and how many
// milliseconds of every second it took, which orders the phases by cost.
public sealed record StressWorkerPhaseCost(
    string Name,
    long Samples,
    double AverageMs,
    double P99Ms,
    double MaximumMs,
    double MsPerSecond);

// The steady interval with the highest tick p99, the highest tick average breaking a tie, and its
// three costliest tick phases.
public sealed record StressWorkerWorstInterval(
    DateTime IntervalStart,
    DateTime IntervalEnd,
    double TickP99Ms,
    double TickAverageMs,
    double CpuSingleCorePercent,
    double? LoopBusyPercent,
    double? GcPauseMsPerSecond,
    IReadOnlyList<StressWorkerPhaseCost> SlowestPhases);

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
    string? Note)
{
    // The realtime loop's failures over the run, which the rule worker_loop_failures judges.
    public long RunLoopStepFailures { get; init; }

    public long RunLoopIsolatedFailures { get; init; }

    // Every tick phase and every loop step over the steady intervals, by name; empty for an
    // older worker's file.
    public IReadOnlyDictionary<string, StressWorkerDurationFold> Phases { get; init; } =
        new Dictionary<string, StressWorkerDurationFold>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, StressWorkerDurationFold> LoopSteps { get; init; } =
        new Dictionary<string, StressWorkerDurationFold>(StringComparer.Ordinal);

    // Every part of a tick phase that times itself over the steady intervals, by name; empty for
    // an older worker's file.
    public IReadOnlyDictionary<string, StressWorkerDurationFold> PhaseParts { get; init; } =
        new Dictionary<string, StressWorkerDurationFold>(StringComparer.Ordinal);

    // The loop thread's busy share in percent, the garbage collection pauses in milliseconds per
    // second, and the longest single pause of an interval, over the steady intervals that carry
    // them; null for an older worker's file.
    public StressWorkerLevelFold? LoopBusyPercent { get; init; }

    public StressWorkerLevelFold? GcPauseMsPerSecond { get; init; }

    public StressWorkerLevelFold? GcLongestPauseMs { get; init; }

    public StressWorkerWorstInterval? WorstInterval { get; init; }

    // Every message kind that the worker sent or received in the steady intervals, the most bytes
    // first; empty for an older worker's file. Diagnostics, judged by no rule.
    public IReadOnlyList<StressWorkerMessageKind> SentByMessage { get; init; } = [];

    public IReadOnlyList<StressWorkerMessageKind> ReceivedByMessage { get; init; } = [];
}

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
        ("tickOverrunsWithoutGcPauses", interval => interval.TickOverrunsWithoutGcPauses ?? 0),
        ("tickResynchronizations", interval => interval.TickResynchronizations),
        ("snapshotDrops", interval => interval.SnapshotDrops),
        ("snapshotBackpressureDrops", interval => interval.SnapshotBackpressureDrops),
        ("snapshotEntityUpdatesDeferred", interval => interval.SnapshotEntityUpdatesDeferred),
        ("snapshotEntityRecords", interval => interval.SnapshotEntityRecords),
        ("snapshotRemoteRecipients", interval => interval.SnapshotRemoteRecipients),
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
        ("generation2Collections", interval => interval.Generation2Collections),
        ("mobPathPlans", interval => interval.MobPathPlans ?? 0),
        ("loopStepFailures", interval => interval.LoopStepFailures ?? 0),
        ("loopIsolatedFailures", interval => interval.LoopIsolatedFailures ?? 0),
        ("datagramsIn", interval => interval.DatagramsIn ?? 0),
        ("datagramBytesIn", interval => interval.DatagramBytesIn ?? 0),
        ("datagramsOut", interval => interval.DatagramsOut ?? 0),
        ("datagramBytesOut", interval => interval.DatagramBytesOut ?? 0),
        ("sealingOverheadBytes", interval => interval.SealingOverheadBytes ?? 0),
        ("droppedDatagrams", interval => interval.DroppedDatagrams ?? 0)
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
        ("totalManagedMemoryBytes", interval => interval.TotalManagedMemoryBytes),
        ("mobs", interval => interval.Mobs ?? 0),
        ("engagedMobs", interval => interval.EngagedMobs ?? 0),
        ("movingMobs", interval => interval.MovingMobs ?? 0),
        ("mobPathQueue", interval => interval.MobPathQueue ?? 0)
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
            note)
        {
            RunLoopStepFailures = run.Sum(interval => interval.LoopStepFailures ?? 0),
            RunLoopIsolatedFailures = run.Sum(interval => interval.LoopIsolatedFailures ?? 0),
            Phases = FoldNamed(steady, interval => interval.Phases),
            LoopSteps = FoldNamed(steady, interval => interval.LoopSteps),
            PhaseParts = FoldNamed(steady, interval => interval.PhaseParts),
            LoopBusyPercent = FoldLevel(steady.Select(interval => interval.LoopBusyPercent)),
            GcPauseMsPerSecond = FoldLevel(steady.Select(interval =>
                interval.GcPauseMs is { } pause && interval.IntervalSeconds > 0
                    ? pause / interval.IntervalSeconds
                    : (double?)null)),
            GcLongestPauseMs = FoldLevel(steady.Select(interval => interval.GcLongestPauseMs)),
            WorstInterval = FindWorstInterval(steady),
            SentByMessage = FoldMessages(steady, interval => interval.SentByMessage),
            ReceivedByMessage = FoldMessages(steady, interval => interval.ReceivedByMessage)
        };
    }

    private static StressWorkerMessageKind[] FoldMessages(
        IReadOnlyList<StressWorkerInterval> steady,
        Func<StressWorkerInterval, IReadOnlyDictionary<string, StressWorkerMessageCount>?> select)
    {
        var seconds = steady.Sum(interval => interval.IntervalSeconds);
        if (seconds <= 0)
        {
            return [];
        }

        var players = steady.Average(interval => (double)interval.Peers);
        return steady
            .SelectMany(interval => select(interval) ?? new Dictionary<string, StressWorkerMessageCount>())
            .GroupBy(kind => kind.Key, StringComparer.Ordinal)
            .Select(kind =>
            {
                var messages = kind.Sum(interval => interval.Value.Messages);
                var bytes = kind.Sum(interval => interval.Value.Bytes);
                return new StressWorkerMessageKind(
                    kind.Key,
                    messages,
                    bytes,
                    messages / seconds,
                    bytes / seconds,
                    players > 0 ? messages / seconds / players : null,
                    players > 0 ? bytes / seconds / players : null);
            })
            .OrderByDescending(kind => kind.Bytes)
            .ThenByDescending(kind => kind.Messages)
            .ThenBy(kind => kind.Name, StringComparer.Ordinal)
            .ToArray();
    }

    // Prints the sent and the received messages by kind, each as a table with a total row, for a
    // worker file that carries them.
    public static void WriteMessageTables(TextWriter writer, StressWorkerPerformanceSummary summary)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(summary);
        var players = summary.Levels.GetValueOrDefault("peers")?.Average;
        WriteMessageTable(writer, "Sent messages by kind", summary.SentByMessage, summary.SteadySeconds, players);
        WriteMessageTable(writer, "Received messages by kind", summary.ReceivedByMessage, summary.SteadySeconds, players);
    }

    private static void WriteMessageTable(
        TextWriter writer,
        string title,
        IReadOnlyList<StressWorkerMessageKind> kinds,
        double seconds,
        double? players)
    {
        if (kinds.Count == 0 || seconds <= 0)
        {
            return;
        }

        var messages = kinds.Sum(kind => kind.Messages);
        var bytes = kinds.Sum(kind => kind.Bytes);
        var total = new StressWorkerMessageKind(
            "Total",
            messages,
            bytes,
            messages / seconds,
            bytes / seconds,
            players > 0 ? messages / seconds / players : null,
            players > 0 ? bytes / seconds / players : null);
        string[][] rows =
        [
            ["Kind", "Messages", "Bytes", "Messages/s", "Bytes/s", "Messages/s/player", "Bytes/s/player"],
            .. kinds.Append(total).Select(kind => new[]
            {
                kind.Name,
                kind.Messages.ToString(CultureInfo.InvariantCulture),
                kind.Bytes.ToString(CultureInfo.InvariantCulture),
                kind.MessagesPerSecond.ToString("0.0", CultureInfo.InvariantCulture),
                kind.BytesPerSecond.ToString("0", CultureInfo.InvariantCulture),
                kind.MessagesPerPlayerPerSecond?.ToString("0.00", CultureInfo.InvariantCulture) ?? "n/a",
                kind.BytesPerPlayerPerSecond?.ToString("0.0", CultureInfo.InvariantCulture) ?? "n/a"
            })
        ];
        var widths = Enumerable.Range(0, rows[0].Length)
            .Select(column => rows.Max(row => row[column].Length))
            .ToArray();
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{title} over the {seconds:0} steady seconds of the worker, the most bytes first:"));
        foreach (var row in rows)
        {
            var cells = row.Select((cell, column) => column == 0
                ? cell.PadRight(widths[column])
                : cell.PadLeft(widths[column]));
            writer.WriteLine("  " + string.Join("  ", cells).TrimEnd());
        }
    }

    public const int SlowestPhaseCount = 3;

    // The names keep the order of their first appearance, which is the worker's tick order.
    private static Dictionary<string, StressWorkerDurationFold> FoldNamed(
        IReadOnlyList<StressWorkerInterval> intervals,
        Func<StressWorkerInterval, IReadOnlyDictionary<string, StressWorkerDuration>?> select)
    {
        var names = intervals
            .SelectMany(interval => select(interval)?.Keys ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var folds = new Dictionary<string, StressWorkerDurationFold>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (FoldDuration(intervals.Select(interval => select(interval)?.GetValueOrDefault(name))) is { } fold)
            {
                folds[name] = fold;
            }
        }

        return folds;
    }

    private static StressWorkerLevelFold? FoldLevel(IEnumerable<double?> source)
    {
        var values = source.OfType<double>().ToArray();
        return values.Length == 0 ? null : new StressWorkerLevelFold(values.Average(), values.Max());
    }

    private static StressWorkerWorstInterval? FindWorstInterval(IReadOnlyList<StressWorkerInterval> steady)
    {
        var worst = steady
            .Where(interval => interval.Tick is { Samples: > 0 })
            .OrderByDescending(interval => interval.Tick!.P99Ms)
            .ThenByDescending(interval => interval.Tick!.AverageMs)
            .FirstOrDefault();
        if (worst is null)
        {
            return null;
        }

        var seconds = worst.IntervalSeconds > 0 ? worst.IntervalSeconds : 1d;
        return new StressWorkerWorstInterval(
            worst.IntervalStart,
            worst.IntervalEnd,
            worst.Tick!.P99Ms,
            worst.Tick.AverageMs,
            worst.CpuSingleCorePercent,
            worst.LoopBusyPercent,
            worst.GcPauseMs is { } pause ? pause / seconds : null,
            (worst.Phases ?? new Dictionary<string, StressWorkerDuration>())
                .Select(phase => new StressWorkerPhaseCost(
                    phase.Key,
                    phase.Value.Samples,
                    phase.Value.AverageMs,
                    phase.Value.P99Ms,
                    phase.Value.MaximumMs,
                    phase.Value.AverageMs * phase.Value.Samples / seconds))
                .OrderByDescending(phase => phase.MsPerSecond)
                .Take(SlowestPhaseCount)
                .ToArray());
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
