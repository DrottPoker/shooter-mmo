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
    public static StressRunSummary Create(
        StressRunReport report,
        StressGeneratorOptions options,
        int exitCode,
        StressMachine machine)
    {
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
            options.ScenarioName ?? $"{report.Configuration.Mode}-{report.Configuration.Workload}",
            options.SourceCommit,
            options.SourceTreeDirty,
            report.EndedAt,
            machine,
            report.Configuration.Mode,
            report.Configuration.Workload,
            report.Configuration.BotCount,
            report.Configuration.SteadyDurationSeconds,
            exitCode == 0 ? "passed" : "failed",
            report.Verdict?.Rules ?? [],
            numbers);
    }

    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DictionaryKeyPolicy = null,
            // The baselines are checked in, so comparisons such as <= stay readable.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, options, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
