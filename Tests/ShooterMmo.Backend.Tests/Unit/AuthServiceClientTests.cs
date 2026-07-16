using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using SimulationWorker.Auth;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class AuthServiceClientTests
{
    [Fact]
    public async Task ConsumeJoinTicketSendsExactWorkerRuntimeAndShardBinding()
    {
        var simulationSessionId = Guid.NewGuid();
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            accountId = Guid.NewGuid(),
            characterId = Guid.NewGuid(),
            characterName = "Hero One",
            shardId = "local-shard-1",
            worldId = "local-world-1",
            workerId = "local-simulation-worker-1",
            workerRuntimeId = "runtime-1",
            simulationSessionId,
            simulationSessionToken = "simulation-session-token",
            sessionExpiresAt = DateTime.UtcNow.AddMinutes(1),
            itemStateRevision = 12,
            carriedWeight = 210,
            carryCapacity = 200,
            isReconnect = false
        }));
        var client = CreateClient(handler);

        var result = await ConsumeAsync(client);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(simulationSessionId, result.Value!.SimulationSessionId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/simulation-join-tickets/consume", request.Path);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("join-ticket", body.RootElement.GetProperty("ticket").GetString());
        Assert.Equal(
            "local-simulation-worker-1",
            body.RootElement.GetProperty("workerId").GetString());
        Assert.Equal("runtime-1", body.RootElement.GetProperty("runtimeId").GetString());
        Assert.Equal("local-shard-1", body.RootElement.GetProperty("shardId").GetString());
    }

    [Fact]
    public async Task SimulationSessionLifecycleUsesRuntimeBoundRoutes()
    {
        var simulationSessionId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var handler = new RecordingHttpMessageHandler(request => CreateJsonResponse(new
        {
            simulationSessionId,
            characterId,
            shardId = "local-shard-1",
            workerId = "local-simulation-worker-1",
            workerRuntimeId = "runtime-1",
            expiresAt = DateTime.UtcNow.AddMinutes(1),
            itemStateRevision = 12,
            carriedWeight = 210,
            carryCapacity = 200,
            released = request.RequestUri!.AbsolutePath.EndsWith(
                "/release",
                StringComparison.Ordinal)
        }));
        var client = CreateClient(handler);

        var heartbeat = await client.HeartbeatSimulationSessionAsync(
            simulationSessionId,
            "runtime-1",
            "simulation-session-token",
            CancellationToken.None);
        var release = await client.ReleaseSimulationSessionAsync(
            simulationSessionId,
            "runtime-1",
            "simulation-session-token",
            CancellationToken.None);

        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.False(heartbeat.Value!.Released);
        Assert.True(release.Succeeded, release.Error?.Message);
        Assert.True(release.Value!.Released);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal(
                $"/api/simulation-sessions/{simulationSessionId}/heartbeat",
                request.Path),
            request => Assert.Equal(
                $"/api/simulation-sessions/{simulationSessionId}/release",
                request.Path));
    }

    [Fact]
    public async Task SimulationWorkerHeartbeatSendsCompleteTopologyIdentity()
    {
        var requestBody = CreateHeartbeatRequest();
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            workerId = "local-simulation-worker-1",
            runtimeId = requestBody.RuntimeId,
            fleetId = requestBody.FleetId,
            nodeId = requestBody.NodeId,
            shardId = requestBody.ShardId,
            worldId = "local-world-1",
            host = requestBody.Host,
            udpPort = requestBody.UdpPort,
            maxConnections = requestBody.MaxConnections,
            activeConnections = requestBody.ActiveConnections,
            protocolVersion = requestBody.ProtocolVersion,
            simulationRevision = requestBody.SimulationRevision,
            collisionRevision = requestBody.CollisionRevision,
            lastHeartbeatAt = DateTime.UtcNow,
            onlineUntil = DateTime.UtcNow.AddSeconds(30)
        }));
        var client = CreateClient(handler);

        var result = await client.HeartbeatSimulationWorkerAsync(
            "local-simulation-worker-1",
            requestBody,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "/api/simulation-workers/local-simulation-worker-1/heartbeat",
            request.Path);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("local-fleet", body.RootElement.GetProperty("fleetId").GetString());
        Assert.Equal("local-node-1", body.RootElement.GetProperty("nodeId").GetString());
        Assert.Equal("local-shard-1", body.RootElement.GetProperty("shardId").GetString());
        Assert.Equal("runtime-1", body.RootElement.GetProperty("runtimeId").GetString());
        Assert.Equal(
            RealtimeProtocol.Version,
            body.RootElement.GetProperty("protocolVersion").GetInt32());
    }

    [Fact]
    public async Task SimulationWorkerOfflineUsesRuntimeGuardedRoute()
    {
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            workerId = "local-simulation-worker-1",
            runtimeId = "runtime-1",
            offlineAt = DateTime.UtcNow
        }));
        var client = CreateClient(handler);

        var result = await client.MarkSimulationWorkerOfflineAsync(
            "local-simulation-worker-1",
            "runtime-1",
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "/api/simulation-workers/local-simulation-worker-1/offline",
            request.Path);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("runtime-1", body.RootElement.GetProperty("runtimeId").GetString());
    }

    [Fact]
    public async Task NetworkFailureReturnsServiceUnavailable()
    {
        var handler = new RecordingHttpMessageHandler(
            _ => throw new HttpRequestException("offline"));
        var result = await ConsumeAsync(CreateClient(handler));

        Assert.False(result.Succeeded);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("auth_service_unavailable", result.Error!.Code);
    }

    [Fact]
    public async Task TimeoutReturnsGatewayTimeout()
    {
        var handler = new RecordingHttpMessageHandler(
            _ => throw new TaskCanceledException("timeout"));
        var result = await ConsumeAsync(CreateClient(handler));

        Assert.False(result.Succeeded);
        Assert.Equal(504, result.StatusCode);
        Assert.Equal("auth_service_timeout", result.Error!.Code);
    }

    [Fact]
    public async Task InvalidSuccessPayloadReturnsBadGateway()
    {
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            accountId = Guid.Empty,
            characterId = Guid.Empty
        }));
        var result = await ConsumeAsync(CreateClient(handler));

        Assert.False(result.Succeeded);
        Assert.Equal(502, result.StatusCode);
        Assert.Equal("invalid_auth_response", result.Error!.Code);
    }

    [Fact]
    public async Task CarryStateAboveTheHardCapReturnsBadGateway()
    {
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            accountId = Guid.NewGuid(),
            characterId = Guid.NewGuid(),
            characterName = "Hero One",
            shardId = "local-shard-1",
            worldId = "local-world-1",
            workerId = "local-simulation-worker-1",
            workerRuntimeId = "runtime-1",
            simulationSessionId = Guid.NewGuid(),
            simulationSessionToken = "simulation-session-token",
            sessionExpiresAt = DateTime.UtcNow.AddMinutes(1),
            itemStateRevision = 1,
            carriedWeight = 281,
            carryCapacity = 200,
            isReconnect = false
        }));

        var result = await ConsumeAsync(CreateClient(handler));

        Assert.False(result.Succeeded);
        Assert.Equal(502, result.StatusCode);
        Assert.Equal("invalid_auth_response", result.Error!.Code);
    }

    [Fact]
    public async Task ProblemDetailsErrorIsMappedWithoutLosingItsCode()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(
            HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new
            {
                type = "about:blank",
                title = "Conflict",
                status = 409,
                detail = "The ticket belongs to another worker runtime.",
                code = "wrong_simulation_worker"
            })
        });
        var result = await ConsumeAsync(CreateClient(handler));

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("wrong_simulation_worker", result.Error!.Code);
        Assert.Equal(
            "The ticket belongs to another worker runtime.",
            result.Error.Message);
    }

    private static Task<AuthServiceResult<ConsumedSimulationJoinTicketResponse>> ConsumeAsync(
        AuthServiceClient client)
    {
        return client.ConsumeJoinTicketAsync(
            "join-ticket",
            "local-simulation-worker-1",
            "runtime-1",
            "local-shard-1",
            CancellationToken.None);
    }

    private static SimulationWorkerHeartbeatRequest CreateHeartbeatRequest()
    {
        return new SimulationWorkerHeartbeatRequest(
            "local-fleet",
            "local-node-1",
            "local-shard-1",
            "runtime-1",
            DateTime.UtcNow.AddMinutes(-1),
            "worker.example.test",
            28015,
            100,
            4,
            RealtimeProtocol.Version,
            GameSimulationCompatibility.Revision,
            "collision-revision-1");
    }

    private static AuthServiceClient CreateClient(HttpMessageHandler handler)
    {
        return new AuthServiceClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://auth-service.test")
        });
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value)
        };
    }

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new RecordedRequest(request.RequestUri!.AbsolutePath, body));
            return responseFactory(request);
        }
    }

    private sealed record RecordedRequest(string Path, string Body);
}
