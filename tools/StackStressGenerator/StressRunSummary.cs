using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mmorpg.Tools.StackStressGenerator;

public sealed record StressMachine(
    string Cpu,
    int LogicalCores,
    long MemoryBytes,
    string OperatingSystem,
    string DotNet)
{
    public static StressMachine Current()
    {
        return new StressMachine(
            CpuModel(),
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription);
    }

    private static string CpuModel()
    {
        try
        {
            if (System.OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key?.GetValue("ProcessorNameString") is string name)
                {
                    return name.Trim();
                }
            }
            else if (File.Exists("/proc/cpuinfo"))
            {
                var line = File.ReadLines("/proc/cpuinfo")
                    .FirstOrDefault(candidate => candidate.StartsWith("model name", StringComparison.Ordinal));
                if (line is not null)
                {
                    return line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "unknown";
        }

        return RuntimeInformation.ProcessArchitecture.ToString();
    }
}

// The small, stable record of one tools/Test-Load.ps1 run, which -UpdateBaseline checks in as
// tools/load/baselines/<scenario>.json. KeyNumbers is flat, so baselines compare key by key.
public sealed record StressRunSummary(
    string Scenario,
    string? Commit,
    bool? TreeDirty,
    DateTime Date,
    StressMachine Machine,
    string Mode,
    string Workload,
    int Bots,
    double SteadySeconds,
    string Result,
    IReadOnlyList<StressVerdictEntry> Verdict,
    IReadOnlyDictionary<string, double?> KeyNumbers)
{
    // What a long run shows, small enough to read in the summary: the ten tables that grew the
    // most, Redis, the memory of every process, the worst drift values, the deaths of every
    // role per bot and ten minutes, the churn, and the message classes of the service logs.
    public StressSummarySoak? Soak { get; init; }

    // The network profile the run went through, with the proxy's counts, or null for a direct
    // connection. A baseline is per scenario and profile.
    public StressNetworkSummary? Network { get; init; }

    public const int TopTables = 10;

    public static StressRunSummary Create(
        StressRunReport report,
        StressGeneratorOptions options,
        int exitCode,
        StressMachine machine)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create(report, options.ScenarioName, options.SourceCommit, options.SourceTreeDirty, exitCode, machine);
    }

    public static StressRunSummary Create(
        StressRunReport report,
        string? scenario,
        string? commit,
        bool? treeDirty,
        int exitCode,
        StressMachine machine)
    {
        ArgumentNullException.ThrowIfNull(report);
        var numbers = new SortedDictionary<string, double?>(StringComparer.Ordinal)
        {
            ["joinP95Ms"] = report.Bots.JoinLatency?.P95Ms,
            ["inputAckP50Ms"] = report.Bots.InputAcknowledgementLatency?.P50Ms,
            ["inputAckP95Ms"] = report.Bots.InputAcknowledgementLatency?.P95Ms,
            ["inputAckP99Ms"] = report.Bots.InputAcknowledgementLatency?.P99Ms,
            ["remoteUpdateRateAverageHz"] = report.Bots.RemoteCadence?.AverageHz,
            ["remoteUpdateRateP5Hz"] = report.Bots.RemoteCadence?.P5Hz,
            ["generatorCpuAverageSingleCorePercent"] = report.GeneratorProcess?.AverageSingleCoreCpuPercent
        };
        foreach (var operation in report.Gameplay.Operations)
        {
            numbers[$"operationP95Ms.{operation.Key}"] = operation.Value.Latency?.P95Ms;
        }

        numbers["generatorPollLatenessP99Ms"] = report.GeneratorTiming?.PollLateness?.P99Ms;
        if (report.Playing is { } playing)
        {
            numbers["attackFirstHitP95Ms"] = playing.Timings.GetValueOrDefault(PlayingMetrics.AttackFirstHit)?.P95Ms;
            numbers["routeQueueP99Ms"] = report.GeneratorTiming?.RouteQueue?.P99Ms;
            numbers["kills"] = playing.Counters.GetValueOrDefault("kills");
            numbers["botDeaths"] = playing.Counters.GetValueOrDefault("botDeaths");
            foreach (var role in playing.DeathsPerBotPerTenMinutes ?? new Dictionary<string, double>())
            {
                numbers[$"deathsPerBotPer10Min.{role.Key}"] = role.Value;
            }
        }

        foreach (var trend in report.Soak?.Memory ?? [])
        {
            numbers[$"workingSetSlopeMiBPerHour.{trend.Process}"] = trend.WorkingSetSlopeMiBPerHour;
            numbers[$"workingSetGrowthMiB.{trend.Process}"] = trend.WorkingSetGrowthMiB;
        }

        if (report.Soak?.Storage is { } storage)
        {
            numbers["databaseBytesPerHour"] = storage.Tables.Sum(table => table.BytesPerHour);
            numbers["redisBytesPerHour"] = storage.Redis?.UsedMemoryBytesPerHour;
        }

        var worker = report.WorkerPerformance;
        if (worker is not null && worker.SteadyIntervals > 0)
        {
            numbers["workerTickP99Ms"] = worker.Durations.GetValueOrDefault("tick")?.WorstP99Ms;
            numbers["workerCpuAverageSingleCorePercent"] = worker.Levels["cpuSingleCorePercent"].Average;
            numbers["workerWorkingSetMaximumBytes"] = worker.Levels["workingSetBytes"].Worst;
            var players = worker.Levels["peers"].Average;
            if (players > 0 && worker.SteadySeconds > 0)
            {
                // Payload bytes per joined client and second, as the worker counts them.
                numbers["bytesInPerBotPerSecond"] = worker.Counters["bytesIn"].Total / worker.SteadySeconds / players;
                numbers["bytesOutPerBotPerSecond"] = worker.Counters["bytesOut"].Total / worker.SteadySeconds / players;
            }
        }

        return new StressRunSummary(
            scenario ?? $"{report.Configuration.Mode}-{report.Configuration.Workload}",
            commit,
            treeDirty,
            report.EndedAt,
            machine,
            report.Configuration.Mode,
            report.Configuration.Workload,
            report.Configuration.BotCount,
            report.Configuration.SteadyDurationSeconds,
            exitCode switch
            {
                0 => "passed",
                Program.CouldNotMeasureExitCode => "could not measure",
                _ => "failed"
            },
            report.Verdict?.Rules ?? [],
            numbers)
        {
            Soak = StressSummarySoak.Create(report),
            Network = report.Network
        };
    }

    public static StressRunSummary Parse(string json)
    {
        return JsonSerializer.Deserialize<StressRunSummary>(json, SerializerOptions)
            ?? throw new InvalidDataException("The summary is empty.");
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DictionaryKeyPolicy = null,
        // The baselines are checked in, so comparisons such as <= stay readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, SerializerOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

public sealed record StressSummarySoak(
    double? StorageHours,
    IReadOnlyList<StressTableGrowth> TopTables,
    StressRedisGrowth? Redis,
    IReadOnlyList<StressMemoryTrend> Memory,
    StressDriftSummary? Drift,
    IReadOnlyDictionary<string, double>? DeathsPerBotPerTenMinutes,
    IReadOnlyDictionary<string, long>? Churn,
    IReadOnlyList<StressLogClass> LogClasses)
{
    // The churn's counters, by their report names.
    private static readonly string[] ChurnCounters =
    [
        "churnRequests", "churnSkippedBusy", "churnSkippedUnsafe", "churnSkippedLate", "churnRetreats",
        "leaves", "leavesFighter", "leavesGatherer", "leavesWalker", "leavesTrader", "leavesWithLogout",
        "rejoinsBody", "rejoinsSaved", "rejoinsSavedFoundBody", "rejoinsBodyFoundGone",
        "rejoinBodiesHit", "rejoinBodiesDied", "rejoinChecksFailed"
    ];

    public static StressSummarySoak Create(StressRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var counters = report.Playing?.Counters;
        return new StressSummarySoak(
            report.Soak?.Storage?.Hours,
            report.Soak?.Storage?.Tables.Take(StressRunSummary.TopTables).ToArray() ?? [],
            report.Soak?.Storage?.Redis,
            report.Soak?.Memory ?? [],
            report.Soak?.Drift,
            report.Playing?.DeathsPerBotPerTenMinutes,
            counters is null || counters.GetValueOrDefault("churnRequests") == 0
                ? null
                : ChurnCounters.ToDictionary(name => name, name => counters.GetValueOrDefault(name), StringComparer.Ordinal),
            report.ServiceLogs?.Classes ?? []);
    }
}
