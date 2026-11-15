using System.Text.Json;
using System.Text.Json.Nodes;
using Mmorpg.Tools.StackStressGenerator;

namespace Mmorpg.Tools.Tests;

// The GameService outage of a load run: what the bots' answers mean against the window that
// tools/Test-Load.ps1 measured, and what the timed service logs say about it.
public sealed class StressGameServiceOutageTests : IDisposable
{
    private static readonly DateTime Stopped = new(2026, 11, 15, 12, 1, 0, DateTimeKind.Utc);
    private static readonly DateTime Ready = Stopped.AddSeconds(62);

    private readonly string folder = Path.Combine(Path.GetTempPath(), $"outage-{Guid.NewGuid():N}");

    public StressGameServiceOutageTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    // Inside the window the worker's refusals, the failures of the calls it cut off, the failed
    // item reads, and the failed requests are the outage's own; the same answers outside it are
    // not, except a refusal between the bot's own notices.
    [Fact]
    public void TheWindowAndTheBotsNoticesDecideWhichAnswersTheOutageExplains()
    {
        var tracker = Tracker();
        WriteWindow();
        var bot = tracker.Register(1);
        bot.OnJoined(Stopped.AddMinutes(-2));
        bot.RecordFailure("item_move", StressOutageCodes.Timeout, Stopped.AddSeconds(3), 2100);
        bot.OnNotice(available: false);
        bot.RecordFailure("vendor_sale", StressOutageCodes.Unavailable, Stopped.AddSeconds(10), 20);
        bot.RecordFailure(StressOutageCodes.InventoryRefresh, StressOutageCodes.InventoryRefreshFailed, Stopped.AddSeconds(20));
        // Between GameService's readiness and the worker's return the worker still refuses.
        bot.RecordFailure("loot_all", StressOutageCodes.Unavailable, Ready.AddSeconds(1));
        bot.OnNotice(available: true);
        bot.RecordFailure("item_move", StressOutageCodes.Unavailable, Ready.AddSeconds(30));
        bot.RecordFailure("item_move", StressOutageCodes.NoAnswer, Stopped.AddSeconds(-30));

        var summary = tracker.Capture(
            [Snapshot(1)],
            [Stopped.AddSeconds(20), Stopped.AddSeconds(-5), Ready.AddSeconds(5)]);

        Assert.Null(summary.WindowProblem);
        Assert.Equal(62, summary.MeasuredSeconds);
        Assert.Equal(2, summary.ExpectedRefusals);
        Assert.Equal(1, summary.ExpectedInFlightFailures);
        Assert.Equal(1, summary.ExplainedInventoryRefreshFailures);
        Assert.Equal(1, summary.ExplainedHttpFailures);
        Assert.Equal(2, summary.UnexplainedFailures);
        Assert.Equal(
            new Dictionary<string, long>
            {
                ["item_move auth_service_unavailable"] = 1,
                ["item_move game_service_unavailable"] = 1
            },
            summary.UnexplainedCodes);
        Assert.Equal((1, 1), (summary.BotsAcrossOutage, summary.BotsWithOneNoticeEach));
        // The answers the outage explains keep their time apart from the operations' latencies.
        Assert.Equal((2, 2100d), (summary.ExpectedAnswerLatency!.Samples, summary.ExpectedAnswerLatency.MaximumMs));
    }

    // A bot joined across the outage hears one notice of each kind; a bot that joined after the
    // stop or failed is not judged, and the notes name the bots that heard another number.
    [Fact]
    public void EveryBotJoinedAcrossTheOutageMustHearOneNoticeOfEachKind()
    {
        var tracker = Tracker();
        WriteWindow();
        var exact = tracker.Register(1);
        var twice = tracker.Register(2);
        var late = tracker.Register(3);
        var failed = tracker.Register(4);
        foreach (var bot in new[] { exact, twice, failed })
        {
            bot.OnJoined(Stopped.AddMinutes(-1));
        }

        late.OnJoined(Stopped.AddSeconds(30));
        foreach (var bot in new[] { exact, twice, late, failed })
        {
            bot.OnNotice(available: false);
            bot.OnNotice(available: true);
        }

        twice.OnNotice(available: false);
        twice.OnNotice(available: true);

        var summary = tracker.Capture(
            [Snapshot(1), Snapshot(2), Snapshot(3), Snapshot(4, StressBotState.Failed)],
            []);

        Assert.Equal(2, summary.BotsAcrossOutage);
        Assert.Equal(1, summary.BotsWithOneNoticeEach);
        Assert.Equal(["bot 2: 2 unavailable, 2 available"], summary.OtherNotices);
        Assert.Equal((5, 5), (summary.UnavailableNotices, summary.AvailableNotices));
    }

    // A kill while saving was paused waits for its Mob's corpse near the place it died; the first
    // durable success after the bot's available notice measures the return.
    [Fact]
    public void KillsWhileSavingWasPausedNeedTheirCorpsesAndTheFirstDurableSuccessCounts()
    {
        var tracker = Tracker();
        WriteWindow();
        var bot = tracker.Register(1);
        bot.OnJoined(Stopped.AddMinutes(-1));
        bot.RecordKill("mob.grey_wolf", 10, 10, Stopped.AddSeconds(5));
        bot.OnNotice(available: false);
        bot.RecordDurableSuccess(Stopped.AddSeconds(4));
        bot.RecordKill("mob.grey_wolf", 10, 10, Stopped.AddSeconds(5));
        bot.RecordKill("mob.feral_wolf", 40, 40, Stopped.AddSeconds(6));
        bot.RecordKill(null, 0, 0, Stopped.AddSeconds(7));
        bot.ObserveMobCorpses(
            [(Guid.NewGuid(), "mob.grey_wolf", 12, 11), (Guid.NewGuid(), "mob.feral_wolf", 60, 60)],
            Stopped.AddSeconds(8));
        Assert.True(bot.HasPendingKills);
        bot.ObserveMobCorpses(
            [(Guid.NewGuid(), "mob.feral_wolf", 60, 60)],
            Stopped.AddSeconds(6 + StressOutageBot.KillCorpseWait.TotalSeconds));
        bot.OnNotice(available: true);
        bot.RecordDurableSuccess(Ready.AddSeconds(3));
        bot.RecordDurableSuccess(Ready.AddSeconds(9));

        var summary = tracker.Capture([Snapshot(1)], []);

        Assert.False(bot.HasPendingKills);
        Assert.Equal((3, 1, 1), (summary.KillsWhileSavingPaused, summary.KillCorpsesSeen, summary.KillCorpsesMissing));
        Assert.Equal(3, summary.FirstDurableSuccessAfterReadySeconds);
        Assert.Equal(1, summary.BotsWithDurableSuccessAfterReturn);
    }

    // Two wolves of one kind die side by side while saving is paused, and one corpse appears:
    // it stands for one kill, and the other counts as missing. A corpse of the same kind that
    // the bot saw there before the kills stands for none of them.
    [Fact]
    public void ACorpseStandsForOneKillAndOnlyWhenItAppearedAfterTheKill()
    {
        var tracker = Tracker();
        WriteWindow();
        var bot = tracker.Register(1);
        bot.OnJoined(Stopped.AddMinutes(-1));
        var olderCorpse = Guid.NewGuid();
        var newCorpse = Guid.NewGuid();
        bot.ObserveMobCorpses([(olderCorpse, "mob.grey_wolf", 10, 10)], Stopped.AddSeconds(-30));
        bot.OnNotice(available: false);
        bot.RecordKill("mob.grey_wolf", 10, 10, Stopped.AddSeconds(5));
        bot.RecordKill("mob.grey_wolf", 11, 10, Stopped.AddSeconds(6));

        bot.ObserveMobCorpses(
            [(olderCorpse, "mob.grey_wolf", 10, 10), (newCorpse, "mob.grey_wolf", 10, 10)],
            Stopped.AddSeconds(7));
        Assert.True(bot.HasPendingKills);
        bot.ObserveMobCorpses(
            [(olderCorpse, "mob.grey_wolf", 10, 10), (newCorpse, "mob.grey_wolf", 10, 10)],
            Stopped.AddSeconds(6 + StressOutageBot.KillCorpseWait.TotalSeconds));
        bot.OnNotice(available: true);

        var summary = tracker.Capture([Snapshot(1)], []);

        Assert.False(bot.HasPendingKills);
        Assert.Equal((2, 1, 1), (summary.KillsWhileSavingPaused, summary.KillCorpsesSeen, summary.KillCorpsesMissing));
    }

    // A bot that commits a durable action after its available notice but before the script's
    // readiness poll saw GameService recovered in zero seconds; that is no missing recovery.
    [Fact]
    public void ADurableSuccessBeforeTheRecordedReadinessIsARecoveryOfZeroSeconds()
    {
        var tracker = Tracker();
        WriteWindow();
        var bot = tracker.Register(1);
        bot.OnJoined(Stopped.AddMinutes(-1));
        bot.OnNotice(available: false);
        bot.OnNotice(available: true);
        bot.RecordDurableSuccess(Ready.AddMilliseconds(-300));

        var summary = tracker.Capture([Snapshot(1)], []);

        Assert.Equal(0, summary.FirstDurableSuccessAfterReadySeconds);
        Assert.Equal(1, summary.BotsWithDurableSuccessAfterReturn);
    }

    [Fact]
    public void AMissingWindowIsReportedAndExplainsNothing()
    {
        var tracker = Tracker();
        var bot = tracker.Register(1);
        bot.OnJoined(Stopped.AddMinutes(-1));
        bot.RecordFailure("item_move", StressOutageCodes.Timeout, Stopped.AddSeconds(3));

        var summary = tracker.Capture([Snapshot(1)], [Stopped.AddSeconds(3)]);

        Assert.Contains("no outage window", summary.WindowProblem, StringComparison.Ordinal);
        Assert.Equal(1, summary.UnexplainedFailures);
        Assert.Equal(0, summary.ExplainedHttpFailures);
        Assert.Equal(0, summary.BotsAcrossOutage);
    }

    // The request appears once the outage's time in the steady interval came, and only once.
    [Fact]
    public void TheRequestIsWrittenOnceItsTimeCame()
    {
        var tracker = Tracker();

        Assert.False(tracker.RequestIfDue(TimeSpan.FromSeconds(59)));
        Assert.False(File.Exists(tracker.Options.RequestPath));
        Assert.True(tracker.RequestIfDue(TimeSpan.FromSeconds(60)));
        Assert.False(tracker.RequestIfDue(TimeSpan.FromSeconds(61)));
        using var request = JsonDocument.Parse(File.ReadAllText(tracker.Options.RequestPath));
        Assert.True(request.RootElement.TryGetProperty("requestedAt", out _));
    }

    // The timed log lines inside the outage's window that an allowance for the outage names are
    // the outage's own: they count apart from the rules, and an error line there is allowed. The
    // same lines outside the window, and an untimed log, count as in any run.
    [Fact]
    public void TheTimedLogsSeparateTheOutagesOwnLinesAndFindTheWorkersNotices()
    {
        var worker = Path.Combine(folder, "simulation-worker.log");
        File.WriteAllLines(worker,
        [
            Line(Stopped.AddSeconds(-20), "warn", "Mmorpg.SimulationWorker.Registry.SimulationWorkerHeartbeatService", "Simulation worker registry heartbeat failed for local-simulation-worker-2: auth_service_timeout."),
            Line(Stopped.AddSeconds(4), "warn", "Mmorpg.SimulationWorker.Registry.SimulationWorkerHeartbeatService", "Simulation worker registry heartbeat failed for local-simulation-worker-2: auth_service_unavailable."),
            Line(Stopped.AddSeconds(4.5), "warn", "Mmorpg.SimulationWorker.GameServiceApi.GameServiceAvailability", "GameService does not answer the registry heartbeat (auth_service_unavailable); items, trade, and quests are paused until it answers again."),
            Line(Stopped.AddSeconds(5), "fail", "Mmorpg.SimulationWorker.Realtime.ItemAuthorityOperations", "Item operation 3f2c1d4e-0000-4000-8000-000000000001 failed with auth_service_timeout."),
            Line(Ready.AddSeconds(1.5), "info", "Mmorpg.SimulationWorker.GameServiceApi.GameServiceAvailability", "GameService answers the registry heartbeat again after 00:01:03; durable actions resume."),
            Line(Ready.AddSeconds(30), "fail", "Mmorpg.SimulationWorker.Realtime.ItemAuthorityOperations", "Item operation 3f2c1d4e-0000-4000-8000-000000000002 failed with auth_service_timeout.")
        ]);
        var allowances = new[]
        {
            new StressLogAllowance("simulation-worker", StressServiceLogs.Warning, "Mmorpg.SimulationWorker.Registry.SimulationWorkerHeartbeatService", "Simulation worker registry heartbeat failed", "The registry heartbeat fails while GameService is stopped.") { DuringOutage = true },
            new StressLogAllowance("simulation-worker", StressServiceLogs.Error, "Mmorpg.SimulationWorker.Realtime.ItemAuthorityOperations", "Item operation <id> failed with auth_service_timeout", "A call that was on its way when GameService stopped times out.") { DuringOutage = true }
        };

        var logs = StressServiceLogs.Read([("simulation-worker", worker, false)], allowances, (Stopped, Ready.AddSeconds(5)));
        var untimed = StressServiceLogs.Read([("simulation-worker", worker, false)], allowances);

        Assert.Equal((1, 1), (logs.WorkerHeartbeatFailures, logs.WorkerHeartbeatFailuresDuringOutage));
        // The heartbeat line before the stop names auth_service_timeout too.
        Assert.Equal((2, 1), (logs.AuthServiceTimeouts, logs.AuthServiceTimeoutsDuringOutage));
        Assert.Equal(1, logs.UnallowedErrors);
        Assert.Equal(Stopped.AddSeconds(4.5), logs.WorkerOutageNoticedAt);
        Assert.Equal(Ready.AddSeconds(1.5), logs.WorkerReturnNoticedAt);
        Assert.Equal((2, 0, 2), (untimed.WorkerHeartbeatFailures, untimed.WorkerHeartbeatFailuresDuringOutage, untimed.UnallowedErrors));
        Assert.Null(untimed.WorkerReturnNoticedAt);
    }

    // The judge adds when the worker noticed the outage and the return to the outage summary.
    [Fact]
    public void TheJudgeAddsTheWorkersNoticesToTheOutage()
    {
        File.WriteAllLines(Path.Combine(folder, "simulation-worker.log"),
        [
            Line(Stopped.AddSeconds(4.5), "warn", "Mmorpg.SimulationWorker.GameServiceApi.GameServiceAvailability", "GameService does not answer the registry heartbeat (auth_service_unavailable); items, trade, and quests are paused until it answers again."),
            Line(Ready.AddSeconds(1.5), "info", "Mmorpg.SimulationWorker.GameServiceApi.GameServiceAvailability", "GameService answers the registry heartbeat again after 00:01:03; durable actions resume.")
        ]);
        File.WriteAllLines(Path.Combine(folder, "game-service-2.log"),
        [
            Line(Ready.AddSeconds(-3), "fail", "Microsoft.Extensions.Hosting.Internal.Host", "A line of the restarted GameService.")
        ]);
        var report = StressRunReport.Parse("""{"configuration":{"mode":"full-stack","workload":"playing"},"gameplay":{"operations":{}},"bots":{"failureCodes":{}}}""") with
        {
            Outage = new StressOutageSummary(60, 60, null, Stopped, Stopped.AddSeconds(60), Ready, null)
        };

        var judged = StressRunJudge.AddLogs(report, folder, [], 5);

        Assert.Equal(4.5, judged.Outage!.WorkerNoticedOutageSeconds!.Value, 3);
        Assert.Equal(1.5, judged.Outage.WorkerNoticedReturnSeconds!.Value, 3);
        Assert.Null(judged.Outage.WorkerLogNote);
        Assert.Contains(judged.ServiceLogs!.Files, file => file.Path.EndsWith("game-service-2.log", StringComparison.Ordinal));
        Assert.Equal(1, judged.ServiceLogs.UnallowedErrors);
    }

    // Only an allowance for the outage may name warning lines.
    [Theory]
    [InlineData("true", null)]
    [InlineData("false", "only error and critical lines are judged")]
    [InlineData("\"yes\"", "must be true or false")]
    public void OnlyAnAllowanceForTheOutageMayNameWarnings(string duringOutage, string? expected)
    {
        var path = Path.Combine(RepositoryRoot(), "tools", "load", "quality-boundary.json");
        var boundaryJson = JsonNode.Parse(File.ReadAllText(path))!;
        var allow = boundaryJson["rules"]!.AsArray().Single(rule => rule!["rule"]!.GetValue<string>() == "service_log_errors")!["allow"]!.AsArray();
        allow.Add(JsonNode.Parse("{ \"service\": \"simulation-worker\", \"level\": \"warning\", \"category\": \"c\", \"message\": \"m\", \"reason\": \"r\", \"duringOutage\": " + duringOutage + " }"));
        var json = boundaryJson.ToJsonString();

        if (expected is null)
        {
            var boundary = StressQualityBoundary.Parse(json, "boundary");
            Assert.Contains(boundary.LogAllowances, allowance => allowance is { DuringOutage: true, Level: StressServiceLogs.Warning });
            return;
        }

        var exception = Assert.Throws<StressGeneratorOptionException>(() => StressQualityBoundary.Parse(json, "boundary"));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    private StressOutageTracker Tracker()
    {
        return new StressOutageTracker(new StressOutageOptions(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), folder));
    }

    private void WriteWindow()
    {
        File.WriteAllText(
            Path.Combine(folder, StressOutageOptions.WindowFileName),
            JsonSerializer.Serialize(new
            {
                requestedAt = Stopped.AddSeconds(-1).ToString("O"),
                stoppedAt = Stopped.ToString("O"),
                exitedAt = Stopped.AddSeconds(2).ToString("O"),
                restartedAt = Stopped.AddSeconds(60).ToString("O"),
                readyAt = Ready.ToString("O")
            }));
    }

    private static string Line(DateTime at, string level, string category, string message)
    {
        return at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ ", System.Globalization.CultureInfo.InvariantCulture)
            + level + ": " + category + "[0]" + Environment.NewLine + "      " + message;
    }

    private static StressBotSnapshot Snapshot(int botIndex, StressBotState state = StressBotState.Completed)
    {
        return new StressBotSnapshot(botIndex, $"bot-{botIndex}", state, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null);
    }

    private static string RepositoryRoot() => Mmorpg.Testing.RepositoryPaths.Root;
}
