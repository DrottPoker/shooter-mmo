using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A credited Mob kill that the player's quest log asks for commits in the player's lane, at the
// place it took at the kill, also across a reconnect. It asks again with the same operation after
// an uncertain answer and commits before the player's session or the worker ends. The quests it
// advanced reach the player without an operation.
public sealed class RealtimeQuestCreditTests
{
    private const string Wolves = "quest.fixture.wolves";

    [Fact]
    public async Task ACreditedKillCountsForTheTaggersQuestAndReachesThePlayer()
    {
        await using var harness = Create();
        var archer = AddArcher(harness, "Quest Hunter");
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.QuestLog(request.CharacterId, 3, Hunting(0)));
        harness.GameService.OnQuestOperation = (_, request, _) => Task.FromResult(KillCounted(request, 4, 1));
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(() => QuestChanges(client).Count == 1, "the counted kill");

        var credit = Assert.Single(harness.GameService.QuestOperations);
        Assert.Equal(("credit_quest_kill", "mob.feral_wolf"), (credit.OperationKind, credit.MobActorId));
        Assert.Null(credit.QuestId);
        Assert.Null(credit.QuestNpcActorId);
        var definition = harness.WorldActorDocument!.Actors.Single(actor => actor.Id == "mob.feral_wolf");
        Assert.Equal(definition.Tags, credit.MobTags);
        Assert.Equal(4, credit.OperationId.Version);
        var change = QuestChanges(client)[0];
        Assert.Equal((Guid.Empty, 3L, 4L, false), (change.OperationId, change.PreviousQuestLogRevision, change.QuestLogRevision, change.ReloadRequired));
        Assert.Equal(1, change.Quests[0].Objectives[0].Progress);
    }

    [Fact]
    public async Task AKillThatNoQuestAsksForSendsNoCredit()
    {
        await using var harness = Create();
        var archer = AddArcher(harness, "Questless Hunter");
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(
            () => Events(client).Any(value => value.Kind == RealtimeCombatEventKind.Died && value.TargetEntityId == wolf.EntityId),
            "the wolf to die");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(300));

        Assert.Empty(harness.GameService.QuestOperations);
        Assert.Empty(QuestChanges(client));
    }

    [Fact]
    public async Task AnUncertainCreditAsksAgainWithTheSameOperationUntilItCommits()
    {
        await using var harness = Create();
        var archer = AddArcher(harness, "Patient Hunter");
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.QuestLog(request.CharacterId, 3, Hunting(0)));
        var calls = 0;
        harness.GameService.OnQuestOperation = (_, request, _) => Interlocked.Increment(ref calls) <= 3
            ? throw new HttpRequestException("The answer was lost.")
            : Task.FromResult(KillCounted(request, 4, 1));
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(
            () => QuestChanges(client).Any(change => !change.ReloadRequired),
            "the kill to count after the retries");

        Assert.Equal(4, harness.GameService.QuestOperations.Count);
        Assert.Single(harness.GameService.QuestOperations.Select(operation => operation.OperationId).Distinct());
        var counted = QuestChanges(client).Single(change => !change.ReloadRequired);
        Assert.Equal((3L, 4L), (counted.PreviousQuestLogRevision, counted.QuestLogRevision));
        Assert.True(harness.QuestLogs!.TryGet(archer.CharacterId, archer.SimulationSessionId, out var log));
        Assert.Equal(1, log!.ActiveQuests[Wolves].Progress["wolves"]);
    }

    [Fact]
    public async Task ALeaveWaitsForTheCreditOfTheLastKill()
    {
        await using var harness = Create();
        var archer = AddArcher(harness, "Leaving Hunter");
        var gate = new TestGate();
        var releasesAtCredit = -1;
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.QuestLog(request.CharacterId, 3, Hunting(0)));
        harness.GameService.OnQuestOperation = async (_, request, _) =>
        {
            await gate.WaitAsync();
            releasesAtCredit = harness.GameService.ReleaseCount;
            return KillCounted(request, 4, 1);
        };
        await harness.StartAsync();
        try
        {
            var client = await harness.JoinAsync(archer);
            var wolf = await WaitForWolfAsync(harness, client);
            client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
            await harness.PollUntilAsync(() => gate.HasEntered, "the credit to reach GameService");

            client.SendLeave(client.JoinAccepted!.SimulationSessionId);
            await harness.PollForAsync(TimeSpan.FromMilliseconds(300));
            Assert.False(client.Has(RealtimeMessageType.LeaveAccepted));
            Assert.Equal(0, harness.GameService.ReleaseCount);

            gate.Release();
            await harness.PollUntilAsync(() => client.Has(RealtimeMessageType.LeaveAccepted), "the leave");
            Assert.Equal(0, releasesAtCredit);
            Assert.Single(harness.GameService.QuestOperations);
        }
        finally
        {
            gate.Release();
        }
    }

    // An abandon that the player sends after a kill waits for the kill's credit, so a kill of the
    // abandoned run never counts for the next one.
    [Fact]
    public async Task AQuestIntentSentAfterAKillWaitsForItsCredit()
    {
        await using var harness = Create(wolfRespawnSeconds: 0.5f);
        var archer = AddArcher(harness, "Orderly Hunter");
        var gate = new TestGate();
        ScriptGatedCredits(harness, gate);
        await harness.StartAsync();
        try
        {
            var client = await harness.JoinAsync(archer);
            await KillTwoWolvesAsync(harness, client, gate);
            client.Send(RealtimeProtocol.EncodeQuestIntent(
                new RealtimeQuestIntent(Guid.NewGuid(), RealtimeQuestIntentKind.Abandon, Wolves)));
            await harness.PollForAsync(TimeSpan.FromMilliseconds(300));

            gate.Release();
            await harness.PollUntilAsync(() => harness.GameService.QuestOperations.Count == 3, "every quest operation");

            Assert.Equal(
                ["credit_quest_kill", "credit_quest_kill", "abandon_quest"],
                harness.GameService.QuestOperations.Select(operation => operation.OperationKind));
        }
        finally
        {
            gate.Release();
        }
    }

    // A reconnect from another client keeps both credits ahead of the new client's operations:
    // the one in flight holds the new client's lane, and the one that waited behind it keeps its
    // place.
    [Fact]
    public async Task AReconnectKeepsThePendingCreditsAheadOfTheNewClient()
    {
        await using var harness = Create(wolfRespawnSeconds: 0.5f);
        var archer = AddArcher(harness, "Restless Hunter");
        var gate = new TestGate();
        ScriptGatedCredits(harness, gate);
        await harness.StartAsync();
        try
        {
            var first = await harness.JoinAsync(archer, "first");
            await KillTwoWolvesAsync(harness, first, gate);
            var reconnect = archer with
            {
                Ticket = "ticket-reconnect",
                SimulationSessionToken = "reconnected-session-token"
            };
            harness.GameService.AddCharacter(reconnect);
            var second = await harness.JoinAsync(reconnect, "second");
            second.Send(RealtimeProtocol.EncodeQuestIntent(
                new RealtimeQuestIntent(Guid.NewGuid(), RealtimeQuestIntentKind.Abandon, Wolves)));
            await harness.PollForAsync(TimeSpan.FromMilliseconds(300));
            Assert.Single(harness.GameService.QuestOperations);

            gate.Release();
            await harness.PollUntilAsync(() => harness.GameService.QuestOperations.Count == 3, "every quest operation");

            Assert.Equal(
                ["credit_quest_kill", "credit_quest_kill", "abandon_quest"],
                harness.GameService.QuestOperations.Select(operation => operation.OperationKind));
            await harness.PollUntilAsync(
                () => QuestChanges(second).Count(change => change.OperationId == Guid.Empty) == 2,
                "both credits on the new client");
        }
        finally
        {
            gate.Release();
        }
    }

    // A death while a leave waits for a credit keeps the leave, which follows the death.
    [Fact]
    public async Task ALeaveThatWaitsForACreditSurvivesTheCharactersDeath()
    {
        await using var harness = Create(playerVersusPlayer: true);
        var hunter = AddArcher(harness, "Doomed Hunter");
        var rival = AddArcher(harness, "Rival Archer");
        var gate = new TestGate();
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(request.CharacterId == hunter.CharacterId
            ? ScriptedGameServiceHandler.QuestLog(request.CharacterId, 3, Hunting(0))
            : ScriptedGameServiceHandler.QuestLog(request.CharacterId, 0));
        harness.GameService.OnQuestOperation = async (_, request, _) =>
        {
            await gate.WaitAsync();
            return KillCounted(request, 4, 1);
        };
        await harness.StartAsync();
        try
        {
            var hunterClient = await harness.JoinAsync(hunter);
            var rivalClient = await harness.JoinAsync(rival);
            var wolf = await WaitForWolfAsync(harness, hunterClient);
            hunterClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
            await harness.PollUntilAsync(() => gate.HasEntered, "the credit to reach GameService");
            hunterClient.SendLeave(hunterClient.JoinAccepted!.SimulationSessionId);
            var hunterEntityId = hunterClient.JoinAccepted.ControlledEntityId;
            await harness.PollUntilAsync(
                () => States(rivalClient).Any(value => value.EntityId == hunterEntityId),
                "the hunter to appear for the rival");

            rivalClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), hunterEntityId));
            await harness.PollUntilAsync(
                () => Events(rivalClient).Any(value => value.Kind == RealtimeCombatEventKind.Died && value.TargetEntityId == hunterEntityId),
                "the hunter to die");
            Assert.False(hunterClient.Has(RealtimeMessageType.LeaveAccepted));

            gate.Release();
            await harness.PollUntilAsync(() => hunterClient.Has(RealtimeMessageType.LeaveAccepted), "the leave after the death");
            Assert.Single(harness.GameService.PlayerDeaths);
            Assert.Single(harness.GameService.QuestOperations);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task AStoppingWorkerCountsTheKillsThatWaitInTheLane()
    {
        await using var harness = Create(wolfRespawnSeconds: 0.5f);
        var archer = AddArcher(harness, "Last Call Hunter");
        var gate = new TestGate();
        ScriptGatedCredits(harness, gate);
        await harness.StartAsync();
        try
        {
            var client = await harness.JoinAsync(archer);
            await KillTwoWolvesAsync(harness, client, gate);
            Assert.Single(harness.GameService.QuestOperations);

            // The first credit's answer arrives while the worker stops, so only the second one
            // waits in the lane.
            var stopping = harness.StopAsync();
            gate.Release();
            await stopping;

            Assert.Equal(2, harness.GameService.QuestOperations.Count);
            Assert.Equal(2, harness.GameService.QuestOperations.Select(operation => operation.OperationId).Distinct().Count());
            Assert.All(harness.GameService.QuestOperations, operation => Assert.Equal("mob.feral_wolf", operation.MobActorId));
        }
        finally
        {
            gate.Release();
        }
    }

    private static RealtimeServerHarness Create(float wolfRespawnSeconds = 30f, bool playerVersusPlayer = false)
    {
        return RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            EnableQuests = true,
            WorldActorDocument = WolfNextToTheSpawn(respawnSeconds: wolfRespawnSeconds),
            ItemCatalog = TestCatalog(minimumDamage: 100, maximumDamage: 100),
            CombatRules = TestRules(respawnSeconds: 30f),
            PlayerVersusPlayer = playerVersusPlayer,
            CombatRandom = new LowestRoll()
        });
    }

    // The first quest operation waits for the gate. Every kill credit counts one more wolf, and
    // an abandon ends the hunt.
    private static void ScriptGatedCredits(RealtimeServerHarness harness, TestGate gate)
    {
        var calls = 0;
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.QuestLog(request.CharacterId, 3, Hunting(0)));
        harness.GameService.OnQuestOperation = async (_, request, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                await gate.WaitAsync();
            }

            return request.OperationKind == "abandon_quest"
                ? ScriptedGameServiceHandler.CommittedQuestOperation(
                    request,
                    0,
                    3 + call,
                    new SimulationQuestChange(Wolves, null, [], 0))
                : KillCounted(request, 3 + call, call);
        };
    }

    // Kills the wolf, waits until its credit holds the gate, and kills the respawned wolf too.
    private static async Task KillTwoWolvesAsync(
        RealtimeServerHarness harness,
        RealtimeRecordingClient client,
        TestGate gate)
    {
        var wolf = await WaitForWolfAsync(harness, client);
        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(() => gate.HasEntered, "the first credit to reach GameService");
        await harness.PollUntilAsync(
            () => WolfSpawns(client).Any(value => value.EntityId != wolf.EntityId && value.SpawnDefinitionId == wolf.SpawnDefinitionId),
            "the wolf to respawn");
        var respawned = WolfSpawns(client).Last(value => value.SpawnDefinitionId == wolf.SpawnDefinitionId);
        await harness.PollUntilAsync(
            () => States(client).Any(value => value.EntityId == respawned.EntityId),
            "the respawned wolf's state");
        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), respawned.EntityId));
        await harness.PollUntilAsync(
            () => Events(client).Any(value => value.Kind == RealtimeCombatEventKind.Died && value.TargetEntityId == respawned.EntityId),
            "the respawned wolf to die");
    }

    private static RealtimeTestCharacter AddArcher(RealtimeServerHarness harness, string name)
    {
        var archer = harness.AddCharacter(name) with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        return archer;
    }

    private static SimulationActiveQuest Hunting(int progress)
    {
        return new SimulationActiveQuest(
            Wolves,
            "hunt",
            DateTime.UtcNow,
            [new SimulationQuestObjectiveProgress("wolves", progress)]);
    }

    private static HttpResponseMessage KillCounted(
        SimulationQuestOperationRequest request,
        long questLogRevision,
        int progress)
    {
        return ScriptedGameServiceHandler.CommittedQuestOperation(
            request,
            0,
            questLogRevision,
            new SimulationQuestChange(Wolves, "hunt", [new SimulationQuestObjectiveProgress("wolves", progress)], 0));
    }

    private static IReadOnlyList<RealtimeQuestLogChanged> QuestChanges(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeQuestLogChanged>(
            RealtimeMessageType.QuestLogChanged,
            RealtimeProtocol.TryDecodeQuestLogChanged);
    }
}
