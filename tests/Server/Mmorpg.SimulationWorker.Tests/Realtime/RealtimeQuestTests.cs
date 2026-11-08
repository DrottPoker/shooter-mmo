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

        var intent = RealtimeQuestIntent.CreateAbandon(Guid.NewGuid(), Wolves);
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

    // The Admin menu's quest tools run as quest operations with their quest and objective, and
    // resetting every quest names none.
    [Fact]
    public async Task AdminQuestToolsRunThroughTheQuestRoute()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableQuests = true,
            EnableAdminTools = true
        });
        var character = harness.AddCharacter("Quest Admin");
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.QuestLog(request.CharacterId, 3, ActiveWolves(1)));
        harness.GameService.OnQuestOperation = (_, request, _) => Task.FromResult(
            request.OperationKind == "admin_reset_quests"
                ? ScriptedGameServiceHandler.CommittedQuestOperation(request, 0, 5, new SimulationQuestChange(Wolves, null, [], 0))
                : ScriptedGameServiceHandler.CommittedQuestOperation(
                    request,
                    0,
                    4,
                    new SimulationQuestChange(Wolves, "hunt", [new SimulationQuestObjectiveProgress("wolves", 2)], 0)));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.Send(RealtimeProtocol.EncodeQuestIntent(
            RealtimeQuestIntent.CreateAdminCompleteObjective(Guid.NewGuid(), Wolves, "wolves")));
        await harness.PollUntilAsync(() => QuestResults(client).Count == 1, "the completed objective");
        client.Send(RealtimeProtocol.EncodeQuestIntent(RealtimeQuestIntent.CreateAdminResetAll(Guid.NewGuid())));
        await harness.PollUntilAsync(() => QuestResults(client).Count == 2, "the reset");

        Assert.Equal(
            [("admin_complete_quest_objective", Wolves, "wolves"), ("admin_reset_quests", (string?)null, (string?)null)],
            harness.GameService.QuestOperations.Select(operation => (operation.OperationKind, operation.QuestId, operation.ObjectiveId)));
        Assert.All(QuestResults(client), result => Assert.True(result.Succeeded, result.Error?.Code));
        Assert.Equal([4L, 5L], QuestChanges(client).Select(change => change.QuestLogRevision));
        Assert.True(harness.QuestLogs!.TryGet(character.CharacterId, character.SimulationSessionId, out var log));
        Assert.Equal(5, log!.Revision);
        Assert.Empty(log.ActiveQuests);
    }

    [Fact]
    public async Task AWorkerWithoutTheAdminToolsRefusesTheQuestToolsButNotAbandoning()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions { EnableQuests = true });
        var character = harness.AddCharacter("Quest Visitor");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.Send(RealtimeProtocol.EncodeQuestIntent(RealtimeQuestIntent.CreateAdminAccept(Guid.NewGuid(), Wolves)));
        await harness.PollUntilAsync(() => QuestResults(client).Count == 1, "the refused tool");

        var result = Assert.Single(QuestResults(client));
        Assert.Equal((false, "admin_tools_disabled"), (result.Succeeded, result.Error!.Code));
        Assert.Equal(RealtimeQuestIntentKind.AdminAccept, result.Kind);
        Assert.Empty(harness.GameService.QuestOperations);
        Assert.Null(client.ServerDisconnect);
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
            RealtimeQuestIntent.CreateAbandon(Guid.NewGuid(), Wolves)));
        await harness.PollUntilAsync(() => QuestResults(client).Count == 1, "the refused abandon");

        var result = Assert.Single(QuestResults(client));
        Assert.False(result.Succeeded);
        Assert.Equal("quest_not_active", result.Error!.Code);
        Assert.Empty(QuestChanges(client));
        Assert.Null(client.ServerDisconnect);
        Assert.True(harness.QuestLogs!.TryGet(character.CharacterId, character.SimulationSessionId, out var log));
        Assert.Equal(0, log!.Revision);
    }

    // Every quest operation names the quest catalog the worker runs, and GameService refusing it
    // because it runs another catalog ends the session, since no quest progress can build on it.
    [Fact]
    public async Task AQuestOperationRefusedForAnotherQuestCatalogEndsTheSession()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions { EnableQuests = true });
        var character = harness.AddCharacter("Quest Outdated");
        harness.GameService.OnQuestOperation = (_, _, _) => Task.FromResult(
            ScriptedGameServiceHandler.Problem(
                HttpStatusCode.Conflict,
                "simulation_worker_quest_catalog_mismatch",
                "The simulation worker runs another quest catalog than GameService."));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.Send(RealtimeProtocol.EncodeQuestIntent(RealtimeQuestIntent.CreateAbandon(Guid.NewGuid(), Wolves)));
        await harness.PollUntilAsync(() => client.ServerDisconnect is not null, "the ended session");

        var operation = Assert.Single(harness.GameService.QuestOperations);
        Assert.Equal(RealtimeServerHarness.QuestCatalog.Revision, operation.QuestCatalogRevision);
        Assert.Equal("simulation_worker_quest_catalog_mismatch", client.ServerDisconnect!.Code);
    }

    // A lost answer asks again with the same operation, which replays a committed result.
    [Fact]
    public async Task AQuestActionWhoseAnswerIsLostAsksAgainWithTheSameOperation()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions { EnableQuests = true });
        var character = harness.AddCharacter("Quest Retrier");
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.QuestLog(request.CharacterId, 3, ActiveWolves(1)));
        var attempts = 0;
        harness.GameService.OnQuestOperation = (_, request, _) => Interlocked.Increment(ref attempts) == 1
            ? throw new HttpRequestException("The answer was lost.")
            : Task.FromResult(ScriptedGameServiceHandler.CommittedQuestOperation(
                request,
                0,
                4,
                new SimulationQuestChange(Wolves, null, [], 0)));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.Send(RealtimeProtocol.EncodeQuestIntent(
            RealtimeQuestIntent.CreateAbandon(Guid.NewGuid(), Wolves)));
        await harness.PollUntilAsync(() => QuestResults(client).Count == 1, "the abandon result");

        Assert.True(Assert.Single(QuestResults(client)).Succeeded);
        Assert.Equal(2, harness.GameService.QuestOperations.Count);
        Assert.Single(harness.GameService.QuestOperations.Select(operation => operation.OperationId).Distinct());
        var change = Assert.Single(QuestChanges(client));
        Assert.Equal((3L, 4L, false), (change.PreviousQuestLogRevision, change.QuestLogRevision, change.ReloadRequired));
    }

    // An action that may have committed without an answer reads the quest log again, and the
    // client reloads its own; a log that cannot be read is read before the next action, which
    // waits for it.
    [Fact]
    public async Task AnUncertainQuestActionReadsTheQuestLogAgainBeforeTheNextOne()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions { EnableQuests = true });
        var character = harness.AddCharacter("Quest Uncertain");
        var logRevision = 3L;
        var logAvailable = true;
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(logAvailable
            ? ScriptedGameServiceHandler.QuestLog(request.CharacterId, Interlocked.Read(ref logRevision), ActiveWolves(1))
            : ScriptedGameServiceHandler.Problem(HttpStatusCode.ServiceUnavailable, "database_unavailable", "Try again."));
        var committing = true;
        harness.GameService.OnQuestOperation = (_, request, _) => committing
            ? throw new HttpRequestException("The answer was lost.")
            : Task.FromResult(ScriptedGameServiceHandler.CommittedQuestOperation(
                request,
                0,
                Interlocked.Read(ref logRevision) + 1,
                new SimulationQuestChange(Wolves, null, [], 0)));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        // The action committed behind a lost answer, and GameService's log now holds it.
        Interlocked.Exchange(ref logRevision, 4);
        client.Send(RealtimeProtocol.EncodeQuestIntent(
            RealtimeQuestIntent.CreateAbandon(Guid.NewGuid(), Wolves)));
        await harness.PollUntilAsync(() => QuestResults(client).Count == 1, "the uncertain result");

        Assert.Equal("auth_service_unavailable", QuestResults(client)[0].Error!.Code);
        var reload = Assert.Single(QuestChanges(client));
        Assert.Equal((3L, 4L, true), (reload.PreviousQuestLogRevision, reload.QuestLogRevision, reload.ReloadRequired));
        Assert.Empty(reload.Quests);
        Assert.False(harness.QuestLogs!.IsStale(character.CharacterId, character.SimulationSessionId));

        // Neither the answer nor the log comes through, so the next action first reads the log.
        logAvailable = false;
        client.Send(RealtimeProtocol.EncodeQuestIntent(
            RealtimeQuestIntent.CreateAbandon(Guid.NewGuid(), Wolves)));
        await harness.PollUntilAsync(() => QuestResults(client).Count == 2, "the second uncertain result");
        Assert.True(harness.QuestLogs.IsStale(character.CharacterId, character.SimulationSessionId));
        var operations = harness.GameService.QuestOperations.Count;

        client.Send(RealtimeProtocol.EncodeQuestIntent(
            RealtimeQuestIntent.CreateAbandon(Guid.NewGuid(), Wolves)));
        await harness.PollUntilAsync(() => QuestResults(client).Count == 3, "the refused action");
        Assert.Equal("quest_log_unavailable", QuestResults(client)[2].Error!.Code);
        Assert.Equal(operations, harness.GameService.QuestOperations.Count);

        Interlocked.Exchange(ref logRevision, 6);
        logAvailable = true;
        committing = false;
        client.Send(RealtimeProtocol.EncodeQuestIntent(
            RealtimeQuestIntent.CreateAbandon(Guid.NewGuid(), Wolves)));
        await harness.PollUntilAsync(() => QuestResults(client).Count == 4, "the committed action");

        Assert.True(QuestResults(client)[3].Succeeded);
        var after = QuestChanges(client)[^1];
        Assert.Equal((4L, 7L, true), (after.PreviousQuestLogRevision, after.QuestLogRevision, after.ReloadRequired));
        Assert.False(harness.QuestLogs.IsStale(character.CharacterId, character.SimulationSessionId));
        Assert.True(harness.QuestLogs.TryGet(character.CharacterId, character.SimulationSessionId, out var log));
        Assert.Equal(7, log!.Revision);
        Assert.Empty(log.ActiveQuests);
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

    // A failed action at the NPC still tells the client to read the quest log the worker read again.
    [Fact]
    public async Task AnUncertainQuestActionAtTheQuestNpcStillTellsTheClientToReload()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            EnableQuests = true
        });
        var character = harness.AddCharacter("Quest Lost Answer");
        var logRevision = 0L;
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.QuestLog(request.CharacterId, Interlocked.Read(ref logRevision)));
        harness.GameService.OnQuestOperation = (_, _, _) => throw new HttpRequestException("The answer was lost.");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        var services = await RealtimeWorldActorInteractionTests.WaitForServicesAsync(harness, client);
        client.SendWorldIntent(RealtimeWorldActorInteractionTests.CreateOpen(character.SimulationSessionId, services));
        await harness.PollUntilAsync(() => client.WorldOpened.Count == 1, "the open");
        var opened = client.WorldOpened[0];
        var quests = opened.Capabilities.Single(value => value.Kind == RealtimeWorldActorCapabilityKind.Quests);

        Interlocked.Exchange(ref logRevision, 1);
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
            RealtimeCapabilityActionKind.AcceptQuest,
            questId: Wolves));
        await harness.PollUntilAsync(() => client.WorldResults.Count == 1, "the uncertain accept");

        var result = Assert.Single(client.WorldResults);
        Assert.False(result.Succeeded);
        Assert.Equal("auth_service_unavailable", result.Error!.Code);
        var reload = Assert.Single(QuestChanges(client));
        Assert.Equal((0L, 1L, true), (reload.PreviousQuestLogRevision, reload.QuestLogRevision, reload.ReloadRequired));
        Assert.True(
            client.IndexOfFirst(RealtimeMessageType.QuestLogChanged)
                < client.IndexOfFirst(RealtimeMessageType.WorldInteractionResult));
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
