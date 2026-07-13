using System.Net;
using ShooterMmo.GameSimulation;
using WorldServer.Auth;
using WorldServer.Config;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class AuthServiceAuthenticationHandlerTests
{
    [Fact]
    public async Task AddsWorldServerIdentityToAuthServiceRequests()
    {
        var recorder = new HeaderRecordingHandler();
        var authenticationHandler = new AuthServiceAuthenticationHandler(CreateConfig())
        {
            InnerHandler = recorder
        };
        using var client = new HttpClient(authenticationHandler)
        {
            BaseAddress = new Uri("http://auth-service.test")
        };

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("local-world-1", recorder.WorldServerId);
        Assert.Equal("test-service-secret", recorder.WorldServerSecret);
    }

    private static WorldServerConfig CreateConfig()
    {
        return new WorldServerConfig(
            "local-world-1",
            "CollisionData",
            27015,
            "127.0.0.1",
            27015,
            100,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(15),
            15,
            TimeSpan.FromMilliseconds(500),
            new MovementSimulationSettings(
                30,
                5f,
                8f,
                720f,
                -24f,
                7f,
                -2f,
                0f,
                -14f,
                14f,
                -14f,
                14f),
            new MovementSpawnConfig(0f, 0f, -1f, 0f),
            new Uri("http://localhost:5000"),
            TimeSpan.FromSeconds(5),
            "test-service-secret",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10),
            "localhost:6379",
            TimeSpan.FromSeconds(1));
    }

    private sealed class HeaderRecordingHandler : HttpMessageHandler
    {
        public string? WorldServerId { get; private set; }

        public string? WorldServerSecret { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            WorldServerId = request.Headers.GetValues(
                AuthServiceAuthenticationHandler.WorldServerIdHeader).Single();
            WorldServerSecret = request.Headers.GetValues(
                AuthServiceAuthenticationHandler.WorldServerSecretHeader).Single();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
