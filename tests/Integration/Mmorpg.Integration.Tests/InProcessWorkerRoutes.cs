using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Quests;
using Mmorpg.GameService.Testing;
using ServerCheckpointRequest = Mmorpg.GameService.Placement.SimulationCharacterCheckpointRequest;
using ServerConsumeRequest = Mmorpg.GameService.Placement.ConsumeSimulationJoinTicketRequest;
using ServerItemOperationRequest = Mmorpg.GameService.Items.SimulationItemOperationRequest;
using ServerMobLootGrantRequest = Mmorpg.GameService.Items.SimulationMobLootGrantRequest;
using ServerQuestDropRequest = Mmorpg.GameService.Quests.SimulationQuestDropRequest;
using ServerQuestLogRequest = Mmorpg.GameService.Quests.SimulationQuestLogRequest;
using ServerQuestOperationRequest = Mmorpg.GameService.Quests.SimulationQuestOperationRequest;
using ServerReleaseRequest = Mmorpg.GameService.Placement.SimulationSessionReleaseRequest;

namespace Mmorpg.Integration.Tests;

// Serves the worker's join ticket consume, session release, checkpoint, item operations, quest log
// read, quest operations, quest drop read, and live Mob loot grants with the GameService
// services, the routes' request types, and their HTTP result mapping, without a web host, so the
// worker's own requests and response validation meet the real routes.
internal sealed class InProcessWorkerRoutes(PostgresIntegrationTestContext context, string workerId)
    : HttpMessageHandler
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(HttpMethod.Post, request.Method);
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        await using var scope = context.Services.CreateAsyncScope();
        if (request.RequestUri.AbsolutePath == "/api/simulation-join-tickets/consume")
        {
            return await RespondAsync(
                scope,
                (await scope.ServiceProvider.GetRequiredService<ShardService>().ConsumeJoinTicketAsync(
                    JsonSerializer.Deserialize<ServerConsumeRequest>(body, WebJson)!,
                    cancellationToken)).ToHttpResult());
        }

        Assert.Equal(4, segments.Length);
        if (segments[1] == "simulation-workers")
        {
            Assert.Equal(workerId, segments[2]);
            Assert.Equal("checkpoint", segments[3]);
            return await RespondAsync(
                scope,
                (await scope.ServiceProvider.GetRequiredService<SimulationSessionService>().CheckpointAsync(
                    workerId,
                    JsonSerializer.Deserialize<ServerCheckpointRequest>(body, WebJson)!,
                    cancellationToken)).ToHttpResult());
        }

        Assert.Equal("simulation-sessions", segments[1]);
        var simulationSessionId = Guid.Parse(segments[2]);
        IResult result = segments[3] switch
        {
            "release" => (await scope.ServiceProvider.GetRequiredService<SimulationSessionService>().ReleaseAsync(
                simulationSessionId,
                workerId,
                JsonSerializer.Deserialize<ServerReleaseRequest>(body, WebJson)!,
                cancellationToken)).ToHttpResult(),
            "item-operations" => (await scope.ServiceProvider.GetRequiredService<SimulationItemMutationService>().ExecuteAsync(
                workerId,
                simulationSessionId,
                JsonSerializer.Deserialize<ServerItemOperationRequest>(body, WebJson)!,
                cancellationToken)).ToHttpResult(),
            "quest-log" => (await scope.ServiceProvider.GetRequiredService<SimulationQuestLogService>().ReadAsync(
                workerId,
                simulationSessionId,
                JsonSerializer.Deserialize<ServerQuestLogRequest>(body, WebJson)!,
                cancellationToken)).ToHttpResult(),
            "quest-operations" => (await scope.ServiceProvider.GetRequiredService<SimulationQuestMutationService>().ExecuteAsync(
                workerId,
                simulationSessionId,
                JsonSerializer.Deserialize<ServerQuestOperationRequest>(body, WebJson)!,
                cancellationToken)).ToHttpResult(),
            "quest-drops" => (await scope.ServiceProvider.GetRequiredService<SimulationQuestDropService>().ResolveAsync(
                workerId,
                simulationSessionId,
                JsonSerializer.Deserialize<ServerQuestDropRequest>(body, WebJson)!,
                cancellationToken)).ToHttpResult(),
            "mob-loot-grants" => (await scope.ServiceProvider.GetRequiredService<CorpseService>().GrantLiveMobLootAsync(
                workerId,
                simulationSessionId,
                JsonSerializer.Deserialize<ServerMobLootGrantRequest>(body, WebJson)!,
                cancellationToken)).ToHttpResult(),
            _ => throw new InvalidOperationException("The worker flow tests call no other route: " + segments[3])
        };

        return await RespondAsync(scope, result);
    }

    private static async Task<HttpResponseMessage> RespondAsync(AsyncServiceScope scope, IResult result)
    {
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
