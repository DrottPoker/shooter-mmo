using Mmorpg.Testing;
using Mmorpg.Tools.SimulationBotClient;
using Mmorpg.Tools.StackStressGenerator;

namespace Mmorpg.Tools.Tests;

// The quality boundary file, the verdict of every rule, the fold of the worker's performance
// file, and the summary that tools/Test-Load.ps1 keeps.
public sealed class StressQualityVerdictTests
{
    private static readonly DateTime RunStart = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SteadyStart = RunStart.AddSeconds(20);
    private static readonly DateTime SteadyEnd = RunStart.AddSeconds(60);
    private static readonly DateTime RunEnd = RunStart.AddSeconds(70);

    private static string BoundaryPath => Path.Combine(RepositoryPaths.Root, "tools", "load", "quality-boundary.json");

    [Fact]
    public void TheCheckedInBoundaryNamesEveryKnownRuleWithItsUnit()
    {
        var boundary = StressQualityBoundary.Load(BoundaryPath);

        Assert.Equal(
            StressQualityBoundary.KnownRules.Keys.Order(StringComparer.Ordinal),
            boundary.Rules.Select(rule => rule.Rule).Order(StringComparer.Ordinal));
        var tick = boundary.Rules.Single(rule => rule.Rule == "simulation_tick_p99_ms");
        Assert.Equal(33.34, tick.Limit);
        Assert.Contains("p99 <= 33.34 ms", tick.Description, StringComparison.Ordinal);
        Assert.Equal(10, boundary.Rules.Single(rule => rule.Rule == "remote_update_rate_p5_hz").Limit);
    }

    [Theory]
    [InlineData("\"extra\": 1,", "unknown property 'extra'")]
    [InlineData("", "")]
    public void TheBoundaryRejectsUnknownProperties(string extra, string expected)
    {
        var json = File.ReadAllText(BoundaryPath).Replace(
            "\"description\": \"The quality",
            extra + "\"description\": \"The quality",
            StringComparison.Ordinal);
        if (expected.Length == 0)
        {
            StressQualityBoundary.Parse(json, "boundary");
            return;
        }

        var exception = Assert.Throws<StressGeneratorOptionException>(() => StressQualityBoundary.Parse(json, "boundary"));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"rule\": \"deadlocks\"", "\"rule\": \"dead_locks\"", "'dead_locks' is unknown")]
    [InlineData("\"rule\": \"deadlocks\",\n      \"limit\": 0,\n      \"unit\": \"count\"", "\"rule\": \"deadlocks\",\n      \"limit\": 0,\n      \"unit\": \"ms\"", "measured in 'count'")]
    [InlineData("\"rule\": \"deadlocks\",\n      \"limit\": 0,", "\"rule\": \"deadlocks\",\n      \"limit\": 0,\n      \"comment\": \"x\",", "unknown property 'comment'")]
    [InlineData("\"rule\": \"deadlocks\"", "\"rule\": \"snapshot_drops\"", "appears twice")]
    public void TheBoundaryRejectsUnknownDuplicateAndMisunitedRules(string find, string replace, string expected)
    {
        var json = File.ReadAllText(BoundaryPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(find, json, StringComparison.Ordinal);

        var exception = Assert.Throws<StressGeneratorOptionException>(
            () => StressQualityBoundary.Parse(json.Replace(find, replace, StringComparison.Ordinal), "boundary"));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBoundaryRejectsAMissingRule()
    {
        var json = """
            { "description": "x", "rules": [ { "rule": "deadlocks", "limit": 0, "unit": "count", "description": "x" } ] }
            """;

        var exception = Assert.Throws<StressGeneratorOptionException>(() => StressQualityBoundary.Parse(json, "boundary"));

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanFullStackRunPassesEveryRule()
    {
        var verdict = Boundary().Evaluate(FullStackReport());

        Assert.True(verdict.Passed, string.Join("; ", verdict.Rules.Where(rule => !rule.Passed).Select(rule => $"{rule.Rule} {rule.Note}")));
        Assert.True(verdict.Valid);
        Assert.Equal(StressQualityBoundary.KnownRules.Count, verdict.Rules.Count);
        // The memory rule judges only a steady interval of ten minutes or more.
        Assert.All(verdict.Rules.Where(rule => rule.Rule != "process_memory_growth"), rule => Assert.NotNull(rule.Measured));
        Assert.StartsWith("Not applicable", verdict.Rules.Single(rule => rule.Rule == "process_memory_growth").Note, StringComparison.Ordinal);
        Assert.Equal(
            StressQualityBoundary.ValidityRules.Order(StringComparer.Ordinal),
            verdict.Rules.Where(rule => rule.Validity).Select(rule => rule.Rule).Order(StringComparer.Ordinal));
    }

    // A generator that fell behind measured nothing: the run is invalid, and the quality rules
    // keep their own result.
    [Theory]
    [InlineData("generator_poll_lateness_p99_ms")]
    [InlineData("generator_route_queue_p99_ms")]
    public void AValidityRuleFailsAloneAndMakesTheRunInvalid(string rule)
    {
        var report = FullStackReport();
        var timing = report.GeneratorTiming!;
        report = report with
        {
            GeneratorTiming = rule == "generator_poll_lateness_p99_ms"
                ? timing with { PollLateness = timing.PollLateness! with { P99Ms = 40 } }
                : timing with { RouteQueue = timing.RouteQueue! with { P99Ms = 2500 } }
        };

        var verdict = Boundary().Evaluate(report);

        var failed = Assert.Single(verdict.Rules, entry => !entry.Passed);
        Assert.Equal(rule, failed.Rule);
        Assert.True(failed.Validity);
        Assert.False(verdict.Valid);
        Assert.True(verdict.Passed);
    }

    [Fact]
    public void AWorkloadWithoutRoutesOrSteadyPollsSkipsTheValidityRules()
    {
        var report = FullStackReport() with
        {
            GeneratorTiming = new StressGeneratorTimingSummary(3, null, null, PlansRoutes: false)
        };

        var verdict = Boundary().Evaluate(report);

        Assert.True(verdict.Valid);
        Assert.All(
            verdict.Rules.Where(rule => rule.Validity),
            rule => Assert.StartsWith("Not applicable", rule.Note, StringComparison.Ordinal));
    }

    public static TheoryData<string> BrokenRules => new()
    {
        "simulation_tick_p99_ms",
        "fixed_tick_resynchronizations",
        "input_acknowledgement_p99_ms",
        "remote_update_rate_p5_hz",
        "gameplay_operation_p95_ms",
        "bot_failures",
        "gameplay_unexpected_failures",
        "http_unexpected_failures",
        "quota_rejections",
        "snapshot_drops",
        "owner_snapshot_gaps",
        "expired_unreleased_sessions",
        "deadlocks",
        "item_invariant_violations",
        "process_memory_growth",
        "service_log_errors"
    };

    [Theory]
    [MemberData(nameof(BrokenRules))]
    public void EachRuleFailsAlone(string rule)
    {
        var verdict = Boundary().Evaluate(Break(FullStackReport(), rule));

        var failed = Assert.Single(verdict.Rules, entry => !entry.Passed);
        Assert.Equal(rule, failed.Rule);
        Assert.NotNull(failed.Measured);
        Assert.False(verdict.Passed);
    }

    [Fact]
    public void TheTickRulePassesAtTheBucketBoundOfTheBudget()
    {
        var report = FullStackReport(tickP99: 33.34);

        var tick = Boundary().Evaluate(report).Rules.Single(rule => rule.Rule == "simulation_tick_p99_ms");

        Assert.True(tick.Passed);
        Assert.Equal(33.34, tick.Measured);
    }

    [Fact]
    public void ARuleThatCannotBeEvaluatedFails()
    {
        var report = FullStackReport() with { WorkerPerformance = null };

        var verdict = Boundary().Evaluate(report);

        var unevaluated = verdict.Rules.Where(rule => !rule.Passed).Select(rule => rule.Rule).Order(StringComparer.Ordinal);
        Assert.Equal(
            ["fixed_tick_resynchronizations", "quota_rejections", "simulation_tick_p99_ms", "snapshot_drops"],
            unevaluated);
        Assert.All(
            verdict.Rules.Where(rule => !rule.Passed),
            rule =>
            {
                Assert.Null(rule.Measured);
                Assert.StartsWith("Could not be evaluated", rule.Note, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void NoRemoteCadenceAndNoSteadyWorkerIntervalCannotBeEvaluated()
    {
        var report = FullStackReport() with
        {
            Bots = FullStackReport().Bots with { RemoteCadence = null },
            WorkerPerformance = StressWorkerPerformance.Fold([], RunStart, RunEnd, SteadyStart, SteadyEnd)
        };

        var failed = Boundary().Evaluate(report).Rules.Where(rule => !rule.Passed).Select(rule => rule.Rule);

        Assert.Contains("remote_update_rate_p5_hz", failed);
        Assert.Contains("simulation_tick_p99_ms", failed);
        Assert.Contains("fixed_tick_resynchronizations", failed);
    }

    [Fact]
    public void WorkerOnlyRunsSkipTheFullStackRulesAndTheLifecycleWorkloadHasNoGameplayRule()
    {
        var report = FullStackReport() with
        {
            Configuration = FullStackReport().Configuration with { Mode = "worker-only", Workload = "lifecycle" },
            FullStack = null,
            Postgres = null,
            Gameplay = new StressGameplaySummary("lifecycle", null, 0, new Dictionary<string, StressGameplayOperationSummary>())
        };

        var verdict = Boundary().Evaluate(report);

        Assert.True(verdict.Passed);
        foreach (var rule in new[] { "gameplay_operation_p95_ms", "http_unexpected_failures", "expired_unreleased_sessions", "deadlocks", "item_invariant_violations" })
        {
            var entry = verdict.Rules.Single(candidate => candidate.Rule == rule);
            Assert.Null(entry.Measured);
            Assert.StartsWith("Not applicable", entry.Note, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AGameplayWorkloadWithoutOperationsCannotBeEvaluated()
    {
        var report = FullStackReport() with
        {
            Gameplay = new StressGameplaySummary("mixed-gameplay", null, 0, new Dictionary<string, StressGameplayOperationSummary>())
        };

        var entry = Boundary().Evaluate(report).Rules.Single(rule => rule.Rule == "gameplay_operation_p95_ms");

        Assert.False(entry.Passed);
        Assert.Null(entry.Measured);
    }

    [Fact]
    public void TheFoldTakesOnlyIntervalsInsideTheSteadyWindowAndTheErrorCountersOfTheWholeRun()
    {
        StressWorkerInterval[] intervals =
        [
            Interval(RunStart.AddSeconds(-10), tickP99: 100, resynchronizations: 3, drops: 1),
            Interval(RunStart, tickP99: 50),
            Interval(RunStart.AddSeconds(10), tickP99: 50, resynchronizations: 1, drops: 2),
            Interval(RunStart.AddSeconds(20), tickP99: 8, cpu: 40),
            Interval(RunStart.AddSeconds(30), tickP99: 16, cpu: 60),
            Interval(RunStart.AddSeconds(40), tickP99: 4, cpu: 50),
            Interval(RunStart.AddSeconds(50), tickP99: 8, cpu: 50, quota: 1),
            Interval(RunStart.AddSeconds(60), tickP99: 24, drops: 5),
            Interval(RunStart.AddSeconds(70), tickP99: 250, drops: 7)
        ];

        var fold = StressWorkerPerformance.Fold(intervals, RunStart, RunEnd, SteadyStart, SteadyEnd);

        // An older worker writes no Mob numbers, which fold as none.
        Assert.Equal(0, fold.Levels["engagedMobs"].Worst);
        Assert.Equal(0, fold.Counters["mobPathPlans"].Total);
        Assert.Equal(9, fold.IntervalsRead);
        Assert.Equal(4, fold.SteadyIntervals);
        Assert.Equal(SteadyStart, fold.FirstSteadyIntervalStart);
        Assert.Equal(SteadyEnd, fold.LastSteadyIntervalEnd);
        Assert.Equal(40, fold.SteadySeconds);
        Assert.Equal(16, fold.Durations["tick"].WorstP99Ms);
        Assert.Equal(0, fold.Counters["tickResynchronizations"].Total);
        Assert.Equal(50, fold.Levels["cpuSingleCorePercent"].Average);
        Assert.Equal(60, fold.Levels["cpuSingleCorePercent"].Worst);
        Assert.Equal(1, fold.Counters["quotaRejections"].Total);
        // The run intervals overlap the run, from the one that ends in it to the one that starts in it.
        Assert.Equal(7, fold.RunIntervals);
        Assert.Equal(7, fold.RunSnapshotDrops);
        Assert.Equal(1, fold.RunQuotaRejections);
        Assert.Null(fold.RunCoverageGap);
        Assert.Null(fold.SteadyCoverageGap);
        Assert.Null(fold.Note);
    }

    [Fact]
    public void WorkerIntervalsThatEndBeforeTheRunCannotJudgeTheCounterRules()
    {
        // The worker stopped writing after the steady interval began: the drops and quota
        // rejections of the rest of the run were never seen.
        var intervals = Enumerable.Range(-1, 5)
            .Select(index => Interval(RunStart.AddSeconds(index * 10), tickP99: 8))
            .ToArray();
        var report = FullStackReport() with
        {
            WorkerPerformance = StressWorkerPerformance.Fold(intervals, RunStart, RunEnd, SteadyStart, SteadyEnd)
        };

        Assert.NotNull(report.WorkerPerformance!.RunCoverageGap);
        var rules = Boundary().Evaluate(report).Rules.ToDictionary(rule => rule.Rule);

        Assert.False(rules["quota_rejections"].Passed);
        Assert.False(rules["snapshot_drops"].Passed);
        Assert.Null(rules["snapshot_drops"].Measured);
        Assert.StartsWith("Could not be evaluated", rules["snapshot_drops"].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerIntervalsThatStartAfterTheRunCannotJudgeTheCounterRules()
    {
        var intervals = Enumerable.Range(1, 7)
            .Select(index => Interval(RunStart.AddSeconds(index * 10), tickP99: 8))
            .ToArray();

        var fold = StressWorkerPerformance.Fold(intervals, RunStart, RunEnd, SteadyStart, SteadyEnd);

        Assert.NotNull(fold.RunCoverageGap);
        Assert.Null(fold.SteadyCoverageGap);
    }

    [Fact]
    public void AMissingWorkerIntervalInsideTheSteadyIntervalCannotJudgeAnyWorkerRule()
    {
        // The line of the interval from 30 s to 40 s is missing, for example after a failed write.
        var intervals = new[] { -10, 0, 10, 20, 40, 50, 60, 70 }
            .Select(second => Interval(RunStart.AddSeconds(second), tickP99: 8))
            .ToArray();
        var report = FullStackReport() with
        {
            WorkerPerformance = StressWorkerPerformance.Fold(intervals, RunStart, RunEnd, SteadyStart, SteadyEnd)
        };

        Assert.NotNull(report.WorkerPerformance!.SteadyCoverageGap);
        var failed = Boundary().Evaluate(report).Rules.Where(rule => !rule.Passed).Select(rule => rule.Rule).ToArray();

        Assert.Contains("simulation_tick_p99_ms", failed);
        Assert.Contains("fixed_tick_resynchronizations", failed);
        Assert.Contains("quota_rejections", failed);
        Assert.Contains("snapshot_drops", failed);
    }

    [Fact]
    public void TheWorkerFileParsesTheLinesTheWorkerWritesAndSkipsAPartialLine()
    {
        const string line = """{"intervalStart":"2026-11-13T12:00:20Z","intervalEnd":"2026-11-13T12:00:30Z","intervalSeconds":10.0,"players":10,"bots":0,"peers":10,"unauthenticatedPeers":0,"entities":11,"cpuMachinePercent":2.5,"cpuSingleCorePercent":40.0,"workingSetBytes":1000,"tick":{"samples":300,"averageMs":1.5,"p95Ms":2,"p99Ms":4,"maximumMs":5.5},"tickLag":null,"tickOverruns":0,"tickResynchronizations":0,"networkPoll":null,"completedOperations":null,"joinQueueDelay":null,"joinFinalization":null,"collisionStreaming":null,"movement":null,"interest":null,"snapshotBroadcast":null,"completedOperationBacklog":0,"maximumCompletedOperationBacklog":2,"corpseMutationBacklog":0,"maximumCorpseMutationBacklog":0,"activeCorpseMutations":0,"snapshotDrops":0,"snapshotBackpressureDrops":0,"snapshotEntityUpdatesDeferred":0,"snapshotEntityRecords":1350,"snapshotVisibilityGroups":150,"snapshotPacketsEncoded":150,"snapshotPacketsSent":1500,"quotaRejections":0,"packetsIn":3000,"bytesIn":90000,"packetsOut":3100,"bytesOut":400000,"snapshotBytes":350000,"joinsAccepted":0,"joinsRejected":0,"spawnPackets":0,"despawnPackets":0,"allocatedBytes":123456,"generation0Collections":1,"generation1Collections":0,"generation2Collections":0,"managedHeapBytes":5000,"managedHeapFragmentedBytes":100,"totalManagedMemoryBytes":4000}""";

        var intervals = StressWorkerPerformance.Parse([line, "{\"intervalStart\":\"2026"]);

        var interval = Assert.Single(intervals);
        Assert.Equal(4, interval.Tick!.P99Ms);
        Assert.Equal(300, interval.Tick.Samples);
        Assert.Equal(400_000, interval.BytesOut);
        Assert.Equal(DateTimeKind.Utc, interval.IntervalStart.Kind);
    }

    [Fact]
    public async Task TheSummaryIsSmallAndCarriesTheVerdictAndTheKeyNumbers()
    {
        var report = FullStackReport();
        report = report with { Verdict = Boundary().Evaluate(report) };
        var options = StressGeneratorOptions.Parse(
            ["--scenario-name", "smoke", "--source-commit", "abc123", "--source-tree-dirty", "false"],
            Directory.GetCurrentDirectory());
        var path = Path.Combine(Path.GetTempPath(), "stress-summary-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await StressRunSummary.Create(report, options, 0, new StressMachine("cpu", 16, 1024, "os", ".NET"))
                .WriteAsync(path, TestContext.Current.CancellationToken);

            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            var root = document.RootElement;
            Assert.Equal(
                ["scenario", "commit", "treeDirty", "date", "machine", "mode", "workload", "bots", "steadySeconds", "result", "verdict", "keyNumbers", "soak"],
                root.EnumerateObject().Select(property => property.Name));
            Assert.Equal("smoke", root.GetProperty("scenario").GetString());
            Assert.Equal("abc123", root.GetProperty("commit").GetString());
            Assert.False(root.GetProperty("treeDirty").GetBoolean());
            Assert.Equal("passed", root.GetProperty("result").GetString());
            Assert.Equal(16, root.GetProperty("machine").GetProperty("logicalCores").GetInt32());
            Assert.Equal(StressQualityBoundary.KnownRules.Count, root.GetProperty("verdict").GetArrayLength());
            var numbers = root.GetProperty("keyNumbers");
            foreach (var key in new[]
                     {
                         "joinP95Ms", "inputAckP50Ms", "inputAckP95Ms", "inputAckP99Ms", "remoteUpdateRateAverageHz",
                         "remoteUpdateRateP5Hz", "generatorCpuAverageSingleCorePercent", "operationP95Ms.item_relocate",
                         "workerTickP99Ms", "workerCpuAverageSingleCorePercent", "workerWorkingSetMaximumBytes",
                         "bytesInPerBotPerSecond", "bytesOutPerBotPerSecond"
                     })
            {
                Assert.True(numbers.TryGetProperty(key, out _), key);
            }

            Assert.Equal(15, numbers.GetProperty("remoteUpdateRateP5Hz").GetDouble(), 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheRemoteCadenceIsJudgedOnTheBotsThatSeeTheLeast()
    {
        var bots = Enumerable.Range(0, 20)
            .Select(index => Cadence(index == 0 ? 6 : 15))
            .ToArray();

        var cadence = StressRemoteCadenceSummary.Create(bots)!;

        Assert.Equal(20, cadence.Bots);
        Assert.Equal(6, cadence.P5Hz, 3);
        Assert.Equal(6, cadence.MinimumHz, 3);
        Assert.Equal((6 + (19 * 15)) / 20d, cadence.AverageHz, 3);
    }

    private static StressQualityBoundary Boundary() => StressQualityBoundary.Load(BoundaryPath);

    private static SimulationBotRemoteCadenceSummary Cadence(double hz)
    {
        return new SimulationBotRemoteCadenceSummary(
            9,
            (long)(hz * 40 * 9),
            40 * 9,
            0,
            Enumerable.Range(0, 9).Select(_ => new SimulationBotEntityCadence((long)(hz * 40), 40)).ToArray());
    }

    // The worker's Mob numbers fold like the other levels and counters of the steady interval.
    [Fact]
    public void TheMobNumbersOfTheWorkerFoldAsLevelsAndCounters()
    {
        StressWorkerInterval[] intervals =
        [
            Interval(RunStart.AddSeconds(20), tickP99: 4) with { Mobs = 300, EngagedMobs = 20, MovingMobs = 30, MobPathQueue = 0, MobPathPlans = 50 },
            Interval(RunStart.AddSeconds(30), tickP99: 4) with { Mobs = 296, EngagedMobs = 40, MovingMobs = 50, MobPathQueue = 2, MobPathPlans = 70 },
            Interval(RunStart.AddSeconds(40), tickP99: 4) with { Mobs = 300, EngagedMobs = 30, MovingMobs = 40, MobPathQueue = 0, MobPathPlans = 60 },
            Interval(RunStart.AddSeconds(50), tickP99: 4) with { Mobs = 300, EngagedMobs = 30, MovingMobs = 40, MobPathQueue = 0, MobPathPlans = 20 }
        ];

        var fold = StressWorkerPerformance.Fold(intervals, RunStart, RunEnd, SteadyStart, SteadyEnd);

        Assert.Equal(299, fold.Levels["mobs"].Average);
        Assert.Equal(40, fold.Levels["engagedMobs"].Worst);
        Assert.Equal(30, fold.Levels["engagedMobs"].Average);
        Assert.Equal(50, fold.Levels["movingMobs"].Worst);
        Assert.Equal(2, fold.Levels["mobPathQueue"].Worst);
        Assert.Equal(200, fold.Counters["mobPathPlans"].Total);
        Assert.Equal(70, fold.Counters["mobPathPlans"].WorstInterval);
    }

    // A line of the worker's performance file carries the Mob numbers by their JSON names.
    [Fact]
    public void AWorkerLineWithMobNumbersParses()
    {
        var line = System.Text.Json.JsonSerializer.Serialize(
            Interval(RunStart, tickP99: 4) with { Mobs = 12, EngagedMobs = 3, MovingMobs = 4, MobPathQueue = 1, MobPathPlans = 9 },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        var interval = Assert.Single(StressWorkerPerformance.Parse([line]));

        Assert.Contains("\"engagedMobs\":3", line, StringComparison.Ordinal);
        Assert.Equal(12, interval.Mobs);
        Assert.Equal(9, interval.MobPathPlans);
    }

    private static StressWorkerInterval Interval(
        DateTime start,
        double tickP99,
        long resynchronizations = 0,
        long drops = 0,
        long quota = 0,
        double cpu = 30)
    {
        return new StressWorkerInterval(
            start, start.AddSeconds(10), 10, 10, 0, 10, 0, 11, cpu / 16, cpu, 100_000_000,
            new StressWorkerDuration(300, 1.5, Math.Min(tickP99, 4), tickP99, tickP99 + 1),
            null, 0, resynchronizations,
            null, null, null, null, null, null, null, null,
            0, 0, 0, 0, 0,
            drops, 0, 0, 1350, 150, 150, 1500, quota,
            3000, 90_000, 3100, 400_000, 350_000, 0, 0, 0, 0, 1_000_000, 1, 0, 0, 5000, 100, 4000);
    }

    private static StressRunReport FullStackReport(double tickP99 = 4)
    {
        var latency = new StressLatencySummary(1000, 10, 45, 80, 100, 120, 50);
        var operation = new StressGameplayOperationSummary(
            100,
            98,
            2,
            0,
            0,
            new StressLatencySummary(100, 20, 150, 300, 400, 500, 160),
            new Dictionary<string, long> { ["success"] = 98 });
        var intervals = Enumerable.Range(0, 7)
            .Select(index => Interval(RunStart.AddSeconds(index * 10), index is >= 2 and <= 5 ? tickP99 : 4))
            .ToArray();
        return new StressRunReport(
            RunStart,
            RunEnd,
            70,
            new StressRunConfiguration(
                "full-stack", "mixed-gameplay", "http://127.0.0.1:5500/", "127.0.0.1", 27025, "worker", "fleet", "node",
                "local-shard-2", "development-world-1", 10, 1, 10, 0, 5, 40, 3, 4, 1337),
            null,
            null,
            null,
            null,
            new StressPostgresSummary(
                "db", 10, 1, 13, 1, 5, 3, 0, 10, 0, 1000, 10, 40, 1000, 0, 100, 100, 0, 0, 0, 0, 0, 0,
                new StressItemInvariantSummary(1210, 1210, 10, 10, 0, 0, 0)),
            new StressFullStackSummary(
                "run", 10, 1, 10, 10, 10, 10, 10, null,
                new Dictionary<string, StressHttpOperationSummary>
                {
                    ["account_login"] = new(10, 10, 0, latency, new Dictionary<int, long> { [200] = 10 }, new Dictionary<string, long>())
                }),
            new StressGameplaySummary(
                "mixed-gameplay",
                null,
                0,
                new Dictionary<string, StressGameplayOperationSummary> { ["item_relocate"] = operation }),
            new StressBotAggregate(
                10, 10, 10, 0, 0, 1000, 1000, 1000, 1000, 1000, 0, 10, 10, 0, 100, latency, latency,
                new Dictionary<string, int>(),
                StressRemoteCadenceSummary.Create(Enumerable.Range(0, 10).Select(_ => Cadence(15)))),
            null)
        {
            SteadyStartedAt = SteadyStart,
            SteadyEndedAt = SteadyEnd,
            ServiceLogs = new StressServiceLogSummary(
                [new StressServiceLogFile("simulation-worker", "simulation-worker.log", 100, 1, 0, 0)],
                [new StressLogClass("simulation-worker", StressServiceLogs.Warning, "SimulationWorker", "Development admin tools are enabled", 1, false, null)]),
            WorkerPerformance = StressWorkerPerformance.Fold(intervals, RunStart, RunEnd, SteadyStart, SteadyEnd),
            GeneratorTiming = new StressGeneratorTimingSummary(
                3,
                new StressLatencySummary(13_000, 0, 0.4, 1.2, 2.5, 9, 0.5),
                new StressLatencySummary(500, 0, 0.1, 2, 5, 12, 0.6),
                PlansRoutes: true)
        };
    }

    private static StressRunReport Break(StressRunReport report, string rule)
    {
        var worker = report.WorkerPerformance!;
        return rule switch
        {
            "simulation_tick_p99_ms" => FullStackReport(tickP99: 50),
            "fixed_tick_resynchronizations" => report with
            {
                WorkerPerformance = worker with
                {
                    Counters = new Dictionary<string, StressWorkerCounterFold>(worker.Counters)
                    {
                        ["tickResynchronizations"] = new(1, 1)
                    }
                }
            },
            "input_acknowledgement_p99_ms" => report with
            {
                Bots = report.Bots with
                {
                    InputAcknowledgementLatency = report.Bots.InputAcknowledgementLatency! with { P99Ms = 151 }
                }
            },
            "remote_update_rate_p5_hz" => report with
            {
                Bots = report.Bots with
                {
                    RemoteCadence = StressRemoteCadenceSummary.Create(Enumerable.Range(0, 10).Select(index => Cadence(index == 0 ? 8 : 15)))
                }
            },
            "gameplay_operation_p95_ms" => report with
            {
                Gameplay = report.Gameplay with
                {
                    Operations = new Dictionary<string, StressGameplayOperationSummary>
                    {
                        ["item_relocate"] = report.Gameplay.Operations["item_relocate"] with
                        {
                            Latency = report.Gameplay.Operations["item_relocate"].Latency! with { P95Ms = 1001 }
                        }
                    }
                }
            },
            "bot_failures" => report with
            {
                Bots = report.Bots with
                {
                    CompletedBots = 9,
                    FailedBots = 1,
                    FailureCodes = new Dictionary<string, int> { ["connection_closed"] = 1 }
                }
            },
            "gameplay_unexpected_failures" => report with
            {
                Gameplay = report.Gameplay with { InventoryRefreshFailures = 1 }
            },
            "http_unexpected_failures" => report with
            {
                FullStack = report.FullStack! with
                {
                    Operations = new Dictionary<string, StressHttpOperationSummary>
                    {
                        ["account_login"] = report.FullStack.Operations["account_login"] with { Failures = 1 }
                    }
                }
            },
            "quota_rejections" => report with { WorkerPerformance = worker with { RunQuotaRejections = 1 } },
            "snapshot_drops" => report with { WorkerPerformance = worker with { RunSnapshotDrops = 1 } },
            "owner_snapshot_gaps" => report with { Bots = report.Bots with { EstimatedMissingSnapshots = 1 } },
            "expired_unreleased_sessions" => report with
            {
                Postgres = report.Postgres! with { MaximumExpiredUnreleasedSimulationSessions = 1 }
            },
            "deadlocks" => report with { Postgres = report.Postgres! with { Deadlocks = 1 } },
            "item_invariant_violations" => report with
            {
                Postgres = report.Postgres! with
                {
                    ItemInvariants = report.Postgres.ItemInvariants! with { PendingItemOperations = 1 }
                }
            },
            "process_memory_growth" => report with
            {
                Configuration = report.Configuration with { SteadyDurationSeconds = 660 },
                SteadyEndedAt = SteadyStart.AddMinutes(11),
                Soak = new StressSoakSummary(
                    null,
                    null,
                    [
                        new StressMemoryTrend("worker", 660, 200, [100, 100, 110, 120, 140]),
                        new StressMemoryTrend("gameService", 660, 0, [200, 200, 200, 200, 200]),
                        new StressMemoryTrend("generator", 660, 0, [300, 300, 300, 300, 300])
                    ])
            },
            "service_log_errors" => report with
            {
                ServiceLogs = report.ServiceLogs! with
                {
                    Classes =
                    [
                        .. report.ServiceLogs.Classes,
                        new StressLogClass("game-service", StressServiceLogs.Error, "Mmorpg.GameService.Items.ItemTransactionService", "Item transaction relocate <id> failed unexpectedly", 1, false, null)
                    ]
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(rule))
        };
    }
}
