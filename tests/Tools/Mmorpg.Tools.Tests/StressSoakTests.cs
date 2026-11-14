using System.Diagnostics;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Testing;
using Mmorpg.Tools.SimulationBotClient;
using Mmorpg.Tools.StackStressGenerator;
using static Mmorpg.Tools.Tests.PlayingTestWorld;

namespace Mmorpg.Tools.Tests;

// What a soak adds: the churn's schedule, safety, and rejoin check, the growth of the tables,
// the memory trend and its rule, the service log classes and their rule, and the report that
// the judge reads back.
public sealed class StressSoakTests
{
    private static readonly int[] Bots = Enumerable.Range(1, 20).ToArray();
    private static readonly DateTime LeftAt = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SimulationVector3 Spot = new(10f, 2f, -30f);
    private static readonly Guid Session = Guid.Parse("8c1f9e0d-0000-4000-8000-000000000001");

    private static string BoundaryPath => Path.Combine(RepositoryPaths.Root, "tools", "load", "quality-boundary.json");

    [Fact]
    public void TheChurnScheduleIsTheSameForTheSameSeedAndAnotherForAnother()
    {
        var settings = new PlayingChurnSettings(10);
        var steady = TimeSpan.FromMinutes(10);

        var first = PlayingChurnSchedule.Create(settings, Bots, steady, 1337);
        var again = PlayingChurnSchedule.Create(settings, Bots.Reverse().ToArray(), steady, 1337);
        var other = PlayingChurnSchedule.Create(settings, Bots, steady, 7);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        // Two of 20 bots a minute over the ten minutes without the quiet tail, 525 seconds; the
        // jitter decides whether the last one falls inside.
        Assert.InRange(first.Count, 17, 18);
        Assert.All(first, churn => Assert.True(churn.Offset < steady - PlayingChurnSchedule.QuietTail));
        Assert.Equal(first.OrderBy(churn => churn.Offset), first);
    }

    [Fact]
    public void TheChurnTakesEveryBotOnceBeforeAnyTwiceAndCoversEveryKindOfReturn()
    {
        var schedule = PlayingChurnSchedule.Create(new PlayingChurnSettings(20), Bots, TimeSpan.FromMinutes(30), 1337);

        Assert.Equal(Bots.Length, schedule.Take(Bots.Length).Select(churn => churn.BotIndex).Distinct().Count());
        for (var start = 0; start + 4 <= schedule.Count; start += 4)
        {
            var block = schedule.Skip(start).Take(4).Select(churn => (churn.Kind, churn.Logout)).ToHashSet();
            Assert.Equal(4, block.Count);
        }

        Assert.All(schedule.Where(churn => churn.Kind == PlayingChurnKind.Body), churn =>
            Assert.InRange(churn.Delay, PlayingChurnSchedule.MinimumBodyDelay, PlayingChurnSchedule.MaximumBodyDelay));
        Assert.All(schedule.Where(churn => churn.Kind == PlayingChurnKind.Saved), churn =>
            Assert.InRange(churn.Delay, PlayingChurnSchedule.MinimumSavedDelay, PlayingChurnSchedule.MaximumSavedDelay));
    }

    [Fact]
    public void AShortSteadyIntervalHasNoChurnAfterItsQuietTail()
    {
        Assert.Empty(PlayingChurnSchedule.Create(new PlayingChurnSettings(50), Bots, PlayingChurnSchedule.QuietTail, 1));
        Assert.Empty(PlayingChurnSchedule.Create(new PlayingChurnSettings(50), [], TimeSpan.FromMinutes(10), 1));
    }

    [Theory]
    [InlineData("{ \"percentPerMinute\": 10 }", null)]
    [InlineData("{ \"percentPerMinute\": 0 }", "between 1 and 50")]
    [InlineData("{ \"percentPerMinute\": 10, \"kind\": \"body\" }", "unknown property 'kind'")]
    [InlineData("{ }", "needs 'percentPerMinute'")]
    public void TheChurnSectionIsReadStrictly(string churn, string? error)
    {
        var json = $$"""
            {
              "roles": { "walker": 100 },
              "grounds": [ { "name": "road", "kind": "walking", "minimumX": 0, "minimumZ": 0, "maximumX": 10, "maximumZ": 10 } ],
              "churn": {{churn}}
            }
            """;
        using var document = System.Text.Json.JsonDocument.Parse(json);

        if (error is null)
        {
            Assert.Equal(10, PlayingScenario.Parse(document.RootElement, "test").Churn!.PercentPerMinute);
            return;
        }

        var exception = Assert.Throws<StressGeneratorOptionException>(() => PlayingScenario.Parse(document.RootElement, "test"));
        Assert.Contains(error, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSoakScenariosChurnTheReferenceMix()
    {
        var ten = StressLoadScenario.Load(Path.Combine(RepositoryPaths.Root, "tools", "load", "scenarios", "soak-10min.json"));
        var thirty = StressLoadScenario.Load(Path.Combine(RepositoryPaths.Root, "tools", "load", "scenarios", "soak-30min.json"));
        var reference = StressLoadScenario.Load(Path.Combine(RepositoryPaths.Root, "tools", "load", "scenarios", "playing-250.json"));

        Assert.Equal((100, 600), (ten.Generator["bots"], ten.Generator["duration-seconds"]));
        Assert.Equal((250, 1800), (thirty.Generator["bots"], thirty.Generator["duration-seconds"]));
        foreach (var soak in new[] { ten, thirty })
        {
            Assert.NotNull(soak.Playing!.Churn);
            Assert.Equal(reference.Playing!.RoleShares, soak.Playing.RoleShares);
            Assert.Equal(reference.Playing.Grounds, soak.Playing.Grounds);
            Assert.Equal(reference.WorldVariant!.Areas, soak.WorldVariant!.Areas);
            Assert.Equal(reference.WorldVariant.Groups, soak.WorldVariant.Groups);
        }
    }

    [Fact]
    public void ABodyTakenOverStandsWhereItLeftWithAtMostWhatItRegenerated()
    {
        var leave = Leave(health: 60);

        Assert.True(Judge(leave, Rejoin(reconnect: true, health: 62, after: 5)).Passed);
        // A hit while the body stood is possible and invisible to the bot.
        Assert.True(Judge(leave, Rejoin(reconnect: true, health: 40, after: 5)).Passed);
        Assert.Equal("rejoin_place_mismatch", Judge(leave, Rejoin(reconnect: true, health: 62, after: 5, x: 11f)).Code);
        Assert.Equal("rejoin_health_mismatch", Judge(leave, Rejoin(reconnect: true, health: 100, after: 5)).Code);
    }

    [Fact]
    public void ACharacterFromItsSavedPlaceComesBackAsItLeftPlusWhatItsBodyRegenerated()
    {
        var leave = Leave(health: 60);
        var restored = Judge(leave, Rejoin(reconnect: false, health: 80, after: 30), Facts(health: 80, standing: 20.4));

        Assert.True(restored.Passed, restored.Detail);
        Assert.Equal(PlayingRejoinCheck.Restored, restored.Outcome);
    }

    [Fact]
    public void LostHealthWithoutAHitFailsAndWithALateReleaseCountsAsAHit()
    {
        var leave = Leave(health: 60);

        var lost = Judge(leave, Rejoin(reconnect: false, health: 50, after: 30), Facts(health: 50, standing: 20.4));
        var hit = Judge(leave, Rejoin(reconnect: false, health: 50, after: 40), Facts(health: 50, standing: 31));

        Assert.False(lost.Passed);
        Assert.Equal("rejoin_health_mismatch", lost.Code);
        Assert.Contains("lost health", lost.Detail, StringComparison.Ordinal);
        Assert.True(hit.Passed, hit.Detail);
        Assert.True(hit.BodyWasHit);
    }

    [Fact]
    public void TheSavedPlaceMustBeWhereTheBotLeftAndTheJoinMustRestoreIt()
    {
        var leave = Leave(health: 60);

        Assert.Equal(
            "rejoin_place_mismatch",
            Judge(leave, Rejoin(reconnect: false, health: 80, after: 30, x: 0f), Facts(health: 80, standing: 20.4, x: 0f)).Code);
        Assert.Equal(
            "rejoin_restore_mismatch",
            Judge(leave, Rejoin(reconnect: false, health: 100, after: 30), Facts(health: 80, standing: 20.4)).Code);
        Assert.Equal(
            "rejoin_health_mismatch",
            Judge(leave, Rejoin(reconnect: false, health: 100, after: 30), Facts(health: 100, standing: 20.4)).Code);
        Assert.Equal(
            "rejoin_state_not_saved",
            Judge(leave, Rejoin(reconnect: false, health: 80, after: 30), Facts(health: 80, standing: 20.4) with
            {
                Saved = new PlayingSavedState(Spot, 80, 100, Guid.NewGuid())
            }).Code);
        Assert.Equal("rejoin_state_not_saved", Judge(leave, Rejoin(reconnect: false, health: 80, after: 30), null).Code);
    }

    [Fact]
    public void ABodyThatDiedBringsTheCharacterBackWithFullHealth()
    {
        var leave = Leave(health: 60);
        var died = Facts(health: 100, standing: 12, x: 0f) with { LastDeathAt = LeftAt.AddSeconds(11) };

        var verdict = Judge(leave, Rejoin(reconnect: false, health: 100, after: 30, x: 0f), died);
        var wounded = Judge(
            leave,
            Rejoin(reconnect: false, health: 70, after: 30, x: 0f),
            died with { Saved = died.Saved! with { Health = 70 } });

        Assert.True(verdict.Passed, verdict.Detail);
        Assert.True(verdict.BodyDied);
        Assert.Equal("rejoin_health_mismatch", wounded.Code);
    }

    [Fact]
    public void ABotLeavesOnlyCalmAndAwayFromAggressiveMobsAndWalksAwayFromThemFirst()
    {
        var body = new FakePlayingBody(SwordKit);
        body.View.ApplyOwnerCombatState(OwnCombat(1));
        Assert.Equal(PlayingLeaveReadiness.Busy, PlayingLeaveSafety.Evaluate(body, roleSetUp: false));
        Assert.Equal(PlayingLeaveReadiness.Ready, PlayingLeaveSafety.Evaluate(body, roleSetUp: true));

        // A defensive Grey Wolf nearby does not matter; an aggressive Feral Wolf does.
        body.View.ApplyWorldActorSpawn(Mob(100, 0f, -10f));
        body.View.ApplyCombatantState(Combatant(100, 1));
        Assert.Equal(PlayingLeaveReadiness.Ready, PlayingLeaveSafety.Evaluate(body, roleSetUp: true));
        body.View.ApplyWorldActorSpawn(Mob(101, 0f, -6f, "mob.feral_wolf"));
        body.View.ApplyCombatantState(Combatant(101, 1));
        Assert.Equal(PlayingLeaveReadiness.TooCloseToMobs, PlayingLeaveSafety.Evaluate(body, roleSetUp: true));
        var point = PlayingLeaveSafety.RetreatPoint(body)!.Value;
        Assert.True(point.Z < -12f - PlayingLeaveSafety.SafeMobDistance);

        // A Mob after the bot, combat, or an intent in flight keeps it in the game.
        body.View.ApplyCombatantState(Combatant(101, 2, target: OwnEntity));
        Assert.Equal(PlayingLeaveReadiness.Busy, PlayingLeaveSafety.Evaluate(body, roleSetUp: true));
        body.View.ApplyCombatantState(Combatant(101, 3));
        body.View.ApplyOwnerCombatState(new RealtimeOwnerCombatState(
            2, 2, 90, 100, 100, 100, RealtimeCombatLifeState.Alive, 1, 0, true, 0, RealtimeCombatAttackStatus.Idle, 0, 0, Guid.Empty, 0));
        Assert.Equal(PlayingLeaveReadiness.Busy, PlayingLeaveSafety.Evaluate(body, roleSetUp: true));
    }

    [Fact]
    public void ABotStandsOnlyOnTheGroundWithoutSpeed()
    {
        var body = new FakePlayingBody(SwordKit);
        Assert.True(PlayingLeaveSafety.IsStanding(body));

        // A grounded character keeps a small downward velocity that holds it to the ground.
        body.View.ApplyOwnerSnapshot(new RealtimeOwnerSimulationSnapshot(
            1, 2, 1, new RealtimePlayerState(0f, 0f, -12f, 0f, -2f, 0f, 0f, true), new RealtimeMovementPathProgress(0, 0, 0)));
        Assert.True(PlayingLeaveSafety.IsStanding(body));
        body.View.ApplyOwnerSnapshot(new RealtimeOwnerSimulationSnapshot(
            2, 4, 2, new RealtimePlayerState(0f, 0f, -12f, 0f, -2f, 0f, 0f, false), new RealtimeMovementPathProgress(0, 0, 0)));
        Assert.False(PlayingLeaveSafety.IsStanding(body));

        body.View.ApplyOwnerSnapshot(new RealtimeOwnerSimulationSnapshot(
            3, 6, 3, new RealtimePlayerState(0f, 0f, -12f, 4.9f, -2f, 0.5f, 0f, true), new RealtimeMovementPathProgress(1, 0, 0)));

        Assert.False(PlayingLeaveSafety.IsStanding(body));
    }

    // A role that the bot leaves for a while ends what it was in the middle of and decides again.
    [Fact]
    public void ARoleResumedAfterARejoinEndsItsTripAndItsFight()
    {
        var body = new FakePlayingBody(SwordKit);
        body.View.Items.Load(Inventory(4, Worn("weapon.iron_sword", "main_hand")));
        body.View.ApplyOwnerCombatState(OwnCombat(1));
        var fighter = new FighterRole(
            new PlayingGround("field", PlayingGroundKind.Hunting, new PlayingRectangle(-150, -50, -120, -20)),
            teleport: false,
            new Random(5),
            new PlayingGround("town", PlayingGroundKind.Town, new PlayingRectangle(-24, -24, 24, 4)));
        fighter.Think(body, TimeSpan.FromSeconds(1));
        Assert.True(fighter.IsSetUp);
        body.MoveTo(-131.5f, -35f);
        body.View.ApplyWorldActorSpawn(Mob(100, -130f, -35f));
        body.View.ApplyCombatantState(Combatant(100, 1));
        fighter.Think(body, TimeSpan.FromSeconds(2));
        Assert.Equal(PlayingOperations.Attack, body.Pending);
        body.Answer();

        fighter.Resume(TimeSpan.FromSeconds(3));

        Assert.Equal("seek", fighter.State);
        Assert.Equal(0ul, fighter.Target);
        Assert.False(fighter.IsSetUp);
        fighter.Think(body, TimeSpan.FromSeconds(4));
        Assert.True(fighter.IsSetUp);
    }

    [Fact]
    public void TheTablesGrowPerHourAndPerPlayerHourSortedByBytes()
    {
        var start = new StressTableSnapshot(LeftAt, 12, [new("accounts", 100, 64_000), new("item_operations", 1_000, 1_000_000), new("corpses", 10, 8_000)]);
        var end = new StressTableSnapshot(
            LeftAt.AddMinutes(30),
            15,
            [new("accounts", 100, 64_000), new("item_operations", 4_000, 4_000_000), new("corpses", 60, 48_000), new("death_events", 50, 16_000)]);

        var growth = StressStorageGrowthSummary.Create(
            start,
            end,
            new StressRedisSnapshot(LeftAt, 200, 1_000_000),
            new StressRedisSnapshot(LeftAt.AddMinutes(30), 250, 1_500_000),
            players: 100);

        Assert.Equal(0.5, growth.Hours, 6);
        Assert.Equal(["item_operations", "corpses", "death_events", "accounts"], growth.Tables.Select(table => table.Table));
        var operations = growth.Tables[0];
        Assert.Equal(6_000, operations.RowsPerHour, 6);
        Assert.Equal(6_000_000, operations.BytesPerHour, 6);
        Assert.Equal(60, operations.RowsPerPlayerHour, 6);
        Assert.Equal(60_000, operations.BytesPerPlayerHour, 6);
        Assert.Equal((0, 50), (growth.Tables[2].StartRows, growth.Tables[2].EndRows));
        Assert.Equal(100, growth.Redis!.KeysPerHour, 6);
        Assert.Equal(1_000_000, growth.Redis.UsedMemoryBytesPerHour, 6);
    }

    [Fact]
    public void TheTrendIsALeastSquaresSlopeAndTheAveragesOfFiveEqualParts()
    {
        var start = 1_000L;
        var trend = new StressTrendAccumulator(start, TimeSpan.FromSeconds(100), frequency: 1);
        for (var second = 0; second < 100; second++)
        {
            // 36 units per second is 129,600 per hour.
            trend.Add(start + second, 500 + (36 * second));
        }

        Assert.Equal(129_600, trend.SlopePerHour(), 3);
        Assert.Equal([500 + (36 * 9.5), 500 + (36 * 29.5), 500 + (36 * 49.5), 500 + (36 * 69.5), 500 + (36 * 89.5)], trend.Fifths().Select(value => value!.Value));
        Assert.Null(new StressTrendAccumulator(0, TimeSpan.FromSeconds(10)).Fifths()[0]);
    }

    [Fact]
    public void TheWorkerMemoryComesFromItsOwnIntervals()
    {
        var steadyStart = LeftAt;
        var intervals = Enumerable.Range(0, 60)
            .Select(index => WorkerInterval(steadyStart.AddSeconds(index * 10), heapMiB: 100 + index, workingSetMiB: 300))
            .ToArray();

        var trend = StressWorkerMemory.Create(intervals, steadyStart, steadyStart.AddMinutes(10))!;

        Assert.Equal(60, trend.Samples);
        Assert.Equal(0, trend.WorkingSetSlopeMiBPerHour, 3);
        // One MiB every ten seconds is 360 MiB per hour.
        Assert.Equal(360, trend.ManagedHeapSlopeMiBPerHour!.Value, 3);
        Assert.Equal(3 * 360d, trend.Generation2CollectionsPerHour!.Value, 3);
        Assert.Equal(1_000_000 / 10d, trend.AllocatedBytesPerSecond!.Value, 3);
        Assert.Equal(0, trend.WorkingSetGrowthMiB!.Value, 6);
    }

    [Fact]
    public void TheMemoryRuleJudgesOnlyALongSteadyIntervalAndTheWorstProcess()
    {
        var boundary = StressQualityBoundary.Load(BoundaryPath);
        var limit = boundary.Rules.Single(rule => rule.Rule == "process_memory_growth").Limit;
        var report = SoakReport(minutes: 9, workerGrowth: limit + 10);

        var shortRun = Rule(boundary, report, "process_memory_growth");
        var leak = Rule(boundary, SoakReport(minutes: 10, workerGrowth: limit + 10), "process_memory_growth");
        var clean = Rule(boundary, SoakReport(minutes: 30, workerGrowth: limit - 1), "process_memory_growth");
        var unmeasured = Rule(
            boundary,
            SoakReport(minutes: 30, workerGrowth: 0) with { Soak = new StressSoakSummary(null, null, []) },
            "process_memory_growth");

        Assert.True(shortRun.Passed);
        Assert.StartsWith("Not applicable", shortRun.Note, StringComparison.Ordinal);
        Assert.False(leak.Passed);
        Assert.Equal(limit + 10, leak.Measured!.Value, 6);
        Assert.Contains("worst worker", leak.Note, StringComparison.Ordinal);
        Assert.True(clean.Passed);
        Assert.False(unmeasured.Passed);
        Assert.Null(unmeasured.Measured);
    }

    // A planned ten-minute interval is judged even when its measured end falls a little short.
    [Fact]
    public void APlannedSixHundredSecondIntervalAlwaysAppliesTheMemoryRule()
    {
        var boundary = StressQualityBoundary.Load(BoundaryPath);
        var report = SoakReport(minutes: 10, workerGrowth: 1);
        var shortened = report with { SteadyEndedAt = report.SteadyStartedAt!.Value.AddSeconds(599.98) };

        var rule = Rule(boundary, shortened, "process_memory_growth");

        Assert.Equal(600, shortened.Configuration.SteadyDurationSeconds);
        Assert.True(rule.Passed);
        Assert.NotNull(rule.Measured);
        Assert.DoesNotContain("Not applicable", rule.Note ?? string.Empty, StringComparison.Ordinal);
    }

    // A bot on its way out of or back into the game may hold a session but does not certainly
    // hold one; a playing bot holds one either way; a failed bot holds none.
    [Theory]
    [InlineData(PlayingChurnPhase.None, StressBotState.Joined, false, true, true)]
    [InlineData(PlayingChurnPhase.Waiting, StressBotState.Joined, false, true, true)]
    [InlineData(PlayingChurnPhase.Retreating, StressBotState.Joined, false, true, true)]
    [InlineData(PlayingChurnPhase.Settling, StressBotState.Joined, false, true, true)]
    [InlineData(PlayingChurnPhase.Leaving, StressBotState.Leaving, false, false, true)]
    [InlineData(PlayingChurnPhase.Leaving, StressBotState.Completed, false, false, true)]
    [InlineData(PlayingChurnPhase.Away, StressBotState.Completed, false, false, true)]
    [InlineData(PlayingChurnPhase.Rejoining, StressBotState.Connecting, false, false, true)]
    [InlineData(PlayingChurnPhase.Rejoining, StressBotState.Joined, false, false, true)]
    [InlineData(PlayingChurnPhase.Rejoining, StressBotState.Joined, true, false, false)]
    [InlineData(PlayingChurnPhase.None, StressBotState.Failed, false, false, false)]
    [InlineData(PlayingChurnPhase.None, StressBotState.Leaving, false, false, true)]
    public void AChurningBotCountsOnTheRightSideOfTheSessionDrift(
        PlayingChurnPhase phase,
        StressBotState connection,
        bool failed,
        bool certainly,
        bool possibly)
    {
        Assert.Equal(new StressSessionHolding(certainly, possibly), PlayingStressBot.SessionHoldingOf(phase, connection, failed));
    }

    // 100 bots, three of them churning: one leaving, whose body holds its session, one away after
    // its body expired, and one rejoining, whose new session exists. Both drift numbers stay 0.
    [Fact]
    public void ARunWithChurnHasNoDrift()
    {
        var holding = Enumerable.Repeat(PlayingStressBot.SessionHoldingOf(PlayingChurnPhase.None, StressBotState.Joined, false), 97)
            .Append(PlayingStressBot.SessionHoldingOf(PlayingChurnPhase.Leaving, StressBotState.Leaving, false))
            .Append(PlayingStressBot.SessionHoldingOf(PlayingChurnPhase.Away, StressBotState.Completed, false))
            .Append(PlayingStressBot.SessionHoldingOf(PlayingChurnPhase.Rejoining, StressBotState.Joined, false))
            .ToArray();
        var drift = new StressDriftAccumulator(10, 30, 64);

        drift.Add(Drift(holding, activeSessions: 99));
        drift.Add(Drift(holding, activeSessions: 97));
        drift.Add(Drift(holding, activeSessions: 100));
        var summary = drift.Capture()!;

        Assert.Equal(0, summary.MaximumSessionsWithoutBot);
        Assert.Equal(0, summary.MaximumBotsWithoutSession);
    }

    [Fact]
    public void ASessionThatIsReallyMissingOrLeftOverStillShows()
    {
        var holding = Enumerable.Repeat(StressSessionHolding.Holds, 99).Append(StressSessionHolding.None).ToArray();
        var drift = new StressDriftAccumulator(10, 30, 64);

        // A playing bot lost its session, and later a failed bot's session stayed behind.
        drift.Add(Drift(holding, activeSessions: 98));
        drift.Add(Drift(holding, activeSessions: 100));
        var summary = drift.Capture()!;

        Assert.Equal(1, summary.MaximumBotsWithoutSession);
        Assert.Equal(1, summary.MaximumSessionsWithoutBot);
    }

    [Fact]
    public void TheServiceLogsCountByMessageClassAndTheAllowListExcusesOnlyItsClasses()
    {
        const string log = """
            info: Microsoft.Hosting.Lifetime[0]
                  Application started. Press Ctrl+C to shut down.
            warn: Mmorpg.SimulationWorker.Sessions.SimulationSessionHeartbeatService[0]
                  Invalidated simulation session 0b6f2c1e-2b1a-4c47-9c55-1f0b8b0e6c11 with code session_revoked after GameService returned 401.
            warn: Mmorpg.SimulationWorker.Sessions.SimulationSessionHeartbeatService[0]
                  Invalidated simulation session 77aa2c1e-2b1a-4c47-9c55-1f0b8b0e6c12 with code session_revoked after GameService returned 401.
            fail: Mmorpg.GameService.Items.ItemTransactionService[0]
                  Item transaction relocate 7aebcfdd-d296-4cbd-a064-dce065822f97 failed unexpectedly for correlation 7bd0bb285bcf9385da4662c2e49df09f.
                  System.OperationCanceledException: The operation was canceled.
                     at System.Threading.CancellationToken.ThrowOperationCanceledException()
            crit: Mmorpg.SimulationWorker.Registry.SimulationWorkerRegistrationLeaseMonitor[0]
                  Simulation worker registration lease expired at 11/13/2026 23:28:42 +00:00. Stopping the process to prevent split-brain simulation.
            """;
        var folder = Path.Combine(Path.GetTempPath(), "stress-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "worker.log"), log);
            File.WriteAllText(Path.Combine(folder, "worker.err.log"), "Unhandled exception. System.InvalidOperationException: broken\n   at Somewhere()\n");
            var allowance = new StressLogAllowance(
                "simulation-worker",
                StressServiceLogs.Critical,
                "Mmorpg.SimulationWorker.Registry.SimulationWorkerRegistrationLeaseMonitor",
                "Simulation worker registration lease expired",
                "A test reason.");

            var logs = StressServiceLogs.Read(
                [("simulation-worker", Path.Combine(folder, "worker.log"), false), ("simulation-worker", Path.Combine(folder, "worker.err.log"), true)],
                [allowance]);

            Assert.Equal(5, logs.Files[0].Entries);
            Assert.Equal((2L, 1L, 1L), (logs.Files[0].Warnings, logs.Files[0].Errors, logs.Files[0].Critical));
            var revoked = Assert.Single(logs.Classes, entry => entry.Level == StressServiceLogs.Warning);
            Assert.Equal(2, revoked.Count);
            Assert.Equal("Invalidated simulation session <id> with code session_revoked after GameService returned #.", revoked.Message);
            Assert.True(Assert.Single(logs.Classes, entry => entry.Level == StressServiceLogs.Critical).Allowed);
            Assert.Contains(logs.Classes, entry => entry.Category == StressServiceLogs.StandardErrorCategory);
            // The error line and the standard error line count; the allowed critical line does not.
            Assert.Equal(2, logs.UnallowedErrors);
            Assert.Equal(StressServiceLogs.Critical, logs.Classes[0].Level);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("\"allow\": []", "\"allow\": [ { \"service\": \"game-service\", \"level\": \"error\", \"category\": \"C\", \"message\": \"M\", \"reason\": \"R\" } ]", null)]
    [InlineData("\"allow\": []", "\"allow\": [ { \"service\": \"game-service\", \"level\": \"warning\", \"category\": \"C\", \"message\": \"M\", \"reason\": \"R\" } ]", "only error and critical")]
    [InlineData("\"allow\": []", "\"allow\": [ { \"service\": \"game-service\", \"level\": \"error\", \"category\": \"C\", \"message\": \"M\" } ]", "needs the text 'reason'")]
    [InlineData("\"rule\": \"deadlocks\",", "\"rule\": \"deadlocks\", \"allow\": [],", "unknown property 'allow'")]
    public void TheAllowListBelongsToTheLogRuleAndNamesAReason(string find, string replace, string? error)
    {
        var json = File.ReadAllText(BoundaryPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(find, json, StringComparison.Ordinal);
        json = json.Replace(find, replace, StringComparison.Ordinal);

        if (error is null)
        {
            Assert.Single(StressQualityBoundary.Parse(json, "boundary").LogAllowances);
            return;
        }

        var exception = Assert.Throws<StressGeneratorOptionException>(() => StressQualityBoundary.Parse(json, "boundary"));
        Assert.Contains(error, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCheckedInAllowListStartsEmpty()
    {
        Assert.Empty(StressQualityBoundary.Load(BoundaryPath).LogAllowances);
    }

    // The judge reads the report the generator wrote, adds the logs, and judges it again.
    [Fact]
    public void AReportReadsBackAsItWasWritten()
    {
        var report = SoakReport(minutes: 30, workerGrowth: 3) with
        {
            PostgresLog = new StressPostgresLogSummary("postgres.log", 1, 2)
        };

        var back = StressRunReport.Parse(report.ToJson());

        Assert.Equal(report.ToJson(), back.ToJson());
        Assert.Equal(
            StressQualityBoundary.Load(BoundaryPath).Evaluate(report),
            StressQualityBoundary.Load(BoundaryPath).Evaluate(back),
            new VerdictComparer());
    }

    [Fact]
    public void TheDeadlockRuleNamesTheKeptPostgresLog()
    {
        var report = SoakReport(minutes: 30, workerGrowth: 0) with
        {
            PostgresLog = new StressPostgresLogSummary(@"C:\run\postgres.log", 1, 4)
        };

        var deadlocks = Rule(StressQualityBoundary.Load(BoundaryPath), report, "deadlocks");

        Assert.Contains(@"C:\run\postgres.log", deadlocks.Note, StringComparison.Ordinal);
        Assert.Contains("4 lock waits", deadlocks.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePostgresLogCountsItsDeadlocksAndLockWaits()
    {
        var path = Path.Combine(Path.GetTempPath(), "postgres-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllLines(path,
            [
                "2026-11-14 12:00:00.001 UTC [81] LOG:  process 81 still waiting for ShareLock on transaction 900 after 1000.123 ms",
                "2026-11-14 12:00:00.500 UTC [82] ERROR:  deadlock detected",
                "2026-11-14 12:00:00.500 UTC [82] DETAIL:  Process 82 waits for ShareLock on transaction 900; blocked by process 81."
            ]);

            var log = StressRunJudge.ReadPostgresLog(path);

            Assert.Equal((1L, 1L), (log.DeadlockReports, log.LockWaitReports));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheDeathsOfEveryRoleArePerBotAndTenMinutesOfTheSteadyInterval()
    {
        var deaths = PlayingMetrics.DeathsPerBotPerTenMinutes(
            new Dictionary<string, int> { ["fighter"] = 40, ["walker"] = 30, ["trader"] = 0 },
            new Dictionary<string, long> { ["fighterDeaths"] = 120, ["walkerDeaths"] = 3 },
            steadySeconds: 1800);

        Assert.Equal(1d, deaths["fighter"], 6);
        Assert.Equal(1d / 30d, deaths["walker"], 6);
        Assert.False(deaths.ContainsKey("trader"));
    }

    [Fact]
    public void RedisReportsItsUsedMemoryInItsInfo()
    {
        Assert.Equal(1_234_567, StressRedisSampler.ParseUsedMemory("# Memory\r\nused_memory:1234567\r\nused_memory_human:1.18M\r\n"));
        Assert.Throws<StressGeneratorOptionException>(() => StressRedisSampler.ParseEndpoint("10.0.0.5:6379"));
        Assert.Equal(56379, StressRedisSampler.ParseEndpoint("127.0.0.1:56379").Port);
    }

    // A remote player that leaves the bot's interest and comes back is one pair, so what a bot
    // keeps does not grow with the length of the run.
    [Fact]
    public void ARemotePlayerSeenTwiceIsOnePair()
    {
        var second = Stopwatch.Frequency;
        var cadence = new SimulationBotRemoteCadence();
        cadence.BeginWindow(0);
        for (var visit = 0; visit < 3; visit++)
        {
            cadence.OnSpawn(5, visit * 10 * second);
            cadence.OnRecord(5, (uint)(visit + 1), (visit * 10 * second) + 1);
            cadence.OnDespawn(5, ((visit * 10) + 2) * second);
        }

        cadence.EndWindow(40 * second);

        var summary = cadence.Capture()!;
        Assert.Equal(1, summary.Entities);
        Assert.Equal(3, summary.PerEntity.Single().Updates);
        Assert.Equal(6d, summary.PerEntity.Single().VisibleSeconds, 6);
        var combined = SimulationBotRemoteCadenceSummary.Combine(summary, summary)!;
        Assert.Equal(2, combined.Entities);
        Assert.Equal(6, combined.Updates);
    }

    [Fact]
    public void TheGeneratorTakesTheSoakOptions()
    {
        var options = StressGeneratorOptions.Parse(
            ["--redis-endpoint", "127.0.0.1:56379", "--database-pool-size", "64", "--worker-heartbeat-timeout-seconds", "30"],
            Directory.GetCurrentDirectory());

        Assert.Equal("127.0.0.1:56379", options.RedisEndpoint);
        Assert.Equal(64, options.DatabasePoolSize);
        Assert.Equal(30, options.WorkerHeartbeatTimeoutSeconds);
        Assert.Throws<StressGeneratorOptionException>(() => StressGeneratorOptions.Parse(
            ["--redis-endpoint", "example.com:6379"],
            Directory.GetCurrentDirectory()));
    }

    private static PlayingRejoinVerdict Judge(
        PlayingLeaveSnapshot leave,
        PlayingRejoinObservation rejoin,
        PlayingRejoinFacts? facts = null)
    {
        return PlayingRejoinCheck.Judge(leave, rejoin, facts, healthPerSecond: 1);
    }

    private static PlayingLeaveSnapshot Leave(int health)
    {
        return new PlayingLeaveSnapshot(Spot, health, 100, 100, LeftAt, Session.ToString("D"), 20);
    }

    private static PlayingRejoinObservation Rejoin(bool reconnect, int health, double after, float x = 10f)
    {
        return new PlayingRejoinObservation(
            reconnect,
            new SimulationVector3(x, Spot.Y, Spot.Z),
            health,
            100,
            RealtimeCombatLifeState.Alive,
            LeftAt.AddSeconds(after));
    }

    private static PlayingRejoinFacts Facts(int health, double standing, float x = 10f)
    {
        return new PlayingRejoinFacts(
            LeftAt.AddSeconds(standing),
            null,
            new PlayingSavedState(new SimulationVector3(x, Spot.Y, Spot.Z), health, 100, Session));
    }

    private static StressDriftSample Drift(IReadOnlyCollection<StressSessionHolding> bots, int activeSessions)
    {
        return new StressDriftSample(
            LeftAt,
            bots.Count(bot => bot.Certainly),
            bots.Count(bot => bot.Possibly),
            activeSessions,
            0,
            25,
            3,
            0,
            10,
            1,
            0);
    }

    private static StressVerdictEntry Rule(StressQualityBoundary boundary, StressRunReport report, string rule)
    {
        return boundary.Evaluate(report).Rules.Single(entry => entry.Rule == rule);
    }

    private static StressRunReport SoakReport(int minutes, double workerGrowth)
    {
        var start = LeftAt;
        var latency = new StressLatencySummary(10, 1, 2, 3, 4, 5, 2);
        return new StressRunReport(
            start.AddMinutes(-2),
            start.AddMinutes(minutes + 1),
            (minutes + 3) * 60,
            new StressRunConfiguration(
                "full-stack", "playing", "http://127.0.0.1:5500/", "127.0.0.1", 27025, "worker", "fleet", "node",
                "local-shard-2", "development-world-2", 10, 1, 10, 0, 6, minutes * 60, 8, 3, 1337),
            null,
            null,
            null,
            null,
            new StressPostgresSummary("db", 10, 1, 13, 1, 5, 3, 0, 10, 0, 1000, 10, 40, 1000, 0, 100, 100, 0, 0, 0, 1, 0, 0, null),
            null,
            new StressGameplaySummary(
                "playing",
                null,
                0,
                new Dictionary<string, StressGameplayOperationSummary>
                {
                    ["leave"] = new(5, 5, 0, 0, 0, latency, new Dictionary<string, long> { ["success"] = 5 })
                }),
            new StressBotAggregate(10, 10, 10, 0, 0, 1, 1, 1, 1, 1, 0, 1, 1, 0, 1, latency, latency, new Dictionary<string, int>()),
            null)
        {
            SteadyStartedAt = start,
            SteadyEndedAt = start.AddMinutes(minutes),
            Soak = new StressSoakSummary(
                null,
                null,
                [
                    new StressMemoryTrend("worker", 100, 10, [200, 200, 200, 200, 200 + workerGrowth], 1, [50, 50, 50, 50, 51], 12, 1_000_000),
                    new StressMemoryTrend("gameService", 100, 0, [300, 300, 300, 300, 300]),
                    new StressMemoryTrend("generator", 100, 0, [400, 400, 400, 400, 400], 0, [100, 100, 100, 100, 100])
                ]),
            ServiceLogs = new StressServiceLogSummary([], [])
        };
    }

    private static StressWorkerInterval WorkerInterval(DateTime start, double heapMiB, double workingSetMiB)
    {
        return new StressWorkerInterval(
            start, start.AddSeconds(10), 10, 10, 0, 10, 0, 11, 2, 30, (long)(workingSetMiB * 1024 * 1024),
            null, null, 0, 0,
            null, null, null, null, null, null, null, null,
            0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 1_000_000, 1, 0, 3, (long)(heapMiB * 1024 * 1024), 0, 0);
    }

    private sealed class VerdictComparer : IEqualityComparer<StressVerdict>
    {
        public bool Equals(StressVerdict? x, StressVerdict? y)
        {
            return x is not null
                && y is not null
                && x.Passed == y.Passed
                && x.Valid == y.Valid
                && x.Rules.SequenceEqual(y.Rules);
        }

        public int GetHashCode(StressVerdict obj) => obj.Rules.Count;
    }
}
