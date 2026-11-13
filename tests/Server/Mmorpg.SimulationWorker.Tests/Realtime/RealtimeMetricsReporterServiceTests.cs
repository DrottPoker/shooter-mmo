using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The periodic log lines and the optional performance file come from one captured interval.
public sealed class RealtimeMetricsReporterServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "mmorpg-performance-report-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TheFileCarriesTheValuesOfTheIntervalThatTheLogLinesShow()
    {
        var path = Path.Combine(directory, "worker-performance.jsonl");
        using var network = new RealtimeNetworkMetrics();
        using var performance = new RealtimePerformanceMetrics();
        var logger = new RecordingLogger();
        var reporter = CreateReporter(network, performance, path, logger);
        var tickBudget = TimeSpan.FromMilliseconds(1000d / 30d);
        performance.RecordSimulationTick(TimeSpan.FromMilliseconds(3), tickBudget);
        performance.RecordSimulationTick(TimeSpan.FromMilliseconds(40), tickBudget);
        performance.RecordTickResynchronization();
        performance.RecordSnapshotPacketReuse(1, 2, 3, 7);
        network.RecordReceived(100);
        network.RecordSent(50);
        network.RecordSent(25);
        network.RecordQuotaRejected();
        network.RecordSnapshotDropped(2);

        var interval = reporter.ReportInterval();

        var lines = File.ReadAllLines(path);
        var entry = Assert.Single(lines);
        using var document = JsonDocument.Parse(entry);
        var root = document.RootElement;
        Assert.Equal(interval.Seconds, root.GetProperty("intervalSeconds").GetDouble());
        Assert.Equal(interval.StartedAtUtc, root.GetProperty("intervalStart").GetDateTime());
        Assert.Equal(interval.EndedAtUtc, root.GetProperty("intervalEnd").GetDateTime());
        var tick = root.GetProperty("tick");
        Assert.Equal(2, tick.GetProperty("samples").GetInt64());
        Assert.Equal(interval.Performance.SimulationTick!.P99UpperBoundMilliseconds, tick.GetProperty("p99Ms").GetDouble());
        Assert.Equal(50, tick.GetProperty("p99Ms").GetDouble());
        Assert.Equal(1, root.GetProperty("tickOverruns").GetInt64());
        Assert.Equal(1, root.GetProperty("tickResynchronizations").GetInt64());
        Assert.Equal(7, root.GetProperty("snapshotEntityUpdatesDeferred").GetInt64());
        Assert.Equal(1, root.GetProperty("packetsIn").GetInt64());
        Assert.Equal(100, root.GetProperty("bytesIn").GetInt64());
        Assert.Equal(2, root.GetProperty("packetsOut").GetInt64());
        Assert.Equal(75, root.GetProperty("bytesOut").GetInt64());
        Assert.Equal(1, root.GetProperty("quotaRejections").GetInt64());
        Assert.Equal(2, root.GetProperty("snapshotDrops").GetInt64());
        Assert.Equal(interval.WorkingSetBytes, root.GetProperty("workingSetBytes").GetInt64());
        Assert.Equal(interval.SingleCoreCpuPercent, root.GetProperty("cpuSingleCorePercent").GetDouble());
        Assert.Equal(interval.Performance.ProcessAllocatedBytes, root.GetProperty("allocatedBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("interest").ValueKind);

        // The log lines show the same interval.
        Assert.Equal(3, logger.Entries.Count);
        var performanceLine = logger.Entries[2];
        Assert.Equal(1L, performanceLine["TickResynchronizations"]);
        Assert.Equal(1L, performanceLine["TickOverruns"]);
        Assert.Contains("p99 <= 50.000 ms", (string)performanceLine["SimulationTick"]!, StringComparison.Ordinal);
        var networkLine = logger.Entries[1];
        Assert.Equal(1L, networkLine["QuotaRejects"]);
        Assert.Equal(2L, networkLine["SnapshotDrops"]);
        var statusLine = logger.Entries[0];
        Assert.Equal(interval.Seconds, statusLine["SampleSeconds"]);
        Assert.Equal(2 / interval.Seconds, statusLine["SnapshotDropsPerSecond"]);
    }

    [Fact]
    public void EachIntervalAppendsOneLineWithTheCountersOfThatIntervalOnly()
    {
        var path = Path.Combine(directory, "nested", "worker-performance.jsonl");
        using var network = new RealtimeNetworkMetrics();
        using var performance = new RealtimePerformanceMetrics();
        var reporter = CreateReporter(network, performance, path, new RecordingLogger());
        network.RecordReceived(10);
        var first = reporter.ReportInterval();
        network.RecordReceived(10);
        network.RecordReceived(10);

        var second = reporter.ReportInterval();

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        using var secondEntry = JsonDocument.Parse(lines[1]);
        Assert.Equal(2, secondEntry.RootElement.GetProperty("packetsIn").GetInt64());
        Assert.Equal(first.EndedAtUtc, second.StartedAtUtc);
    }

    [Fact]
    public void NoPathWritesNoFile()
    {
        Directory.CreateDirectory(directory);
        using var network = new RealtimeNetworkMetrics();
        using var performance = new RealtimePerformanceMetrics();
        var logger = new RecordingLogger();
        var reporter = CreateReporter(network, performance, null, logger);

        reporter.ReportInterval();

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        Assert.Null(RealtimePerformanceReportWriter.Create(null, logger));
        Assert.Equal(3, logger.Entries.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RealtimeMetricsReporterService CreateReporter(
        RealtimeNetworkMetrics network,
        RealtimePerformanceMetrics performance,
        string? path,
        ILogger<RealtimeMetricsReporterService> logger)
    {
        var config = RealtimeServerHarness.CreateConfig(27_999, new RealtimeServerHarnessOptions()) with
        {
            PerformanceReportPath = path
        };
        return new RealtimeMetricsReporterService(network, performance, config, logger);
    }

    private sealed class RecordingLogger : ILogger<RealtimeMetricsReporterService>
    {
        public List<Dictionary<string, object?>> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information
                && state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                Entries.Add(values.ToDictionary(pair => pair.Key, pair => pair.Value));
            }
        }
    }
}
