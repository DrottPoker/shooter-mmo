using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WorldServer.Auth;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class AuthServiceClientTests
{
    [Fact]
    public async Task ConsumeJoinTicketSendsTheExpectedWorldId()
    {
        var worldSessionId = Guid.NewGuid();
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            accountId = Guid.NewGuid(),
            characterId = Guid.NewGuid(),
            characterName = "Hero One",
            worldId = "local-world-1",
            worldSessionId,
            worldSessionToken = "world-session-token",
            sessionExpiresAt = DateTime.UtcNow.AddMinutes(1),
            isReconnect = false
        }));
        var client = CreateClient(handler);

        var result = await client.ConsumeJoinTicketAsync(
            "join-ticket",
            "local-world-1",
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(worldSessionId, result.Value!.WorldSessionId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/world-join-tickets/consume", request.Path);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("join-ticket", body.RootElement.GetProperty("ticket").GetString());
        Assert.Equal("local-world-1", body.RootElement.GetProperty("worldId").GetString());
    }

    [Fact]
    public async Task WorldSessionLifecycleUsesTheSessionSpecificRoutes()
    {
        var worldSessionId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var handler = new RecordingHttpMessageHandler(request => CreateJsonResponse(new
        {
            worldSessionId,
            characterId,
            worldId = "local-world-1",
            expiresAt = DateTime.UtcNow.AddMinutes(1),
            released = request.RequestUri!.AbsolutePath.EndsWith("/release", StringComparison.Ordinal)
        }));
        var client = CreateClient(handler);

        var heartbeat = await client.HeartbeatWorldSessionAsync(
            worldSessionId,
            "local-world-1",
            "world-session-token",
            CancellationToken.None);
        var release = await client.ReleaseWorldSessionAsync(
            worldSessionId,
            "local-world-1",
            "world-session-token",
            CancellationToken.None);

        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.False(heartbeat.Value!.Released);
        Assert.True(release.Succeeded, release.Error?.Message);
        Assert.True(release.Value!.Released);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal($"/api/world-sessions/{worldSessionId}/heartbeat", request.Path),
            request => Assert.Equal($"/api/world-sessions/{worldSessionId}/release", request.Path));
    }

    [Fact]
    public async Task WorldHeartbeatUsesTheAuthenticatedWorldRoute()
    {
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            worldId = "local-world-1",
            lastHeartbeatAt = DateTime.UtcNow,
            onlineUntil = DateTime.UtcNow.AddSeconds(30)
        }));
        var client = CreateClient(handler);

        var result = await client.HeartbeatWorldAsync("local-world-1", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal("/api/worlds/local-world-1/heartbeat", Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public async Task NetworkFailureReturnsServiceUnavailable()
    {
        var handler = new RecordingHttpMessageHandler(_ => throw new HttpRequestException("offline"));
        var client = CreateClient(handler);

        var result = await client.ConsumeJoinTicketAsync(
            "join-ticket",
            "local-world-1",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("auth_service_unavailable", result.Error!.Code);
    }

    [Fact]
    public async Task TimeoutReturnsGatewayTimeout()
    {
        var handler = new RecordingHttpMessageHandler(_ => throw new TaskCanceledException("timeout"));
        var client = CreateClient(handler);

        var result = await client.ConsumeJoinTicketAsync(
            "join-ticket",
            "local-world-1",
            CancellationToken.None);

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
        var client = CreateClient(handler);

        var result = await client.ConsumeJoinTicketAsync(
            "join-ticket",
            "local-world-1",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(502, result.StatusCode);
        Assert.Equal("invalid_auth_response", result.Error!.Code);
    }

    [Fact]
    public async Task ProblemDetailsErrorIsMappedWithoutLosingItsCode()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new
            {
                type = "about:blank",
                title = "Conflict",
                status = 409,
                detail = "The ticket belongs to another world.",
                code = "wrong_world"
            })
        });
        var client = CreateClient(handler);

        var result = await client.ConsumeJoinTicketAsync(
            "join-ticket",
            "local-world-1",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("wrong_world", result.Error!.Code);
        Assert.Equal("The ticket belongs to another world.", result.Error.Message);
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
