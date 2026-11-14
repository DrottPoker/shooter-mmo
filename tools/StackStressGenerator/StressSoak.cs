using System.Diagnostics;

namespace Mmorpg.Tools.StackStressGenerator;

// One drift check of a report interval: what can slowly go wrong while the population stays the
// same. The active sessions are compared with the bots: the bots that certainly hold a session,
// joined and playing, and the bots that may hold one, which adds the bots on their way out of or
// back into the game, whose body may still stand or whose new session may already exist. The
// lease and the worker's registration are compared with their limits.
public sealed record StressDriftSample(
    DateTime TakenAt,
    int BotsHoldingSession,
    int BotsThatMayHoldSession,
    int ActiveSessions,
    int SessionsNearLeaseExpiry,
    double? MinimumSessionLeaseHeadroomSeconds,
    double? WorkerHeartbeatAgeSeconds,
    int PendingItemOperations,
    int ServiceConnections,
    int ServiceConnectionsInUse,
    double OldestIdleInTransactionSeconds);

// The worst value of every drift check over the steady interval: the most active sessions above
// the bots that may hold one, which no bot explains, and the most bots that certainly hold a
// session without one, which a lost session explains. A bot that churns counts on neither side,
// so both stay 0 in a healthy run.
public sealed record StressDriftSummary(
    int Samples,
    int MaximumSessionsWithoutBot,
    int MaximumBotsWithoutSession,
    int MaximumSessionsNearLeaseExpiry,
    double LeaseExpiryThresholdSeconds,
    double? MinimumSessionLeaseHeadroomSeconds,
    double? MaximumWorkerHeartbeatAgeSeconds,
    double? WorkerHeartbeatTimeoutSeconds,
    int MaximumPendingItemOperations,
    int MaximumServiceConnections,
    int MaximumServiceConnectionsInUse,
    int? DatabasePoolSize,
    double MaximumIdleInTransactionSeconds)
{
    // The least time the worker's registration had left before GameService takes it offline.
    public double? MinimumWorkerRegistrationHeadroomSeconds =>
        WorkerHeartbeatTimeoutSeconds is { } timeout && MaximumWorkerHeartbeatAgeSeconds is { } age ? timeout - age : null;
}

// Folds the drift samples of the steady interval into their worst values without keeping them.
public sealed class StressDriftAccumulator(double leaseExpiryThresholdSeconds, double? workerHeartbeatTimeoutSeconds, int? databasePoolSize)
{
    private int samples;
    private int sessionsWithoutBot;
    private int botsWithoutSession;
    private int nearExpiry;
    private double? leaseHeadroom;
    private double? heartbeatAge;
    private int pending;
    private int connections;
    private int connectionsInUse;
    private double idleInTransaction;

    public void Add(StressDriftSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        samples++;
        sessionsWithoutBot = Math.Max(sessionsWithoutBot, sample.ActiveSessions - sample.BotsThatMayHoldSession);
        botsWithoutSession = Math.Max(botsWithoutSession, sample.BotsHoldingSession - sample.ActiveSessions);
        nearExpiry = Math.Max(nearExpiry, sample.SessionsNearLeaseExpiry);
        if (sample.MinimumSessionLeaseHeadroomSeconds is { } headroom)
        {
            leaseHeadroom = Math.Min(leaseHeadroom ?? double.MaxValue, headroom);
        }

        if (sample.WorkerHeartbeatAgeSeconds is { } age)
        {
            heartbeatAge = Math.Max(heartbeatAge ?? 0d, age);
        }

        pending = Math.Max(pending, sample.PendingItemOperations);
        connections = Math.Max(connections, sample.ServiceConnections);
        connectionsInUse = Math.Max(connectionsInUse, sample.ServiceConnectionsInUse);
        idleInTransaction = Math.Max(idleInTransaction, sample.OldestIdleInTransactionSeconds);
    }

    public StressDriftSummary? Capture()
    {
        return samples == 0
            ? null
            : new StressDriftSummary(
                samples,
                sessionsWithoutBot,
                botsWithoutSession,
                nearExpiry,
                leaseExpiryThresholdSeconds,
                leaseHeadroom,
                heartbeatAge,
                workerHeartbeatTimeoutSeconds,
                pending,
                connections,
                connectionsInUse,
                databasePoolSize,
                idleInTransaction);
    }
}

public sealed record StressTableSize(string Table, long Rows, long Bytes);

// Every user table with its exact row count and its total size, tables, indexes, and TOAST, at one
// moment, and how long the counting took.
public sealed record StressTableSnapshot(DateTime TakenAt, double Milliseconds, IReadOnlyList<StressTableSize> Tables);

public sealed record StressRedisSnapshot(DateTime TakenAt, long Keys, long UsedMemoryBytes);

public sealed record StressTableGrowth(
    string Table,
    long StartRows,
    long EndRows,
    long StartBytes,
    long EndBytes,
    double RowsPerHour,
    double BytesPerHour,
    double RowsPerPlayerHour,
    double BytesPerPlayerHour);

public sealed record StressRedisGrowth(
    StressRedisSnapshot Start,
    StressRedisSnapshot End,
    double KeysPerHour,
    double UsedMemoryBytesPerHour);

// How the database and Redis grew over the steady interval: every table with its rows and bytes
// at both ends and its growth per hour and per player hour, sorted by bytes per hour.
public sealed record StressStorageGrowthSummary(
    double Hours,
    int Players,
    string CountMethod,
    double StartCountMilliseconds,
    double EndCountMilliseconds,
    IReadOnlyList<StressTableGrowth> Tables,
    StressRedisGrowth? Redis)
{
    public const string ExactCount = "count(*) of every table";

    public static StressStorageGrowthSummary Create(
        StressTableSnapshot start,
        StressTableSnapshot end,
        StressRedisSnapshot? redisStart,
        StressRedisSnapshot? redisEnd,
        int players)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        var hours = Math.Max(0d, (end.TakenAt - start.TakenAt).TotalHours);
        var playerHours = hours * Math.Max(0, players);
        var before = start.Tables.ToDictionary(table => table.Table, StringComparer.Ordinal);
        var tables = end.Tables
            .Select(table =>
            {
                var first = before.GetValueOrDefault(table.Table) ?? new StressTableSize(table.Table, 0, 0);
                var rows = table.Rows - first.Rows;
                var bytes = table.Bytes - first.Bytes;
                return new StressTableGrowth(
                    table.Table,
                    first.Rows,
                    table.Rows,
                    first.Bytes,
                    table.Bytes,
                    PerUnit(rows, hours),
                    PerUnit(bytes, hours),
                    PerUnit(rows, playerHours),
                    PerUnit(bytes, playerHours));
            })
            .OrderByDescending(table => table.BytesPerHour)
            .ThenByDescending(table => table.RowsPerHour)
            .ThenBy(table => table.Table, StringComparer.Ordinal)
            .ToArray();
        var redis = redisStart is not null && redisEnd is not null
            ? new StressRedisGrowth(
                redisStart,
                redisEnd,
                PerUnit(redisEnd.Keys - redisStart.Keys, hours),
                PerUnit(redisEnd.UsedMemoryBytes - redisStart.UsedMemoryBytes, hours))
            : null;
        return new StressStorageGrowthSummary(hours, players, ExactCount, start.Milliseconds, end.Milliseconds, tables, redis);
    }

    private static double PerUnit(long amount, double units)
    {
        return units <= 0d ? 0d : amount / units;
    }
}

// The memory of one process over the steady interval: the least-squares slope of its working
// set and the average of every fifth of the interval, and where the process reports it, its
// managed heap the same way, generation 2 collections per hour, and allocation per second.
public sealed record StressMemoryTrend(
    string Process,
    int Samples,
    double WorkingSetSlopeMiBPerHour,
    IReadOnlyList<double?> WorkingSetFifthsMiB,
    double? ManagedHeapSlopeMiBPerHour = null,
    IReadOnlyList<double?>? ManagedHeapFifthsMiB = null,
    double? Generation2CollectionsPerHour = null,
    double? AllocatedBytesPerSecond = null)
{
    // How far the last fifth's average working set lies above the second fifth's; the first
    // fifth is left out because caches, pools, and the JIT still warm up in it.
    public double? WorkingSetGrowthMiB => WorkingSetFifthsMiB.Count == 5 ? WorkingSetFifthsMiB[4] - WorkingSetFifthsMiB[1] : null;
}

// The worker's memory over the steady interval from its own performance file: its working set
// and managed heap at the end of every steady interval, generation 2 collections per hour, and
// allocation per second.
public static class StressWorkerMemory
{
    private const double Mebibyte = 1024d * 1024d;

    public static StressMemoryTrend? Create(
        IReadOnlyList<StressWorkerInterval> intervals,
        DateTime? steadyStartedAt,
        DateTime? steadyEndedAt)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        if (steadyStartedAt is not { } start || steadyEndedAt is not { } end || end <= start)
        {
            return null;
        }

        var steady = intervals
            .Where(interval => interval.IntervalStart >= start && interval.IntervalEnd <= end)
            .ToArray();
        if (steady.Length == 0)
        {
            return null;
        }

        var workingSet = new StressTrendAccumulator(start.Ticks, end - start, TimeSpan.TicksPerSecond);
        var heap = new StressTrendAccumulator(start.Ticks, end - start, TimeSpan.TicksPerSecond);
        foreach (var interval in steady)
        {
            workingSet.Add(interval.IntervalEnd.Ticks, interval.WorkingSetBytes);
            heap.Add(interval.IntervalEnd.Ticks, interval.ManagedHeapBytes);
        }

        var seconds = steady.Sum(interval => interval.IntervalSeconds);
        return new StressMemoryTrend(
            "worker",
            steady.Length,
            workingSet.SlopePerHour() / Mebibyte,
            workingSet.Fifths(1d / Mebibyte),
            heap.SlopePerHour() / Mebibyte,
            heap.Fifths(1d / Mebibyte),
            seconds <= 0d ? null : steady.Sum(interval => interval.Generation2Collections) / seconds * 3600d,
            seconds <= 0d ? null : steady.Sum(interval => interval.AllocatedBytes) / seconds);
    }
}

// What a long run shows besides its quality rules: how the database and Redis grew, the worst
// drift values, and the memory of every process.
public sealed record StressSoakSummary(
    StressStorageGrowthSummary? Storage,
    StressDriftSummary? Drift,
    IReadOnlyList<StressMemoryTrend> Memory);

// A least-squares slope and the averages of five equal parts of a planned interval, from samples
// that arrive one at a time; nothing per sample is kept.
public sealed class StressTrendAccumulator
{
    private readonly double[] fifthSums = new double[5];
    private readonly int[] fifthCounts = new int[5];
    private readonly long startTimestamp;
    private readonly double frequency;
    private readonly double fifthSeconds;
    private double sumX;
    private double sumY;
    private double sumXX;
    private double sumXY;

    // Timestamps count frequency units per second: Stopwatch ticks by default, or DateTime ticks
    // with TimeSpan.TicksPerSecond.
    public StressTrendAccumulator(long startTimestamp, TimeSpan plannedDuration, double? frequency = null)
    {
        this.startTimestamp = startTimestamp;
        this.frequency = frequency ?? Stopwatch.Frequency;
        fifthSeconds = Math.Max(0.001d, plannedDuration.TotalSeconds / 5d);
    }

    public int Samples { get; private set; }

    public void Add(long timestamp, double value)
    {
        var x = (timestamp - startTimestamp) / frequency;
        Samples++;
        sumX += x;
        sumY += value;
        sumXX += x * x;
        sumXY += x * value;
        var fifth = Math.Clamp((int)Math.Floor(x / fifthSeconds), 0, 4);
        fifthSums[fifth] += value;
        fifthCounts[fifth]++;
    }

    // The slope in value units per hour, or 0 without two distinct sample times.
    public double SlopePerHour()
    {
        var denominator = (Samples * sumXX) - (sumX * sumX);
        return Samples < 2 || denominator <= 0d
            ? 0d
            : (((Samples * sumXY) - (sumX * sumY)) / denominator) * 3600d;
    }

    // The average of every fifth, scaled, or null for a fifth without samples.
    public double?[] Fifths(double scale = 1d)
    {
        var averages = new double?[5];
        for (var index = 0; index < 5; index++)
        {
            averages[index] = fifthCounts[index] == 0 ? null : fifthSums[index] / fifthCounts[index] * scale;
        }

        return averages;
    }

    // The average of the first and the last fifth that has samples, for the window fields.
    public double FirstFifth() => Fifths().FirstOrDefault(value => value is not null) ?? 0d;

    public double LastFifth() => Fifths().LastOrDefault(value => value is not null) ?? 0d;
}
