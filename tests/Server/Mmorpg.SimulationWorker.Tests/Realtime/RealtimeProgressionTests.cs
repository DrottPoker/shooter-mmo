using System.Net;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeQuestCreditTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The owner learns its level and experience after every join and after every change GameService
// committed, before the quest log change and the result of the operation that caused it; the
// viewers of a character hear when it reached a higher level. A kill credit that cannot give the
// character experience costs no GameService call unless a quest asks for the kill. The Admin
// menu's level and experience tools run through GameService on the character's lane.
public sealed class RealtimeProgressionTests
{
    private const string Wolves = "quest.fixture.wolves";

    private static readonly SimulationProgression Midway = new(12, 3400, 9800, 0);

    [Fact]
    public async Task AJoinSendsTheProgressionAfterJoinAcceptedAndAReconnectSendsItAgain()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Growing Walker") with { Progression = Midway };
        harness.GameService.AddCharacter(character);
        var bot = harness.AddCharacter("Levelless Bot", isSyntheticBot: true);
        await harness.StartAsync();

        var first = await harness.JoinAsync(character, "first");
        await harness.PollUntilAsync(() => Progressions(first).Count == 1, "the joined progression");

        var snapshot = Progressions(first)[0];
        Assert.Equal(
            (12, 3400L, 9800L, 0, 0L, 0, RealtimeProgressionChangeReason.Snapshot),
            Describe(snapshot));
        Assert.True(
            first.IndexOfFirst(RealtimeMessageType.JoinAccepted)
                < first.IndexOfFirst(RealtimeMessageType.OwnerProgressionChanged));

        // The reconnect's ticket read the progression again, which the snapshot carries.
        var reconnect = character with
        {
            Ticket = "ticket-growing-reconnect",
            SimulationSessionToken = "growing-reconnected-token",
            Progression = new SimulationProgression(13, 10, 10400, 6)
        };
        harness.GameService.AddCharacter(reconnect);
        var second = await harness.JoinAsync(reconnect, "second");
        await harness.PollUntilAsync(() => Progressions(second).Count == 1, "the reconnected progression");
        Assert.True(second.JoinAccepted!.IsReconnect);
        Assert.Equal(
            (13, 10L, 10400L, 6, 0L, 0, RealtimeProgressionChangeReason.Snapshot),
            Describe(Progressions(second)[0]));

        var botClient = await harness.JoinAsync(bot);
        await harness.PollForAsync(TimeSpan.FromMilliseconds(300));
        Assert.Empty(Progressions(botClient));
    }

    // The progression goes before the quest log change, both on the owner channel.
    [Fact]
    public async Task AKillCreditSendsTheProgressionBeforeTheQuestLogChange()
    {
        await using var harness = RealtimeQuestCreditTests.Create();
        var archer = AddProgressingArcher(harness, "Rising Hunter", Midway);
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.QuestLog(request.CharacterId, 3, Hunting(0)));
        harness.GameService.OnQuestOperation = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.CommittedProgression(
                request,
                4,
                new SimulationProgressionChange(Guid.Empty, 12, 3445, 9800, 0, 45, 0),
                new SimulationQuestChange(Wolves, "hunt", [new SimulationQuestObjectiveProgress("wolves", 1)], 0)));
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(() => QuestChanges(client).Count == 1, "the counted kill");

        Assert.Equal(
            [(12, 3400L, 9800L, 0, 0L, 0, RealtimeProgressionChangeReason.Snapshot),
                (12, 3445L, 9800L, 0, 45L, 0, RealtimeProgressionChangeReason.MobKill)],
            Progressions(client).Select(Describe));
        var owner = client.ReliablePacketsOn(RealtimeProtocol.OwnerChannel).Select(packet => packet.Type).ToList();
        Assert.True(
            owner.LastIndexOf(RealtimeMessageType.OwnerProgressionChanged)
                < owner.IndexOf(RealtimeMessageType.QuestLogChanged));
    }

    // A level-up reaches the players that see the character in detail, never its owner, who learns
    // it from its own progression.
    [Fact]
    public async Task ALevelUpReachesAViewerAndNotTheOwner()
    {
        await using var harness = RealtimeQuestCreditTests.Create();
        var archer = AddProgressingArcher(harness, "Leveling Hunter", new SimulationProgression(3, 1400, 1500, -4));
        var viewer = harness.AddCharacter("Level Watcher");
        harness.GameService.OnQuestOperation = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.CommittedProgression(
                request,
                0,
                new SimulationProgressionChange(Guid.Empty, 4, 20, 1800, -3, 120, 1)));
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var watcher = await harness.JoinAsync(viewer);
        var entityId = client.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(() => watcher.Spawns.Any(spawn => spawn.EntityId == entityId), "the hunter's spawn");
        var wolf = await WaitForWolfAsync(harness, client);

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(() => LevelUps(watcher).Count == 1, "the viewer's level-up");
        await harness.PollUntilAsync(() => Progressions(client).Count == 2, "the owner's progression");

        var levelUp = Assert.Single(LevelUps(watcher));
        Assert.Equal((entityId, (ushort)4), (levelUp.EntityId, levelUp.Level));
        Assert.Equal(
            (4, 20L, 1800L, -3, 120L, 1, RealtimeProgressionChangeReason.MobKill),
            Describe(Progressions(client)[1]));
        await harness.PollForAsync(TimeSpan.FromMilliseconds(300));
        Assert.Empty(LevelUps(client));
        Assert.Empty(Progressions(watcher));
    }

    [Fact]
    public async Task ATurnInSendsTheProgressionOfItsReward()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            EnableQuests = true
        });
        var character = harness.AddCharacter("Quest Finisher") with { Progression = Midway };
        harness.GameService.AddCharacter(character);
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.QuestLog(request.CharacterId, 3, Hunting(5)));
        harness.GameService.OnQuestOperation = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.CommittedProgression(
                request,
                4,
                new SimulationProgressionChange(Guid.Empty, 12, 3700, 9800, 0, 300, 0),
                new SimulationQuestChange(Wolves, null, [], 1)));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        var services = await RealtimeWorldActorInteractionTests.WaitForServicesAsync(harness, client);
        client.SendWorldIntent(RealtimeWorldActorInteractionTests.CreateOpen(character.SimulationSessionId, services));
        await harness.PollUntilAsync(() => client.WorldOpened.Count == 1, "the open");
        var opened = client.WorldOpened[0];
        var quests = opened.Capabilities.Single(value => value.Kind == RealtimeWorldActorCapabilityKind.Quests);

        client.SendWorldIntent(RealtimeWorldInteractionIntent.CreateCapabilityAction(
            Guid.NewGuid(),
            character.SimulationSessionId,
            opened.InteractionSessionId,
            RealtimeWorldInteractionTargetKind.WorldActor,
            services.EntityId,
            services.RuntimeActorId,
            services.InteractionRevision,
            quests.Id,
            quests.Kind,
            quests.Revision,
            RealtimeCapabilityActionKind.TurnInQuest,
            questId: Wolves));
        await harness.PollUntilAsync(() => client.WorldResults.Count == 1, "the turn-in result");

        Assert.Equal("turn_in_quest", Assert.Single(harness.GameService.QuestOperations).OperationKind);
        Assert.True(Assert.Single(client.WorldResults).Succeeded);
        Assert.Equal(
            (12, 3700L, 9800L, 0, 300L, 0, RealtimeProgressionChangeReason.QuestReward),
            Describe(Progressions(client)[^1]));
        var owner = client.ReliablePacketsOn(RealtimeProtocol.OwnerChannel).Select(packet => packet.Type).ToList();
        Assert.True(
            owner.LastIndexOf(RealtimeMessageType.OwnerProgressionChanged)
                < owner.IndexOf(RealtimeMessageType.QuestLogChanged));
        Assert.True(
            owner.IndexOf(RealtimeMessageType.QuestLogChanged)
                < owner.IndexOf(RealtimeMessageType.WorldInteractionResult));
    }

    [Fact]
    public async Task AtTheHighestLevelAKillSendsNoCreditUnlessAQuestAsksForIt()
    {
        var highest = new SimulationProgression(200, 0, 0, int.MaxValue);
        await using (var harness = RealtimeQuestCreditTests.Create())
        {
            var archer = AddProgressingArcher(harness, "Capped Hunter", highest);
            await harness.StartAsync();
            var client = await harness.JoinAsync(archer);

            await KillTheWolfAsync(harness, client);
            await harness.PollForAsync(TimeSpan.FromMilliseconds(300));

            Assert.Empty(harness.GameService.QuestOperations);
        }

        await using (var harness = RealtimeQuestCreditTests.Create())
        {
            var archer = AddProgressingArcher(harness, "Capped Quester", highest);
            harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(
                ScriptedGameServiceHandler.QuestLog(request.CharacterId, 3, Hunting(0)));
            harness.GameService.OnQuestOperation = (_, request, _) => Task.FromResult(KillCounted(request, 4, 1));
            await harness.StartAsync();
            var client = await harness.JoinAsync(archer);

            await KillTheWolfAsync(harness, client);
            await harness.PollUntilAsync(() => QuestChanges(client).Count == 1, "the counted kill");

            Assert.Equal("credit_mob_kill", Assert.Single(harness.GameService.QuestOperations).OperationKind);
        }
    }

    // A Mob at the highest level that gives the character nothing costs no credit either.
    [Fact]
    public async Task AKillOfAMobTooLowForTheCharacterSendsNoCredit()
    {
        await using var harness = RealtimeQuestCreditTests.Create();
        var wolfLevel = harness.WorldActorDocument!.Catalog.Actors.Single(actor => actor.Id == "mob.feral_wolf").Level;
        var archer = AddProgressingArcher(harness, "Outgrown Hunter", new SimulationProgression(30, 0, 50000, wolfLevel));
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);

        await KillTheWolfAsync(harness, client);
        await harness.PollForAsync(TimeSpan.FromMilliseconds(300));

        Assert.Empty(harness.GameService.QuestOperations);
    }

    // A player who left stays as a body without a peer: its credit commits, and nothing reaches the
    // connection that is gone.
    [Fact]
    public async Task ADetachedSessionHearsNothingAndItsCreditStillCommits()
    {
        await using var harness = RealtimeQuestCreditTests.Create();
        var archer = AddProgressingArcher(harness, "Departing Riser", Midway);
        var gate = new TestGate();
        var committed = 0;
        harness.GameService.OnQuestOperation = async (_, request, _) =>
        {
            await gate.WaitAsync();
            Interlocked.Increment(ref committed);
            return ScriptedGameServiceHandler.CommittedProgression(
                request,
                0,
                new SimulationProgressionChange(Guid.Empty, 13, 5, 10400, 6, 6405, 1));
        };
        await harness.StartAsync();
        try
        {
            var client = await harness.JoinAsync(archer);
            var wolf = await WaitForWolfAsync(harness, client);
            client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
            await harness.PollUntilAsync(() => gate.HasEntered, "the credit to reach GameService");
            client.SendLeave(client.JoinAccepted!.SimulationSessionId);
            await harness.PollUntilAsync(() => client.Has(RealtimeMessageType.LeaveAccepted), "the leave");

            gate.Release();
            await harness.PollUntilAsync(() => Volatile.Read(ref committed) == 1, "the credit to commit");
            await harness.PollForAsync(TimeSpan.FromMilliseconds(300));

            Assert.Single(harness.GameService.QuestOperations);
            Assert.Equal(RealtimeProgressionChangeReason.Snapshot, Assert.Single(Progressions(client)).Reason);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task SetLevelAndGrantExperienceRunThroughGameServiceAndAnswerAfterTheProgression()
    {
        await using var harness = AdminHarness();
        var character = harness.AddCharacter("Level Admin") with { Progression = Midway };
        harness.GameService.AddCharacter(character);
        harness.GameService.OnQuestOperation = (_, request, _) => Task.FromResult(
            request.OperationKind == "admin_set_character_level"
                ? ScriptedGameServiceHandler.CommittedProgression(
                    request,
                    0,
                    new SimulationProgressionChange(Guid.Empty, 60, 0, 90000, 50, 0, 48))
                : ScriptedGameServiceHandler.CommittedProgression(
                    request,
                    0,
                    new SimulationProgressionChange(Guid.Empty, 60, 1500, 90000, 50, 1500, 0)));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        await harness.PollUntilAsync(() => Progressions(client).Count == 1, "the joined progression");

        var setLevel = RealtimeAdminIntent.CreateSetLevel(Guid.NewGuid(), 60);
        client.Send(RealtimeProtocol.EncodeAdminIntent(setLevel));
        await harness.PollUntilAsync(() => AdminResults(client).Count == 1, "the set level");
        var grant = RealtimeAdminIntent.CreateGrantExperience(Guid.NewGuid(), 1500);
        client.Send(RealtimeProtocol.EncodeAdminIntent(grant));
        await harness.PollUntilAsync(() => AdminResults(client).Count == 2, "the granted experience");

        Assert.Equal(
            [
                (setLevel.OperationId, "admin_set_character_level", (int?)60, (long?)null, (string?)null),
                (grant.OperationId, "admin_grant_experience", (int?)null, (long?)1500, (string?)null)
            ],
            harness.GameService.QuestOperations.Select(operation =>
                (operation.OperationId, operation.OperationKind, operation.Level, operation.Experience, operation.QuestId)));
        Assert.All(AdminResults(client), result => Assert.True(result.Succeeded, result.Error?.Code));
        Assert.Equal(
            [(setLevel.OperationId, RealtimeAdminIntentKind.SetLevel), (grant.OperationId, RealtimeAdminIntentKind.GrantExperience)],
            AdminResults(client).Select(result => (result.OperationId, result.Kind)));
        Assert.Equal(
            [
                (60, 0L, 90000L, 50, 0L, 48, RealtimeProgressionChangeReason.Admin),
                (60, 1500L, 90000L, 50, 1500L, 0, RealtimeProgressionChangeReason.Admin)
            ],
            Progressions(client).Skip(1).Select(Describe));
        var owner = client.ReliablePacketsOn(RealtimeProtocol.OwnerChannel)
            .Where(packet => packet.Type is RealtimeMessageType.OwnerProgressionChanged or RealtimeMessageType.AdminIntentResult)
            .Select(packet => packet.Type)
            .Skip(1)
            .ToArray();
        Assert.Equal(
            [
                RealtimeMessageType.OwnerProgressionChanged,
                RealtimeMessageType.AdminIntentResult,
                RealtimeMessageType.OwnerProgressionChanged,
                RealtimeMessageType.AdminIntentResult
            ],
            owner);
    }

    [Fact]
    public async Task AProgressionToolThatGameServiceRefusesAnswersWithItsError()
    {
        await using var harness = AdminHarness();
        var character = harness.AddCharacter("Overreaching Admin") with { Progression = Midway };
        harness.GameService.AddCharacter(character);
        harness.GameService.OnQuestOperation = (_, _, _) => Task.FromResult(
            ScriptedGameServiceHandler.Problem(
                HttpStatusCode.Conflict,
                "progression_level_above_highest",
                "The level is above the highest level."));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        await harness.PollUntilAsync(() => Progressions(client).Count == 1, "the joined progression");

        client.Send(RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateSetLevel(Guid.NewGuid(), 9000)));
        await harness.PollUntilAsync(() => AdminResults(client).Count == 1, "the refused level");

        var result = Assert.Single(AdminResults(client));
        Assert.Equal((false, "progression_level_above_highest"), (result.Succeeded, result.Error!.Code));
        Assert.Single(harness.GameService.QuestOperations);
        Assert.Single(Progressions(client));
        Assert.Null(client.ServerDisconnect);
    }

    [Fact]
    public async Task AWorkerWithoutTheAdminToolsRefusesTheProgressionTools()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions { EnableQuests = true });
        var character = harness.AddCharacter("Plain Leveler");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.Send(RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateSetLevel(Guid.NewGuid(), 10)));
        client.Send(RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateGrantExperience(Guid.NewGuid(), 10)));
        await harness.PollUntilAsync(() => AdminResults(client).Count == 2, "both refusals");

        Assert.All(AdminResults(client), result => Assert.Equal((false, "admin_tools_disabled"), (result.Succeeded, result.Error!.Code)));
        Assert.Empty(harness.GameService.QuestOperations);
    }

    private static RealtimeServerHarness AdminHarness()
    {
        return RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableQuests = true,
            EnableAdminTools = true
        });
    }

    private static RealtimeTestCharacter AddProgressingArcher(
        RealtimeServerHarness harness,
        string name,
        SimulationProgression progression)
    {
        var archer = AddArcher(harness, name) with { Progression = progression };
        harness.GameService.AddCharacter(archer);
        return archer;
    }

    private static async Task KillTheWolfAsync(RealtimeServerHarness harness, RealtimeRecordingClient client)
    {
        var wolf = await WaitForWolfAsync(harness, client);
        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(
            () => Events(client).Any(value => value.Kind == RealtimeCombatEventKind.Died && value.TargetEntityId == wolf.EntityId),
            "the wolf to die");
    }

    private static (int Level, long Experience, long ToNext, int Highest, long Gained, int Levels, RealtimeProgressionChangeReason Reason)
        Describe(RealtimeOwnerProgressionChanged change)
    {
        return (
            change.Level,
            change.Experience,
            change.ExperienceToNextLevel,
            change.HighestMobLevelWithoutExperience,
            change.ExperienceGained,
            change.LevelsGained,
            change.Reason);
    }

    private static IReadOnlyList<RealtimeOwnerProgressionChanged> Progressions(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeOwnerProgressionChanged>(
            RealtimeMessageType.OwnerProgressionChanged,
            RealtimeProtocol.TryDecodeOwnerProgressionChanged);
    }

    private static IReadOnlyList<RealtimeEntityLeveledUp> LevelUps(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeEntityLeveledUp>(
            RealtimeMessageType.EntityLeveledUp,
            RealtimeProtocol.TryDecodeEntityLeveledUp);
    }

    private static IReadOnlyList<RealtimeAdminIntentResult> AdminResults(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeAdminIntentResult>(
            RealtimeMessageType.AdminIntentResult,
            RealtimeProtocol.TryDecodeAdminIntentResult);
    }
}
