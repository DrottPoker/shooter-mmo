using System.Net;
using ShooterMmo.GameSimulation;
using SimulationWorker.Auth;
using SimulationWorker.Config;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class AuthServiceAuthenticationHandlerTests
{
    [Fact]
    public async Task AddsSimulationWorkerIdentityToAuthServiceRequests()
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
        Assert.Equal("local-simulation-worker-1", recorder.SimulationWorkerId);
        Assert.Equal("test-service-secret", recorder.SimulationWorkerSecret);
    }

    private static SimulationWorkerConfig CreateConfig()
    {
        return new SimulationWorkerConfig(
            "local-simulation-worker-1",
            "local-fleet",
            "local-node-1",
            "local-shard-1",
            "development-world-1",
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
        public string? SimulationWorkerId { get; private set; }

        public string? SimulationWorkerSecret { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SimulationWorkerId = request.Headers.GetValues(
                AuthServiceAuthenticationHandler.SimulationWorkerIdHeader).Single();
            SimulationWorkerSecret = request.Headers.GetValues(
                AuthServiceAuthenticationHandler.SimulationWorkerSecretHeader).Single();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
