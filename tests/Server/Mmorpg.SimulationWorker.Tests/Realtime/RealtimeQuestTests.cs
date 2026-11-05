using System.Net;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The worker keeps every player's quest log from the join on, runs quest actions at quest NPCs
// and from the quest log through GameService, and sends each committed quest change before the
// result of the action that caused it.
public sealed class RealtimeQuestTests
{
    private const string Wolves = "quest.fixture.wolves";

    [Fact]
    public async Task JoinKeepsThePlayersQuestLogAndBotsNeverReadOne()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions { EnableQuests = true });
        var player = harness.AddCharacter("Quest Keeper");
        var bot = harness.AddCharacter("Quest Bot", isSyntheticBot: true);
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.QuestLog(request.CharacterId, 3, ActiveWolves(1)));
        await harness.StartAsync();

        await harness.JoinAsync(player);
        await harness.JoinAsync(bot);

        var read = Assert.Single(harness.GameService.QuestLogReads);
        Assert.Equal(player.CharacterId, read.CharacterId);
        Assert.Equal(player.SimulationSessionToken, read.SessionToken);
        Assert.True(harness.QuestLogs!.TryGet(player.CharacterId, player.SimulationSessionId, out var log));
        Assert.Equal(3, log!.Revision);
        Assert.Equal("hunt", log.ActiveQuests[Wolves].StepId);
        Assert.Equal(1, log.ActiveQuests[Wolves].Progress["wolves"]);
        Assert.False(harness.QuestLogs.TryGet(bot.CharacterId, bot.SimulationSessionId, out _));
    }

    [Fact]
    public async Task AJoinWithoutItsQuestLogIsRejectedAndReleased()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions { EnableQuests = true });
        var unavailable = harness.AddCharacter("Unread Quests");
        var mismatched = harness.AddCharacter("Other Quests");
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(
            request.CharacterId == unavailable.CharacterId
                ? ScriptedGameServiceHandler.Problem(HttpStatusCode.ServiceUnavailable, "database_unavailable", "Try again.")
                : ScriptedGameServiceHandler.Json(new SimulationQuestLogResponse(request.CharacterId, new string('a', 64), 0, [], [])));
        await harness.StartAsync();

        var first = harness.Connect(unavailable.Name, unavailable.Ticket);
        var second = harness.Connect(mismatched.Name, mismatched.Ticket);
        await harness.PollUntilAsync(
            () => first.JoinRejected is not null && second.JoinRejected is not null,
            "both joins to be rejected");

        Assert.Equal("database_unavailable", first.JoinRejected!.Code);
        Assert.Equal("simulation_worker_quest_catalog_mismatch", second.JoinRejected!.Code);
        Assert.Equal(2, harness.GameService.ReleaseCount);
        Assert.False(harness.QuestLogs!.TryGet(unavailable.CharacterId, unavailable.SimulationSessionId, out _));
    }

    [Fact]
    public async Task AbandoningSendsTheQuestLogChangeBeforeTheResult()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions { EnableQuests = true });
        var character = harness.AddCharacter("Quest Quitter");
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.QuestLog(request.CharacterId, 3, ActiveWolves(1)));
        harness.GameService.OnQuestOperation = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.CommittedQuestOperation(
                request,
                0,
                4,
                new SimulationQuestChange(Wolves, null, [], 0)));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        var intent = new RealtimeQuestIntent(Guid.NewGuid(), RealtimeQuestIntentKind.Abandon, Wolves);
        client.Send(RealtimeProtocol.EncodeQuestIntent(intent));
        await harness.PollUntilAsync(() => QuestResults(client).Count == 1, "the abandon result");

        var operation = Assert.Single(harness.GameService.QuestOperations);
        Assert.Equal(intent.OperationId, operation.OperationId);
        Assert.Equal("abandon_quest", operation.OperationKind);
        Assert.Equal(Wolves, operation.QuestId);
        Assert.Null(operation.QuestNpcActorId);
        var change = Assert.Single(QuestChanges(client));
        Assert.Equal(intent.OperationId, change.OperationId);
        Assert.Equal((3L, 4L, false), (change.PreviousQuestLogRevision, change.QuestLogRevision, change.ReloadRequired));
        var quest = Assert.Single(change.Quests);
        Assert.Equal((Wolves, string.Empty, 0), (quest.QuestId, quest.StepId, quest.CompletionCount));
        Assert.True(
            client.IndexOfFirst(RealtimeMessageType.QuestLogChanged)
                < client.IndexOfFirst(RealtimeMessageType.QuestIntentResult));
        var result = Assert.Single(QuestResults(client));
        Assert.True(result.Succeeded);
        Assert.Equal(intent.OperationId, result.OperationId);
        Assert.True(harness.QuestLogs!.TryGet(character.CharacterId, character.SimulationSessionId, out var log));
        Assert.Equal(4, log!.Revision);
        Assert.Empty(log.ActiveQuests);
    }

    [Fact]
    public async Task ARefusedQuestIntentKeepsTheSessionAndItsQuestLog()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions { EnableQuests = true });
        var character = harness.AddCharacter("Quest Stubborn");
        harness.GameService.OnQuestOperation = (_, _, _) => Task.FromResult(
            ScriptedGameServiceHandler.Problem(HttpStatusCode.Conflict, "quest_not_active", "The quest is not active."));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.Send(RealtimeProtocol.EncodeQuestIntent(
            new RealtimeQuestIntent(Guid.NewGuid(), RealtimeQuestIntentKind.Abandon, Wolves)));
        await harness.PollUntilAsync(() => QuestResults(client).Count == 1, "the refused abandon");

        var result = Assert.Single(QuestResults(client));
        Assert.False(result.Succeeded);
        Assert.Equal("quest_not_active", result.Error!.Code);
        Assert.Empty(QuestChanges(client));
        Assert.Null(client.ServerDisconnect);
        Assert.True(harness.QuestLogs!.TryGet(character.CharacterId, character.SimulationSessionId, out var log));
        Assert.Equal(0, log!.Revision);
    }

    [Fact]
    public async Task AQuestActionAtTheQuestNpcNamesTheNpcAndPatchesTheQuestLog()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            EnableQuests = true
        });
        var character = harness.AddCharacter("Quest Taker");
        harness.GameService.OnQuestOperation = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.CommittedQuestOperation(
                request,
                0,
                1,
                new SimulationQuestChange(Wolves, "hunt", [new SimulationQuestObjectiveProgress("wolves", 0)], 0)));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        var services = await RealtimeWorldActorInteractionTests.WaitForServicesAsync(harness, client);
        client.SendWorldIntent(RealtimeWorldActorInteractionTests.CreateOpen(character.SimulationSessionId, services));
        await harness.PollUntilAsync(() => client.WorldOpened.Count == 1, "the open");
        var opened = client.WorldOpened[0];
        var quests = opened.Capabilities.Single(value => value.Kind == RealtimeWorldActorCapabilityKind.Quests);

        var accept = RealtimeWorldInteractionIntent.CreateCapabilityAction(
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
            RealtimeCapabilityActionKind.AcceptQuest,
            questId: Wolves);
        client.SendWorldIntent(accept);
        await harness.PollUntilAsync(() => client.WorldResults.Count == 1, "the accept result");

        var operation = Assert.Single(harness.GameService.QuestOperations);
        Assert.Equal(("accept_quest", Wolves, "npc.city_services"), (operation.OperationKind, operation.QuestId, operation.QuestNpcActorId));
        Assert.Null(operation.ObjectiveId);
        var change = Assert.Single(QuestChanges(client));
        Assert.Equal((0L, 1L), (change.PreviousQuestLogRevision, change.QuestLogRevision));
        Assert.Equal(("hunt", 0), (change.Quests[0].StepId, change.Quests[0].Objectives[0].Progress));
        Assert.True(
            client.IndexOfFirst(RealtimeMessageType.QuestLogChanged)
                < client.IndexOfFirst(RealtimeMessageType.WorldInteractionResult));
        var result = Assert.Single(client.WorldResults);
        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal("Quest accepted.", result.Message);
    }

    private static SimulationActiveQuest ActiveWolves(int progress)
    {
        return new SimulationActiveQuest(
            Wolves,
            "hunt",
            DateTime.UtcNow,
            [new SimulationQuestObjectiveProgress("wolves", progress)]);
    }

    private static IReadOnlyList<RealtimeQuestIntentResult> QuestResults(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeQuestIntentResult>(
            RealtimeMessageType.QuestIntentResult,
            RealtimeProtocol.TryDecodeQuestIntentResult);
    }

    private static IReadOnlyList<RealtimeQuestLogChanged> QuestChanges(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeQuestLogChanged>(
            RealtimeMessageType.QuestLogChanged,
            RealtimeProtocol.TryDecodeQuestLogChanged);
    }
}
