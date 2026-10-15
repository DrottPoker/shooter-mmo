using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.Backend.Tests.Unit;

public sealed class SimulationWorkerHeartbeatServiceTests
{
    [Fact]
    public async Task RegistersAfterUdpReadinessAndMarksTheExactRuntimeOfflineOnStop()
    {
        var handler = new RegistryHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var config = CreateConfig();
        var readiness = new RealtimeTransportReadiness();
        var identity = new SimulationWorkerIdentity(
            "test-worker-runtime",
            DateTime.UtcNow.AddMinutes(-1));
        var collisionWorld = CollisionTestWorldFactory.Create();
        var registrationLease = new SimulationWorkerRegistrationLease(TimeProvider.System);
        using var service = new SimulationWorkerHeartbeatService(
            new GameServiceClient(httpClient),
            config,
            identity,
            new ActiveSimulationSessionStore(),
            readiness,
            collisionWorld,
            NullLogger<SimulationWorkerHeartbeatService>.Instance,
            registrationLease);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await service.StartAsync(timeout.Token);
        await Task.Delay(50, timeout.Token);
        Assert.Empty(handler.Paths);

        readiness.MarkListening(config.UdpPort);
        await handler.HeartbeatReceived.Task.WaitAsync(timeout.Token);
        Assert.True(SpinWait.SpinUntil(() => registrationLease.IsValid, TimeSpan.FromSeconds(1)));
        Assert.Equal(
            "/api/simulation-workers/local-simulation-worker-1/heartbeat",
            Assert.Single(handler.Paths));

        await service.StopAsync(timeout.Token);
        await handler.OfflineReceived.Task.WaitAsync(timeout.Token);
        Assert.False(registrationLease.HasLease);

        Assert.Equal(
            new[]
            {
                "/api/simulation-workers/local-simulation-worker-1/heartbeat",
                "/api/simulation-workers/local-simulation-worker-1/offline"
            },
            handler.Paths);
        Assert.Equal("127.0.0.1", handler.HeartbeatRequest!.Host);
        Assert.Equal(27015, handler.HeartbeatRequest.UdpPort);
        Assert.Equal("local-fleet", handler.HeartbeatRequest.FleetId);
        Assert.Equal("local-node-1", handler.HeartbeatRequest.NodeId);
        Assert.Equal("local-shard-1", handler.HeartbeatRequest.ShardId);
        Assert.Equal("development-world-1", handler.HeartbeatRequest.WorldId);
        Assert.Equal("test-worker-runtime", handler.HeartbeatRequest.RuntimeId);
        Assert.Equal(100, handler.HeartbeatRequest.MaxConnections);
        Assert.Equal(0, handler.HeartbeatRequest.ActiveConnections);
        Assert.Equal("test-worker-runtime", handler.OfflineRequest!.RuntimeId);
    }

    [Theory]
    [InlineData("simulation_world_not_found")]
    [InlineData("shard_world_rebind_blocked")]
    public async Task DefinitiveAuthorityFailureStopsTheWorkerImmediately(string errorCode)
    {
        var handler = new AuthorityFailureRegistryHandler(errorCode);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var config = CreateConfig();
        var readiness = new RealtimeTransportReadiness();
        var registrationLease = new SimulationWorkerRegistrationLease(TimeProvider.System);
        var applicationLifetime = new TestHostApplicationLifetime();
        using var service = new SimulationWorkerHeartbeatService(
            new GameServiceClient(httpClient),
            config,
            new SimulationWorkerIdentity(
                "stale-worker-runtime",
                DateTime.UtcNow.AddMinutes(-2)),
            new ActiveSimulationSessionStore(),
            readiness,
            CollisionTestWorldFactory.Create(),
            NullLogger<SimulationWorkerHeartbeatService>.Instance,
            registrationLease,
            applicationLifetime);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await service.StartAsync(timeout.Token);
        readiness.MarkListening(config.UdpPort);
        await handler.HeartbeatReceived.Task.WaitAsync(timeout.Token);
        await applicationLifetime.StopRequested.Task.WaitAsync(timeout.Token);

        Assert.False(registrationLease.HasLease);
        Assert.True(applicationLifetime.ApplicationStopping.IsCancellationRequested);

        await service.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task ReplacementRuntimeWaitsForThePreviousLeaseAndThenRegisters()
    {
        // The previous runtime's lease is still live for the first three heartbeats.
        var handler = new ScriptedRegistryHandler(attempt =>
            attempt <= 3 ? "worker_runtime_changed" : null);
        var harness = CreateHarness(handler, TimeSpan.FromSeconds(30));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await harness.Service.StartAsync(timeout.Token);
        harness.Readiness.MarkListening(harness.Config.UdpPort);
        await handler.Succeeded.Task.WaitAsync(timeout.Token);

        Assert.True(SpinWait.SpinUntil(() => harness.Lease.IsValid, TimeSpan.FromSeconds(1)));
        Assert.False(harness.Lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.True(handler.Attempts >= 4);

        await harness.Service.StopAsync(timeout.Token);
        harness.Service.Dispose();
    }

    [Fact]
    public async Task RuntimeThatOwnedTheWorkerStopsWhenAnotherRuntimeTakesOver()
    {
        var handler = new ScriptedRegistryHandler(attempt =>
            attempt == 1 ? null : "worker_runtime_changed");
        var harness = CreateHarness(handler, TimeSpan.FromSeconds(30));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await harness.Service.StartAsync(timeout.Token);
        harness.Readiness.MarkListening(harness.Config.UdpPort);
        await harness.Lifetime.StopRequested.Task.WaitAsync(timeout.Token);

        Assert.Equal(2, handler.Attempts);
        Assert.False(harness.Lease.HasLease);

        await harness.Service.StopAsync(timeout.Token);
        harness.Service.Dispose();
    }

    [Fact]
    public async Task ReplacementRuntimeStopsWhenAnotherRuntimeStaysLiveBeyondTheAcquireTimeout()
    {
        var handler = new ScriptedRegistryHandler(_ => "worker_runtime_changed");
        var harness = CreateHarness(handler, TimeSpan.FromMilliseconds(200));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await harness.Service.StartAsync(timeout.Token);
        harness.Readiness.MarkListening(harness.Config.UdpPort);
        await harness.Lifetime.StopRequested.Task.WaitAsync(timeout.Token);

        Assert.True(handler.Attempts >= 2);
        Assert.False(harness.Lease.HasLease);

        await harness.Service.StopAsync(timeout.Token);
        harness.Service.Dispose();
    }

    private static HeartbeatHarness CreateHarness(
        HttpMessageHandler handler,
        TimeSpan acquireTimeout)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var config = CreateConfig() with
        {
            RegistryHeartbeatInterval = TimeSpan.FromMilliseconds(20),
            RegistryAcquireTimeout = acquireTimeout
        };
        var readiness = new RealtimeTransportReadiness();
        var lease = new SimulationWorkerRegistrationLease(TimeProvider.System);
        var lifetime = new TestHostApplicationLifetime();
        var service = new SimulationWorkerHeartbeatService(
            new GameServiceClient(httpClient),
            config,
            new SimulationWorkerIdentity(
                "replacement-worker-runtime",
                DateTime.UtcNow.AddMinutes(-5)),
            new ActiveSimulationSessionStore(),
            readiness,
            CollisionTestWorldFactory.Create(),
            NullLogger<SimulationWorkerHeartbeatService>.Instance,
            lease,
            lifetime);
        return new HeartbeatHarness(service, config, readiness, lease, lifetime);
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
            new Uri("http://game-service.test"),
            TimeSpan.FromSeconds(2),
            "test-simulation-worker-secret-at-least-32-characters",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromHours(1),
            "127.0.0.1:6379",
            TimeSpan.FromSeconds(1));
    }

    private sealed class RegistryHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        public SimulationWorkerHeartbeatRequest? HeartbeatRequest { get; private set; }

        public SimulationWorkerOfflineRequest? OfflineRequest { get; private set; }

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
                HeartbeatRequest = await request.Content!.ReadFromJsonAsync<SimulationWorkerHeartbeatRequest>(
                    cancellationToken);
                var now = DateTime.UtcNow;
                HeartbeatReceived.TrySetResult();
                return JsonResponse(new SimulationWorkerHeartbeatResponse(
                    "local-simulation-worker-1",
                    HeartbeatRequest!.RuntimeId,
                    HeartbeatRequest.FleetId,
                    HeartbeatRequest.NodeId,
                    HeartbeatRequest.ShardId,
                    "development-world-1",
                    HeartbeatRequest!.Host,
                    HeartbeatRequest.UdpPort,
                    HeartbeatRequest.MaxConnections,
                    HeartbeatRequest.ActiveConnections,
                    HeartbeatRequest.ProtocolVersion,
                    HeartbeatRequest.SimulationRevision,
                    HeartbeatRequest.CollisionRevision,
                    now,
                    now.AddSeconds(30)));
            }

            OfflineRequest = await request.Content!.ReadFromJsonAsync<SimulationWorkerOfflineRequest>(
                cancellationToken);
            OfflineReceived.TrySetResult();
            return JsonResponse(new SimulationWorkerOfflineResponse(
                "local-simulation-worker-1",
                OfflineRequest!.RuntimeId,
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

    private sealed class AuthorityFailureRegistryHandler(string errorCode) : HttpMessageHandler
    {
        public TaskCompletionSource HeartbeatReceived { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                HeartbeatReceived.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = JsonContent.Create(new
                    {
                        code = errorCode,
                        detail = "The worker cannot own the requested shard and World binding."
                    })
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new
                {
                    code = errorCode,
                    detail = "The worker cannot own the requested shard and World binding."
                })
            });
        }
    }

    private sealed record HeartbeatHarness(
        SimulationWorkerHeartbeatService Service,
        SimulationWorkerConfig Config,
        RealtimeTransportReadiness Readiness,
        SimulationWorkerRegistrationLease Lease,
        TestHostApplicationLifetime Lifetime);

    // Answers each heartbeat with the scripted error code, or success when it is null.
    private sealed class ScriptedRegistryHandler(Func<int, string?> errorForAttempt)
        : HttpMessageHandler
    {
        private int attempts;

        public int Attempts => Volatile.Read(ref attempts);

        public TaskCompletionSource Succeeded { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = JsonContent.Create(new
                    {
                        code = "worker_runtime_changed",
                        detail = "A different runtime currently owns this simulation worker identity."
                    })
                };
            }

            var attempt = Interlocked.Increment(ref attempts);
            var errorCode = errorForAttempt(attempt);
            if (errorCode is not null)
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = JsonContent.Create(new
                    {
                        code = errorCode,
                        detail = "Another live runtime currently owns this simulation worker identity."
                    })
                };
            }

            var heartbeat = await request.Content!.ReadFromJsonAsync<SimulationWorkerHeartbeatRequest>(
                cancellationToken);
            var now = DateTime.UtcNow;
            Succeeded.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SimulationWorkerHeartbeatResponse(
                    "local-simulation-worker-1",
                    heartbeat!.RuntimeId,
                    heartbeat.FleetId,
                    heartbeat.NodeId,
                    heartbeat.ShardId,
                    "development-world-1",
                    heartbeat.Host,
                    heartbeat.UdpPort,
                    heartbeat.MaxConnections,
                    heartbeat.ActiveConnections,
                    heartbeat.ProtocolVersion,
                    heartbeat.SimulationRevision,
                    heartbeat.CollisionRevision,
                    now,
                    now.AddSeconds(30)))
            };
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public TaskCompletionSource StopRequested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ApplicationStarted => started.Token;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => stopped.Token;

        public void StopApplication()
        {
            stopping.Cancel();
            StopRequested.TrySetResult();
        }
    }
}
