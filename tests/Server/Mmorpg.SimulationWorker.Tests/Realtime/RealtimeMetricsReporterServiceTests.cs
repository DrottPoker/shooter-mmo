using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.WorldActors;

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
        performance.TickPhaseTimer("movement").Record(TimeSpan.FromMilliseconds(2));
        performance.TickPhaseTimer("snapshots").Record(TimeSpan.FromMilliseconds(30));
        performance.TickPhaseTimer("corpse presence");
        performance.LoopStepTimer("send flush").Record(TimeSpan.FromMilliseconds(5));
        performance.RecordTickResynchronization();
        performance.RecordSnapshotSends(1, 2, 3, 7);
        performance.RecordLoopFailure("snapshots", RealtimeLoopFailureScope.Step);
        performance.RecordLoopFailure("movement", RealtimeLoopFailureScope.Player);
        performance.RecordLoopFailure("snapshots", RealtimeLoopFailureScope.Peer);
        network.RecordReceived(new byte[100]);
        network.RecordSent(new byte[50]);
        network.RecordSent(new byte[25]);
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
        Assert.Equal(1, root.GetProperty("loopStepFailures").GetInt64());
        Assert.Equal(2, root.GetProperty("loopIsolatedFailures").GetInt64());
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
        // A phase without a sample in the interval is left out.
        var phases = root.GetProperty("phases");
        Assert.Equal(["movement", "snapshots"], phases.EnumerateObject().Select(phase => phase.Name));
        Assert.Equal(30, phases.GetProperty("snapshots").GetProperty("maximumMs").GetDouble());
        Assert.Equal(1, root.GetProperty("loopSteps").GetProperty("send flush").GetProperty("samples").GetInt64());
        Assert.Equal(interval.Performance.GcPauseMilliseconds, root.GetProperty("gcPauseMs").GetDouble());
        // The ticks of 3 and 40 ms and the send flush of 5 ms; the phases lie inside the ticks.
        Assert.Equal(48d, interval.Performance.LoopBusyMilliseconds, 9);
        Assert.Equal(48d / (interval.Seconds * 1000d) * 100d, root.GetProperty("loopBusyPercent").GetDouble(), 9);

        // The log lines show the same interval.
        Assert.Equal(3, logger.Entries.Count);
        var performanceLine = logger.Entries[2];
        Assert.Equal(1L, performanceLine["TickResynchronizations"]);
        Assert.Equal(1L, performanceLine["TickOverruns"]);
        Assert.Equal(1L, performanceLine["LoopStepFailures"]);
        Assert.Equal(2L, performanceLine["LoopIsolatedFailures"]);
        Assert.Equal(interval.LoopBusyPercent, performanceLine["LoopBusyPercent"]);
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
        network.RecordReceived(new byte[10]);
        var first = reporter.ReportInterval();
        network.RecordReceived(new byte[10]);
        network.RecordReceived(new byte[10]);

        var second = reporter.ReportInterval();

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        using var secondEntry = JsonDocument.Parse(lines[1]);
        Assert.Equal(2, secondEntry.RootElement.GetProperty("packetsIn").GetInt64());
        Assert.Equal(first.EndedAtUtc, second.StartedAtUtc);
    }

    // The messages of every kind with a message in the interval, by type name in type byte
    // order; a kind without a message in the interval is left out.
    [Fact]
    public void TheFileCarriesTheMessagesOfEveryKindInTheInterval()
    {
        var path = Path.Combine(directory, "worker-performance.jsonl");
        using var network = new RealtimeNetworkMetrics();
        using var performance = new RealtimePerformanceMetrics();
        var reporter = CreateReporter(network, performance, path, new RecordingLogger());
        var disconnect = RealtimeProtocol.EncodeServerDisconnect("code", "message");
        var leaveAccepted = RealtimeProtocol.EncodeLeaveAccepted(30);
        var joinRequest = RealtimeProtocol.EncodeJoinRequest("ticket");
        network.RecordSent(disconnect);
        network.RecordSent(leaveAccepted);
        network.RecordSent(leaveAccepted);
        network.RecordReceived(joinRequest);
        network.RecordReceived([]);
        reporter.ReportInterval();
        network.RecordSent(leaveAccepted);

        reporter.ReportInterval();

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        var sent = first.RootElement.GetProperty("sentByMessage");
        Assert.Equal(["LeaveAccepted", "ServerDisconnect"], sent.EnumerateObject().Select(kind => kind.Name));
        Assert.Equal(2, sent.GetProperty("LeaveAccepted").GetProperty("messages").GetInt64());
        Assert.Equal(2L * leaveAccepted.Length, sent.GetProperty("LeaveAccepted").GetProperty("bytes").GetInt64());
        Assert.Equal(disconnect.Length, sent.GetProperty("ServerDisconnect").GetProperty("bytes").GetInt64());
        var received = first.RootElement.GetProperty("receivedByMessage");
        Assert.Equal(["0", "JoinRequest"], received.EnumerateObject().Select(kind => kind.Name));
        Assert.Equal(1, received.GetProperty("0").GetProperty("messages").GetInt64());
        Assert.Equal(0, received.GetProperty("0").GetProperty("bytes").GetInt64());
        Assert.Equal(3, first.RootElement.GetProperty("packetsOut").GetInt64());
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal(
            ["LeaveAccepted"],
            second.RootElement.GetProperty("sentByMessage").EnumerateObject().Select(kind => kind.Name));
        Assert.Empty(second.RootElement.GetProperty("receivedByMessage").EnumerateObject());
    }

    [Fact]
    public void TheFileCarriesTheMobGaugesAndTheMobRoutesPlannedInTheInterval()
    {
        var path = Path.Combine(directory, "worker-performance.jsonl");
        using var network = new RealtimeNetworkMetrics();
        using var performance = new RealtimePerformanceMetrics();
        using var combat = new CombatMetrics();
        using var worldActors = new WorldActorMetrics();
        var config = RealtimeServerHarness.CreateConfig(27_999, new RealtimeServerHarnessOptions()) with
        {
            PerformanceReportPath = path
        };
        combat.RecordMobPathPlan("reached");
        var reporter = new RealtimeMetricsReporterService(
            network,
            performance,
            config,
            new RecordingLogger(),
            combat,
            worldActors);
        combat.SetMobActivity(engaged: 3, moving: 2, queuedPlans: 1);
        combat.RecordMobPathPlan("reached");
        combat.RecordMobPathPlan("no_path");

        reporter.ReportInterval();
        combat.SetMobActivity(engaged: 0, moving: 0, queuedPlans: 0);
        reporter.ReportInterval();

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal(0, first.RootElement.GetProperty("mobs").GetInt64());
        Assert.Equal(3, first.RootElement.GetProperty("engagedMobs").GetInt64());
        Assert.Equal(2, first.RootElement.GetProperty("movingMobs").GetInt64());
        Assert.Equal(1, first.RootElement.GetProperty("mobPathQueue").GetInt64());
        // The plan before the reporter started belongs to no interval.
        Assert.Equal(2, first.RootElement.GetProperty("mobPathPlans").GetInt64());
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal(0, second.RootElement.GetProperty("engagedMobs").GetInt64());
        Assert.Equal(0, second.RootElement.GetProperty("mobPathPlans").GetInt64());
    }

    [Fact]
    public void AWorkerWithoutCombatMetricsWritesNoMobNumbers()
    {
        var path = Path.Combine(directory, "worker-performance.jsonl");
        using var network = new RealtimeNetworkMetrics();
        using var performance = new RealtimePerformanceMetrics();
        var reporter = CreateReporter(network, performance, path, new RecordingLogger());

        reporter.ReportInterval();

        using var entry = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path)));
        Assert.Equal(JsonValueKind.Null, entry.RootElement.GetProperty("engagedMobs").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.RootElement.GetProperty("mobPathPlans").ValueKind);
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
