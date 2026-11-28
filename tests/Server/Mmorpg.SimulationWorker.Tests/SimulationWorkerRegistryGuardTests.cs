using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.GameSimulation;
using Mmorpg.Server.Common.Contracts;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.Tests.Realtime;
using Mmorpg.Testing.Content;

namespace Mmorpg.SimulationWorker.Tests;

// What the worker checks about GameService at its registration: that the leases GameService grants
// can work with its own timing settings, from the first answer, and that both run the same version of
// the call between them. Neither is a credential failure, and neither lets the worker play.
public sealed class SimulationWorkerRegistryGuardTests
{
    private const string WorkerId = "local-simulation-worker-1";

    // The first answer shows what GameService grants. A registry lease that is shorter than a heartbeat
    // interval and its call, or a session lease shorter than an outage that the worker tolerates and
    // the round that renews it, can never work, so the worker stops before it plays anyone, with one
    // message that names the settings of both sides.
    [Theory]
    [InlineData(2, 150, "registry lease of 2 s (Simulation:WorkerHeartbeatTimeoutSeconds)", "SimulationWorker:RegistryHeartbeatSeconds")]
    [InlineData(30, 60, "session lease of 60 s (Simulation:SessionLeaseLifetimeSeconds)", "SimulationWorker:GameServiceOutageToleranceSeconds")]
    public async Task AnIncompatibleLeaseInTheFirstAnswerStopsTheWorkerWithOneMessageThatNamesBothSides(
        int registrySeconds,
        int sessionSeconds,
        string granted,
        string workerSetting)
    {
        var harness = new Harness(new LeaseAnswer(registrySeconds, sessionSeconds));

        await harness.RunUntilStopAsync();

        var message = Assert.Single(harness.Logger.Entries, entry => entry.Level == LogLevel.Critical).Message;
        Assert.Contains(granted, message, StringComparison.Ordinal);
        Assert.Contains(workerSetting, message, StringComparison.Ordinal);
        Assert.False(harness.Lease.IsValid, "A worker that cannot work with these leases must not accept joins.");
        Assert.True(harness.Lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task BothLeasesThatFailAreNamedInOneMessage()
    {
        var harness = new Harness(new LeaseAnswer(2, 60));

        await harness.RunUntilStopAsync();

        var message = Assert.Single(harness.Logger.Entries, entry => entry.Level == LogLevel.Critical).Message;
        Assert.Contains("registry lease of 2 s", message, StringComparison.Ordinal);
        Assert.Contains("session lease of 60 s", message, StringComparison.Ordinal);
        Assert.Contains("Simulation:WorkerHeartbeatTimeoutSeconds", message, StringComparison.Ordinal);
        Assert.Contains("Simulation:SessionLeaseLifetimeSeconds", message, StringComparison.Ordinal);
    }

    // The worker is already registered at GameService when it stops, so it goes offline and frees the
    // Shard at once, as any stop does.
    [Fact]
    public async Task AWorkerThatStopsForItsLeasesGoesOfflineAtGameService()
    {
        var harness = new Harness(new LeaseAnswer(2, 150));

        await harness.RunUntilStopAsync();
        await harness.Service.StopAsync(CancellationToken.None);

        Assert.Contains(harness.Handler.Paths, path => path.EndsWith("/offline", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeasesThatFitAreAcceptedAndCheckedOnlyAtTheFirstAnswer()
    {
        // The first answer fits. Later answers grant less, as a GameService that restarted with other
        // settings would; a running worker is not stopped for it, and its joins check the session lease.
        var answer = new LeaseAnswer(30, 150);
        var harness = new Harness(answer);
        await harness.StartAsync();

        await harness.WaitForHeartbeatsAsync(1);
        Harness.WaitUntil(() => harness.Lease.IsValid, "The first answer did not give the worker its lease.");
        answer.RegistrySeconds = 14;
        answer.SessionSeconds = 20;
        await harness.WaitForHeartbeatsAsync(4);

        Assert.False(harness.Lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level == LogLevel.Critical);
        await harness.Service.StopAsync(CancellationToken.None);
    }

    // The call between the two has a version. GameService refuses another one with a code of its own,
    // which is no credential failure: the worker logs one error line that says which side is older,
    // plays nobody, and tries again at its normal pace until the older side is updated.
    [Theory]
    [InlineData(ServiceContract.Version + 1, "This worker is the older side")]
    [InlineData(ServiceContract.Version - 1, "GameService is the older side")]
    public async Task AContractMismatchIsAnErrorThatNamesTheOlderSideAndEndsNothing(
        int gameServiceVersion,
        string expected)
    {
        var handler = new MismatchAnswer($"The worker runs service contract version {ServiceContract.Version} and GameService runs version {gameServiceVersion}. Update the older side so both run the same version.");
        var harness = new Harness(handler);
        await harness.StartAsync();

        await harness.WaitForHeartbeatsAsync(3);

        // The service logs an answer a moment after the handler counted the request.
        Harness.WaitUntil(
            () => harness.Logger.Entries.Count(entry => entry.Level == LogLevel.Error) >= 3,
            "The worker must try again at its normal pace.");
        var errors = harness.Logger.Entries.Where(entry => entry.Level == LogLevel.Error).ToArray();
        Assert.All(errors, entry =>
        {
            Assert.Contains(ServiceContract.MismatchCode, entry.Message, StringComparison.Ordinal);
            Assert.Contains(expected, entry.Message, StringComparison.Ordinal);
            Assert.Contains($"version {gameServiceVersion}", entry.Message, StringComparison.Ordinal);
        });
        Assert.False(harness.Lifetime.ApplicationStopping.IsCancellationRequested, "A mismatch is no lost authority.");
        Assert.False(harness.Lease.HasLease, "Nobody may join a worker that GameService refuses.");
        Assert.True(harness.Availability.IsAvailable, "GameService answers, so durable work is not paused as in an outage.");
        await harness.Service.StopAsync(CancellationToken.None);
    }

    // The worker sends the version with every heartbeat.
    [Fact]
    public async Task EveryHeartbeatCarriesTheServiceContractVersion()
    {
        var answer = new LeaseAnswer(30, 150);
        var harness = new Harness(answer);
        await harness.StartAsync();

        await harness.WaitForHeartbeatsAsync(2);

        // The service keeps sending while the test looks, so the test looks at a copy.
        SimulationWorkerHeartbeatRequest[] requests;
        lock (answer.Requests)
        {
            requests = [.. answer.Requests];
        }

        Assert.True(requests.Length >= 2);
        Assert.All(requests, request => Assert.Equal(ServiceContract.Version, request.ServiceContractVersion));
        await harness.Service.StopAsync(CancellationToken.None);
    }

    private sealed class Harness
    {
        private readonly HttpClient httpClient;
        private readonly RealtimeTransportReadiness readiness = new();

        public Harness(ScriptedHandler handler)
        {
            Handler = handler;
            httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://game-service.test") };
            Availability = new GameServiceAvailability(TimeProvider.System, new CapturingLogger<GameServiceAvailability>());
            Config = new SimulationWorkerConfig(
                WorkerId,
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
                new MovementSimulationSettings(30, 5f, 720f, -24f, -2f, 0f, -14f, 14f, -14f, 14f),
                new MovementSpawnConfig(0f, 0f, -1f, 0f),
                new Uri("http://game-service.test"),
                TimeSpan.FromSeconds(2),
                "test-simulation-worker-secret-at-least-32-characters",
                TimeSpan.FromSeconds(10),
                // A heartbeat every 20 ms, with leases that the timing check measures against the
                // default call timeout and tolerance.
                TimeSpan.FromMilliseconds(20),
                "127.0.0.1:6379",
                TimeSpan.FromSeconds(1))
            {
                RegistryHeartbeatRetryDelay = TimeSpan.FromMilliseconds(20)
            };
            Service = new SimulationWorkerHeartbeatService(
                new GameServiceClient(httpClient, Availability),
                Config,
                new SimulationWorkerIdentity("guard-runtime", DateTime.UtcNow.AddMinutes(-1)),
                new ActiveSimulationSessionStore(),
                readiness,
                CollisionTestWorldFactory.Create(),
                ItemCatalogTestData.CompileFixture(),
                QuestCatalogTestData.CompileFixture(),
                CombatRulesLoader.Load(Path.Combine(AppContext.BaseDirectory, SimulationWorkerConfig.DefaultCombatRulesPath)),
                new SimulationShardRules(),
                Logger,
                Lease,
                Lifetime,
                Availability);
        }

        public ScriptedHandler Handler { get; }

        public SimulationWorkerConfig Config { get; }

        public GameServiceAvailability Availability { get; }

        public SimulationWorkerHeartbeatService Service { get; }

        public SimulationWorkerRegistrationLease Lease { get; } = new(TimeProvider.System);

        public CapturingLogger<SimulationWorkerHeartbeatService> Logger { get; } = new();

        public StoppableLifetime Lifetime { get; } = new();

        public async Task StartAsync()
        {
            await Service.StartAsync(CancellationToken.None);
            readiness.MarkListening(Config.UdpPort);
        }

        public async Task RunUntilStopAsync()
        {
            await StartAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Lifetime.StopRequested.Task.WaitAsync(timeout.Token);
        }

        public static void WaitUntil(Func<bool> condition, string because)
        {
            Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)), because);
        }

        public async Task WaitForHeartbeatsAsync(int count)
        {
            Assert.True(
                SpinWait.SpinUntil(() => Handler.Heartbeats >= count, TimeSpan.FromSeconds(10)),
                $"Only {Handler.Heartbeats} of {count} heartbeats arrived.");
            // The answer of the last one is read by the service a moment later.
            await Task.Delay(50);
        }
    }

    private abstract class ScriptedHandler : HttpMessageHandler
    {
        private int heartbeats;

        public List<string> Paths { get; } = [];

        public List<SimulationWorkerHeartbeatRequest> Requests { get; } = [];

        public int Heartbeats => Volatile.Read(ref heartbeats);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths)
            {
                Paths.Add(path);
            }

            if (!path.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SimulationWorkerOfflineResponse(WorkerId, "guard-runtime", DateTime.UtcNow))
                };
            }

            var heartbeat = (await request.Content!.ReadFromJsonAsync<SimulationWorkerHeartbeatRequest>(cancellationToken))!;
            lock (Requests)
            {
                Requests.Add(heartbeat);
            }

            var response = Answer(heartbeat);
            Interlocked.Increment(ref heartbeats);
            return response;
        }

        protected abstract HttpResponseMessage Answer(SimulationWorkerHeartbeatRequest heartbeat);
    }

    // Grants the leases that the test sets, in the registry answer.
    private sealed class LeaseAnswer(int registrySeconds, int sessionSeconds) : ScriptedHandler
    {
        public int RegistrySeconds { get; set; } = registrySeconds;

        public int SessionSeconds { get; set; } = sessionSeconds;

        protected override HttpResponseMessage Answer(SimulationWorkerHeartbeatRequest heartbeat)
        {
            var now = DateTime.UtcNow;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SimulationWorkerHeartbeatResponse(
                    WorkerId,
                    heartbeat.RuntimeId,
                    heartbeat.FleetId,
                    heartbeat.NodeId,
                    heartbeat.ShardId,
                    heartbeat.WorldId,
                    "mvp-open-risk",
                    heartbeat.Host,
                    heartbeat.UdpPort,
                    heartbeat.MaxConnections,
                    heartbeat.ActiveConnections,
                    heartbeat.ProtocolVersion,
                    heartbeat.SimulationRevision,
                    heartbeat.CollisionRevision,
                    heartbeat.ItemCatalogRevision,
                    heartbeat.QuestCatalogRevision,
                    now,
                    now.AddSeconds(RegistrySeconds),
                    SessionSeconds))
            };
        }
    }

    // Refuses every heartbeat as GameService refuses another version of the call.
    private sealed class MismatchAnswer(string message) : ScriptedHandler
    {
        protected override HttpResponseMessage Answer(SimulationWorkerHeartbeatRequest heartbeat)
        {
            return new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new { code = ServiceContract.MismatchCode, detail = message })
            };
        }
    }

    private sealed class StoppableLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();

        public TaskCompletionSource StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            StopRequested.TrySetResult();
            stopping.Cancel();
        }
    }
}
