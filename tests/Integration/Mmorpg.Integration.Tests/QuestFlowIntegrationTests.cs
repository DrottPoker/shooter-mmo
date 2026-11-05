using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameProtocol;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Quests;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.Testing;
using ServerJoinTicket = Mmorpg.GameService.Placement.ConsumedSimulationJoinTicketResponse;
using ServerQuestLogRequest = Mmorpg.GameService.Quests.SimulationQuestLogRequest;
using ServerQuestOperationRequest = Mmorpg.GameService.Items.SimulationQuestOperationRequest;
using WorkerJoinTicket = Mmorpg.SimulationWorker.GameServiceApi.ConsumedSimulationJoinTicketResponse;
using WorkerQuestLog = Mmorpg.SimulationWorker.Quests.CharacterQuestLog;
using WorkerQuestLogRequest = Mmorpg.SimulationWorker.GameServiceApi.SimulationQuestLogRequest;

namespace Mmorpg.Integration.Tests;

// The worker's quest log read and quest actions against the GameService quest routes, through
// the routes' JSON contracts and HTTP result mapping: the worker reads the joining character's
// quest log, accepts the fixture errand at Mira, which grants its letter, and abandons it, which
// takes the letter back.
public sealed class QuestFlowIntegrationTests
{
    private const string ShardId = "local-shard-1";
    private const string WorkerId = "local-simulation-worker-1";
    private const string WorkerRuntimeId = "integration-worker-runtime";
    private const string Errand = "quest.fixture.errand";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [PostgresIntegrationFact]
    public async Task TheWorkerKeepsTheQuestLogThroughAnAcceptAndAnAbandon()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync("quest-flow@example.com", "quest_flow", "Quest Flow");
        await CompleteWolvesAsync(context, player.Character.Id);
        var session = await JoinAsync(context, player);
        using var httpClient = new HttpClient(new InProcessQuestRoutes(context))
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var client = new GameServiceClient(httpClient);

        var read = await client.ReadSimulationQuestLogAsync(
            session.SimulationSessionId,
            new WorkerQuestLogRequest(
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken),
            cancellationToken);
        Assert.True(read.Succeeded, read.Error?.Code + ": " + read.Error?.Message);
        Assert.Equal(context.Services.GetRequiredService<IQuestDefinitions>().Revision, read.Value!.QuestCatalogRevision);
        Assert.Empty(read.Value.ActiveQuests);
        Assert.Equal(1, Assert.Single(read.Value.CompletedQuests).CompletionCount);

        var sessions = new ActiveSimulationSessionStore();
        sessions.Register(session);
        var items = new CharacterItemStateStore();
        items.Register(
            session.CharacterId,
            session.SimulationSessionId,
            CharacterItemState.Create(session.CarryState, []),
            out _);
        var questLogs = new CharacterQuestLogStore();
        questLogs.Register(session.CharacterId, session.SimulationSessionId, WorkerQuestLog.From(read.Value));
        var executor = new QuestOperationExecutor(client, items, sessions, questLogs);

        var refused = await executor.ExecuteAsync(
            session,
            new QuestOperation(Guid.NewGuid(), QuestOperationKinds.Accept, Errand, "npc.city_guard"),
            cancellationToken);
        Assert.False(refused.Succeeded);
        Assert.Equal("quest_npc_access_required", refused.Code);
        Assert.False(refused.ShouldDisconnect);

        var accepted = await executor.ExecuteAsync(
            session,
            new QuestOperation(Guid.NewGuid(), QuestOperationKinds.Accept, Errand, "npc.city_services"),
            cancellationToken);
        Assert.True(accepted.Succeeded, accepted.Code + ": " + accepted.Message);
        var letter = Assert.Single(accepted.ItemStateChange!.Items);
        Assert.Equal(("fixture.letter", RealtimeItemStateLocationKind.Container), (letter.DefinitionId, letter.Location));
        Assert.Equal(read.Value.QuestLogRevision, accepted.QuestLogChange!.PreviousQuestLogRevision);
        var errand = Assert.Single(accepted.QuestLogChange.Quests);
        Assert.Equal((Errand, "work"), (errand.QuestId, errand.StepId));
        Assert.Equal(
            ["hand_over=0", "ore=0", "report=0"],
            errand.Objectives.Select(objective => objective.ObjectiveId + "=" + objective.Progress));

        var abandoned = await executor.ExecuteAsync(
            session,
            new QuestOperation(Guid.NewGuid(), QuestOperationKinds.Abandon, Errand),
            cancellationToken);
        Assert.True(abandoned.Succeeded, abandoned.Code + ": " + abandoned.Message);
        var removed = Assert.Single(abandoned.ItemStateChange!.Items);
        Assert.Equal((letter.ItemInstanceId, RealtimeItemStateLocationKind.Removed), (removed.ItemInstanceId, removed.Location));
        Assert.Equal(accepted.QuestLogChange.QuestLogRevision, abandoned.QuestLogChange!.PreviousQuestLogRevision);
        Assert.Equal(string.Empty, Assert.Single(abandoned.QuestLogChange.Quests).StepId);

        Assert.True(questLogs.TryGet(session.CharacterId, session.SimulationSessionId, out var kept));
        Assert.Empty(kept!.ActiveQuests);
        Assert.Equal(await QuestLogRevisionAsync(context, session.CharacterId), kept.Revision);
        Assert.True(items.TryGet(session.CharacterId, session.SimulationSessionId, out var carry));
        Assert.Equal(abandoned.ItemStateRevision, carry!.ItemStateRevision);
    }

    private static async Task CompleteWolvesAsync(PostgresIntegrationTestContext context, Guid characterId)
    {
        var questLog = context.Services.GetRequiredService<ICharacterQuestLog>();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var scope = new PostgresTransactionScope(connection, transaction);
        await questLog.AcceptAsync(scope, characterId, "quest.fixture.wolves", "hunt", ["wolves"], CancellationToken.None);
        await questLog.CompleteAsync(scope, characterId, "quest.fixture.wolves", CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static async Task<long> QuestLogRevisionAsync(PostgresIntegrationTestContext context, Guid characterId)
    {
        var questLog = context.Services.GetRequiredService<ICharacterQuestLog>();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var state = await questLog.ReadAsync(
            new PostgresTransactionScope(connection, transaction),
            characterId,
            CancellationToken.None);
        await transaction.CommitAsync();
        return state.Revision;
    }

    // The worker session comes from the join response as the worker reads it.
    private static async Task<ActiveSimulationSession> JoinAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await context.ShardService.ConsumeJoinTicketAsync(
            new Mmorpg.GameService.Placement.ConsumeSimulationJoinTicketRequest(
                join.Value!.JoinTicket,
                WorkerId,
                WorkerRuntimeId,
                ShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        var ticket = JsonSerializer.Deserialize<WorkerJoinTicket>(
            JsonSerializer.Serialize<ServerJoinTicket>(consumed.Value!, WebJson),
            WebJson)!;
        return ActiveSimulationSession.FromJoinTicket(ticket, DateTime.UtcNow);
    }

    // Serves the worker's quest log read and quest operations with the GameService services, the
    // routes' request types, and their HTTP result mapping, without a web host.
    private sealed class InProcessQuestRoutes(PostgresIntegrationTestContext context) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(4, segments.Length);
            Assert.Equal("simulation-sessions", segments[1]);
            var simulationSessionId = Guid.Parse(segments[2]);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            await using var scope = context.Services.CreateAsyncScope();
            IResult result = segments[3] switch
            {
                "quest-log" => (await scope.ServiceProvider.GetRequiredService<SimulationQuestLogService>().ReadAsync(
                    WorkerId,
                    simulationSessionId,
                    JsonSerializer.Deserialize<ServerQuestLogRequest>(body, WebJson)!,
                    cancellationToken)).ToHttpResult(),
                "quest-operations" => (await scope.ServiceProvider.GetRequiredService<SimulationQuestMutationService>().ExecuteAsync(
                    WorkerId,
                    simulationSessionId,
                    JsonSerializer.Deserialize<ServerQuestOperationRequest>(body, WebJson)!,
                    cancellationToken)).ToHttpResult(),
                _ => throw new InvalidOperationException("The quest flow calls no other route: " + segments[3])
            };

            var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            var responseBody = new MemoryStream();
            httpContext.Response.Body = responseBody;
            await result.ExecuteAsync(httpContext);
            var response = new HttpResponseMessage((HttpStatusCode)httpContext.Response.StatusCode)
            {
                Content = new ByteArrayContent(responseBody.ToArray())
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                httpContext.Response.ContentType ?? "application/json");
            return response;
        }
    }
}
