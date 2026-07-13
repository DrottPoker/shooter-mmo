using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ShooterMmo.GameSimulation;
using WorldServer.Auth;
using WorldServer.Config;
using WorldServer.Realtime;
using WorldServer.Worlds;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class WorldRegistryHeartbeatServiceTests
{
    [Fact]
    public async Task RegistersAfterUdpReadinessAndMarksTheExactInstanceOfflineOnStop()
    {
        var handler = new RegistryHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://auth-service.test")
        };
        var config = CreateConfig();
        var readiness = new RealtimeTransportReadiness();
        var identity = new WorldServerInstanceIdentity("test-world-instance");
        var collisionWorld = CollisionTestWorldFactory.Create();
        using var service = new WorldRegistryHeartbeatService(
            new AuthServiceClient(httpClient),
            config,
            identity,
            readiness,
            collisionWorld,
            NullLogger<WorldRegistryHeartbeatService>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await service.StartAsync(timeout.Token);
        await Task.Delay(50, timeout.Token);
        Assert.Empty(handler.Paths);

        readiness.MarkListening(config.UdpPort);
        await handler.HeartbeatReceived.Task.WaitAsync(timeout.Token);
        Assert.Equal("/api/worlds/local-world-1/heartbeat", Assert.Single(handler.Paths));

        await service.StopAsync(timeout.Token);
        await handler.OfflineReceived.Task.WaitAsync(timeout.Token);

        Assert.Equal(
            new[]
            {
                "/api/worlds/local-world-1/heartbeat",
                "/api/worlds/local-world-1/offline"
            },
            handler.Paths);
        Assert.Equal("127.0.0.1", handler.HeartbeatRequest!.Host);
        Assert.Equal(27015, handler.HeartbeatRequest.UdpPort);
        Assert.Equal("test-world-instance", handler.HeartbeatRequest.InstanceId);
        Assert.Equal("test-world-instance", handler.OfflineRequest!.InstanceId);
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
            new Uri("http://auth-service.test"),
            TimeSpan.FromSeconds(2),
            "test-world-server-secret-at-least-32-characters",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromHours(1),
            "127.0.0.1:6379",
            TimeSpan.FromSeconds(1));
    }

    private sealed class RegistryHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        public WorldHeartbeatRequest? HeartbeatRequest { get; private set; }

        public WorldOfflineRequest? OfflineRequest { get; private set; }

        public TaskCompletionSource HeartbeatReceived { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource OfflineReceived { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                HeartbeatRequest = await request.Content!.ReadFromJsonAsync<WorldHeartbeatRequest>(
                    cancellationToken);
                var now = DateTime.UtcNow;
                HeartbeatReceived.TrySetResult();
                return JsonResponse(new WorldHeartbeatResponse(
                    "local-world-1",
                    HeartbeatRequest!.Host,
                    HeartbeatRequest.UdpPort,
                    HeartbeatRequest.InstanceId,
                    HeartbeatRequest.ProtocolVersion,
                    HeartbeatRequest.SimulationRevision,
                    HeartbeatRequest.CollisionRevision,
                    now,
                    now.AddSeconds(30)));
            }

            OfflineRequest = await request.Content!.ReadFromJsonAsync<WorldOfflineRequest>(
                cancellationToken);
            OfflineReceived.TrySetResult();
            return JsonResponse(new WorldOfflineResponse(
                "local-world-1",
                OfflineRequest!.InstanceId,
                DateTime.UtcNow));
        }

        private static HttpResponseMessage JsonResponse<T>(T value)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(value)
            };
        }
    }
}
