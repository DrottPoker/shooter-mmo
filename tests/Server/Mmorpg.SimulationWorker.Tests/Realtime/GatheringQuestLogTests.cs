using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The quest side of a gathering yield off the realtime loop: a yield that counted for a quest
// patches the worker's quest log, and one whose answer may have been lost after it counted makes
// the worker read the log again, so the log never silently misses a committed change.
public sealed class GatheringQuestLogTests
{
    private const string Errand = "quest.fixture.errand";

    [Fact]
    public async Task ACommittedYieldPatchesTheQuestLogAndTheClient()
    {
        using var context = new Context();
        context.GameService.OnItemOperation = (_, request, _) => Task.FromResult(context.Counted(request, 6, ore: 1));

        var completed = await context.GatherAsync(countsForQuests: true);

        Assert.True(completed.Result.Succeeded);
        var change = completed.QuestLogChange!;
        Assert.Equal((Guid.Empty, 5L, 6L, false), (change.OperationId, change.PreviousQuestLogRevision, change.QuestLogRevision, change.ReloadRequired));
        Assert.Equal(1, change.Quests[0].Objectives[0].Progress);
        Assert.Equal(1, context.Log().ActiveQuests[Errand].Progress["ore"]);
        Assert.Empty(context.GameService.QuestLogReads);
    }

    [Fact]
    public async Task ALostAnswerOfAYieldThatCountedReadsTheQuestLogAgain()
    {
        using var context = new Context();
        context.GameService.OnItemOperation = (_, _, _) => throw new HttpRequestException("The answer was lost.");
        context.GameService.OnQuestLog = (_, request, _) => Task.FromResult(ScriptedGameServiceHandler.QuestLog(
            request.CharacterId,
            6,
            Working(1)));

        var completed = await context.GatherAsync(countsForQuests: true);

        Assert.Equal("auth_service_unavailable", completed.Result.Error!.Code);
        var reload = completed.QuestLogChange!;
        Assert.Equal((Guid.Empty, 5L, 6L, true), (reload.OperationId, reload.PreviousQuestLogRevision, reload.QuestLogRevision, reload.ReloadRequired));
        Assert.False(context.QuestLogs.IsStale(context.Session.CharacterId, context.Session.SimulationSessionId));
        Assert.Equal(1, context.Log().ActiveQuests[Errand].Progress["ore"]);
    }

    [Fact]
    public async Task AQuestLogThatCannotBeReadStaysStaleUntilACommittedYieldReadsIt()
    {
        using var context = new Context();
        context.GameService.OnItemOperation = (_, _, _) => throw new HttpRequestException("The answer was lost.");
        context.GameService.OnQuestLog = (_, _, _) => throw new HttpRequestException("GameService is down.");

        var lost = await context.GatherAsync(countsForQuests: true);

        Assert.Null(lost.QuestLogChange);
        Assert.True(context.QuestLogs.IsStale(context.Session.CharacterId, context.Session.SimulationSessionId));

        // The lost yield finished the ore, so the next one changes no quest, and the worker reads
        // the stale log again all the same.
        context.GameService.OnItemOperation = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.CommittedItemOperation(request, 1, 10, 200));
        context.GameService.OnQuestLog = (_, request, _) => Task.FromResult(ScriptedGameServiceHandler.QuestLog(
            request.CharacterId,
            6,
            Working(2)));

        var committed = await context.GatherAsync(countsForQuests: true);

        var reload = committed.QuestLogChange!;
        Assert.Equal((5L, 6L, true), (reload.PreviousQuestLogRevision, reload.QuestLogRevision, reload.ReloadRequired));
        Assert.False(context.QuestLogs.IsStale(context.Session.CharacterId, context.Session.SimulationSessionId));
        Assert.Equal(2, context.Log().ActiveQuests[Errand].Progress["ore"]);

        // A current log is not read again.
        var reads = context.GameService.QuestLogReads.Count;
        await context.GatherAsync(countsForQuests: false);
        Assert.Equal(reads, context.GameService.QuestLogReads.Count);
    }

    [Fact]
    public async Task ALostAnswerOfAYieldThatCountsForNoQuestLeavesTheQuestLogAlone()
    {
        using var context = new Context();
        context.GameService.OnItemOperation = (_, _, _) => throw new HttpRequestException("The answer was lost.");

        var completed = await context.GatherAsync(countsForQuests: false);

        Assert.Null(completed.QuestLogChange);
        Assert.False(context.QuestLogs.IsStale(context.Session.CharacterId, context.Session.SimulationSessionId));
        Assert.Empty(context.GameService.QuestLogReads);
    }

    private static SimulationActiveQuest Working(int ore)
    {
        return new SimulationActiveQuest(Errand, "work", DateTime.UtcNow, [new SimulationQuestObjectiveProgress("ore", ore)]);
    }

    private sealed class Context : IDisposable
    {
        private readonly RealtimeLoopFixture fixture = new(enableWorldActors: true);
        private readonly HttpClient httpClient;
        private readonly GatheringAuthorityOperations operations;
        private readonly GatheringHandler handler;
        private readonly ConnectedPlayer player;
        private readonly WorldActorRuntimeState vein;

        public Context()
        {
            httpClient = new HttpClient(GameService) { BaseAddress = new Uri("http://game-service.test") };
            var client = new GameServiceClient(httpClient);
            player = fixture.ConnectPlayer(1);
            Session = player.Session;
            operations = new GatheringAuthorityOperations(
                new SimulationGatheringService(client),
                fixture.Completions,
                NullLogger.Instance,
                new QuestOperationExecutor(
                    client,
                    new CharacterItemStateStore(),
                    new ActiveSimulationSessionStore(),
                    QuestLogs,
                    RealtimeServerHarness.QuestCatalog));
            handler = fixture.CreateGatheringHandler();
            vein = fixture.WorldActors!.ListActors().Single(actor => actor.Definition.Kind == WorldActorKindIds.ResourceNode);
            QuestLogs.Register(
                Session.CharacterId,
                Session.SimulationSessionId,
                CharacterQuestLog.From(new SimulationQuestLogResponse(
                    Session.CharacterId,
                    RealtimeServerHarness.QuestCatalog.Revision,
                    5,
                    [Working(0)],
                    [])));
        }

        public ScriptedGameServiceHandler GameService { get; } = new();

        public CharacterQuestLogStore QuestLogs { get; } = new();

        public ActiveSimulationSession Session { get; }

        public CharacterQuestLog Log()
        {
            Assert.True(QuestLogs.TryGet(Session.CharacterId, Session.SimulationSessionId, out var log));
            return log!;
        }

        public async Task<GatheringCycleCompleted> GatherAsync(bool countsForQuests)
        {
            await operations.RunAsync(
                handler,
                player.Context,
                Session,
                vein,
                vein.ResourceGeneration,
                new SimulationGatheringRequest(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "resource.mine",
                    vein.Spawn.Id,
                    vein.ResourceProfile!,
                    countsForQuests ? vein.Definition.Id : null));
            Assert.True(fixture.Completions.TryDequeue(out var completion));
            return Assert.IsType<GatheringCycleCompleted>(completion.Operation);
        }

        // A committed yield that brought the errand's ore to the given count.
        public HttpResponseMessage Counted(SimulationItemOperationRequest request, long questLogRevision, int ore)
        {
            return ScriptedGameServiceHandler.Json(new SimulationItemTransactionResponse(
                request.OperationId,
                request.OperationKind,
                true,
                null,
                [new SimulationItemCharacterRevision(request.CharacterId, 1, 10, 200)],
                [],
                [],
                [],
                CharacterStateTestData.Create(request.CharacterId, 1, 10, 200),
                null,
                new SimulationQuestLogChange(
                    request.CharacterId,
                    questLogRevision,
                    [new SimulationQuestChange(Errand, "work", [new SimulationQuestObjectiveProgress("ore", ore)], 0)])));
        }

        public void Dispose()
        {
            httpClient.Dispose();
            fixture.Dispose();
        }
    }
}
