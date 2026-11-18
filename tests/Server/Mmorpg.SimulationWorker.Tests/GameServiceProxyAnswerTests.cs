using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.Tests.Realtime;
using Mmorpg.Testing.Content;

namespace Mmorpg.SimulationWorker.Tests;

// GameService answers every failure with Problem Details that carry a code. A proxy in front of it
// answers its own error page, or nothing, while GameService restarts. That is an outage that ends no
// player's session, never an invalid answer: invalid_auth_response ends the session of whoever
// asked, so every player's next durable action would have ended that player's session, and the
// registry heartbeat would have taken its refused branch without marking GameService unavailable.
public sealed class GameServiceProxyAnswerTests
{
    private const string WorkerId = "local-simulation-worker-1";

    // The answers of an intermediary, by what its status and body say.
    public static TheoryData<string, HttpStatusCode, bool> OutageAnswers => new()
    {
        { "a 502 with an HTML error page", HttpStatusCode.BadGateway, true },
        { "an empty 503", HttpStatusCode.ServiceUnavailable, false },
        { "a 504 with an HTML error page", HttpStatusCode.GatewayTimeout, true },
        { "an empty 500", HttpStatusCode.InternalServerError, false }
    };

    // The call, as every group of GameService routes makes it.
    private static readonly string[] Operations =
    [
        "registry heartbeat",
        "item operation",
        "checkpoint",
        "session heartbeat",
        "join ticket",
        "corpse restore"
    ];

    [Theory]
    [MemberData(nameof(OutageAnswers))]
    public async Task AnErrorStatusWithoutAProblemCodeIsTheOutageOnEveryRoute(
        string description,
        HttpStatusCode status,
        bool html)
    {
        foreach (var operation in Operations)
        {
            var client = CreateClient(new FixedAnswer(() => Answer(status, html)));

            var outcome = await CallAsync(client, operation);

            Assert.False(outcome.Succeeded, $"{operation}, {description}");
            Assert.Equal("auth_service_unavailable", outcome.Code);
            Assert.Equal((int)status, outcome.StatusCode);
            Assert.Equal(GameServiceCallMetrics.NoProblemReason, outcome.Reason);
            Assert.True(GameServiceOutcome.IsTransient(outcome.Code!, outcome.StatusCode), operation);
            Assert.True(GameServiceOutcome.IsUncertain(outcome.Code!), operation);
            Assert.True(GameServiceOutcome.MayHaveCommitted(outcome.Code!, outcome.StatusCode), operation);
            Assert.False(GameServiceOutcome.EndsSession(outcome.Code!), operation);
            Assert.False(GameServiceOutcome.EndsItemSession(outcome.Code!), operation);
            Assert.Contains($"HTTP {(int)status}", outcome.Message, StringComparison.Ordinal);
        }
    }

    // A rejection of the call that ends nothing. Its status is never the proxy's own, because a
    // release reads 401, 404, and 409 as GameService's answers about a session, and a proxy's 404 page
    // is not one.
    [Theory]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Conflict, false)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    public async Task AClientErrorWithoutAProblemCodeIsARejectionThatEndsNoSessionAndKeepsNoStatusMeaning(
        HttpStatusCode status,
        bool html)
    {
        foreach (var operation in Operations)
        {
            var client = CreateClient(new FixedAnswer(() => Answer(status, html)));

            var outcome = await CallAsync(client, operation);

            Assert.False(outcome.Succeeded);
            Assert.Equal("auth_service_rejected", outcome.Code);
            Assert.Equal((int)HttpStatusCode.BadRequest, outcome.StatusCode);
            Assert.Equal(GameServiceCallMetrics.NoProblemReason, outcome.Reason);
            Assert.False(GameServiceOutcome.IsTransient(outcome.Code!, outcome.StatusCode), operation);
            Assert.False(GameServiceOutcome.EndsItemSession(outcome.Code!), operation);
            Assert.False(GameServiceOutcome.MayHaveCommitted(outcome.Code!, outcome.StatusCode), operation);
        }
    }

    // The status says that the credentials are wrong, which GameService itself says with
    // invalid_service_credentials, and that is the one rejection that ends the session.
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired)]
    public async Task AnAuthenticationStatusWithoutAProblemCodeIsAFailedAuthentication(HttpStatusCode status)
    {
        var client = CreateClient(new FixedAnswer(() => Answer(status, html: true)));

        var outcome = await CallAsync(client, "item operation");

        Assert.Equal("auth_service_authentication_failed", outcome.Code);
        Assert.Equal((int)HttpStatusCode.BadGateway, outcome.StatusCode);
        Assert.Equal(GameServiceCallMetrics.AuthenticationReason, outcome.Reason);
        Assert.True(GameServiceOutcome.EndsSession(outcome.Code!));
    }

    // invalid_auth_response stays for a success status whose body does not hold what the call needs.
    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[]")]
    public async Task OnlyASuccessStatusWhoseBodyFailsItsValidationIsAnInvalidResponse(string body)
    {
        var client = CreateClient(new FixedAnswer(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

        var outcome = await CallAsync(client, "checkpoint");

        Assert.Equal("invalid_auth_response", outcome.Code);
        Assert.Equal(GameServiceCallMetrics.InvalidResponseReason, outcome.Reason);
        Assert.True(GameServiceOutcome.EndsSession(outcome.Code!));
    }

    // GameService's own answers keep their code and status: a server error counts as failed, and a
    // refusal of the domain, the call working as designed, does not.
    [Fact]
    public async Task AProblemDetailsAnswerKeepsItsCodeAndOnlyAServerErrorCountsAsAFailedCall()
    {
        var serverError = await CallAsync(
            CreateClient(new FixedAnswer(() => Problem(HttpStatusCode.ServiceUnavailable, "database_unavailable"))),
            "item operation");
        var refusal = await CallAsync(
            CreateClient(new FixedAnswer(() => Problem(HttpStatusCode.Conflict, "item_slot_occupied"))),
            "item operation");

        Assert.Equal(("database_unavailable", 503, GameServiceCallMetrics.ServerErrorReason), (serverError.Code, serverError.StatusCode, serverError.Reason));
        Assert.Equal(("item_slot_occupied", 409, (string?)null), (refusal.Code, refusal.StatusCode, refusal.Reason));
    }

    // Every call that fails for a reason of the channel is counted by its route group and reason,
    // and the labels stay few: a group and a reason, never an id.
    [Fact]
    public async Task EveryFailedCallIsCountedByRouteGroupAndReason()
    {
        using var metrics = new GameServiceCallMetrics();
        var counted = new List<(string? Route, string? Reason, long Value)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == GameServiceCallMetrics.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "simulation_worker.game_service.call_failures")
            {
                return;
            }

            string? route = null;
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "route")
                {
                    route = tag.Value as string;
                }
                else if (tag.Key == "reason")
                {
                    reason = tag.Value as string;
                }
            }

            lock (counted)
            {
                counted.Add((route, reason, value));
            }
        });
        listener.Start();
        var proxy = new GameServiceClient(
            new HttpClient(new FixedAnswer(() => Answer(HttpStatusCode.BadGateway, html: true)))
            {
                BaseAddress = new Uri("http://game-service.test")
            },
            callMetrics: metrics);
        var timeout = new GameServiceClient(
            new HttpClient(new ThrowingHandler(new TaskCanceledException("timeout")))
            {
                BaseAddress = new Uri("http://game-service.test")
            },
            callMetrics: metrics);
        var down = new GameServiceClient(
            new HttpClient(new ThrowingHandler(new HttpRequestException("offline")))
            {
                BaseAddress = new Uri("http://game-service.test")
            },
            callMetrics: metrics);
        var refusing = new GameServiceClient(
            new HttpClient(new FixedAnswer(() => Problem(HttpStatusCode.Conflict, "item_slot_occupied")))
            {
                BaseAddress = new Uri("http://game-service.test")
            },
            callMetrics: metrics);

        await CallAsync(proxy, "registry heartbeat");
        await CallAsync(timeout, "checkpoint");
        await CallAsync(down, "item operation");
        await CallAsync(refusing, "item operation");
        listener.RecordObservableInstruments();

        lock (counted)
        {
            Assert.Equal(
                [
                    ("registry", GameServiceCallMetrics.NoProblemReason, 1L),
                    ("checkpoint", GameServiceCallMetrics.TimeoutReason, 1L),
                    ("item", GameServiceCallMetrics.UnreachableReason, 1L)
                ],
                counted);
        }
    }

    // Every operation name that the client gives a call belongs to a named route group, so a new
    // call cannot be counted under no group.
    [Fact]
    public void EveryOperationOfTheClientBelongsToARouteGroup()
    {
        var source = File.ReadAllText(Path.Combine(
            Mmorpg.Testing.RepositoryPaths.Root,
            "src",
            "Server",
            "SimulationWorker",
            "GameServiceApi",
            "GameServiceClient.cs"));
        var operations = System.Text.RegularExpressions.Regex
            .Matches(source, "\"(GameService\\.[A-Za-z]+)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToArray();

        Assert.True(operations.Length >= 19, "The scan no longer finds the operations of the client.");
        Assert.All(
            operations,
            operation => Assert.NotEqual(GameServiceCallMetrics.OtherRouteGroup, GameServiceCallMetrics.RouteGroupOf(operation)));
    }

    // The registry heartbeat is the probe of the availability. An outage that a proxy answers with
    // an error page marks GameService unavailable, which pauses the durable actions, exactly as a
    // lost connection does, and neither stops the worker nor lets the next heartbeat end anything.
    [Theory]
    [MemberData(nameof(OutageAnswers))]
    public async Task AProxyErrorPageOnTheRegistryHeartbeatMarksGameServiceUnavailableAndStopsNothing(
        string description,
        HttpStatusCode status,
        bool html)
    {
        var secondHeartbeatGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var handler = new RegistryAnswer(() =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1)
            {
                return Task.FromResult<HttpResponseMessage?>(Answer(status, html));
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        }, secondHeartbeatGate.Task);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://game-service.test") };
        var availability = new GameServiceAvailability(TimeProvider.System, NullLogger<GameServiceAvailability>.Instance);
        var config = CreateConfig() with
        {
            RegistryHeartbeatInterval = TimeSpan.FromMilliseconds(20),
            RegistryHeartbeatRetryDelay = TimeSpan.FromMilliseconds(20)
        };
        var readiness = new RealtimeTransportReadiness();
        var lease = new SimulationWorkerRegistrationLease(TimeProvider.System);
        var lifetime = new StoppableLifetime();
        using var service = new SimulationWorkerHeartbeatService(
            new GameServiceClient(httpClient, availability),
            config,
            new SimulationWorkerIdentity("proxy-runtime", DateTime.UtcNow.AddMinutes(-1)),
            new ActiveSimulationSessionStore(),
            readiness,
            CollisionTestWorldFactory.Create(),
            ItemCatalogTestData.CompileFixture(),
            QuestCatalogTestData.CompileFixture(),
            CombatRulesLoader.Load(Path.Combine(AppContext.BaseDirectory, SimulationWorkerConfig.DefaultCombatRulesPath)),
            new SimulationShardRules(),
            NullLogger<SimulationWorkerHeartbeatService>.Instance,
            lease,
            lifetime,
            availability);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await service.StartAsync(timeout.Token);
        readiness.MarkListening(config.UdpPort);
        Assert.True(
            SpinWait.SpinUntil(() => !availability.IsAvailable, TimeSpan.FromSeconds(5)),
            $"{description} did not mark GameService unavailable.");

        // The heartbeat goes again at the retry pace until one succeeds, and the worker never stopped.
        await handler.SecondHeartbeatArrived.Task.WaitAsync(timeout.Token);
        Assert.False(availability.IsAvailable);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        secondHeartbeatGate.SetResult();
        Assert.True(
            SpinWait.SpinUntil(() => availability.IsAvailable && lease.IsValid, TimeSpan.FromSeconds(5)),
            "GameService did not count as available once a heartbeat succeeded.");
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);

        await service.StopAsync(timeout.Token);
    }

    // A player whose durable action meets the proxy's error page learns that the outcome is uncertain
    // and keeps playing: the session, the peer, and the entity all stay.
    [Theory]
    [MemberData(nameof(OutageAnswers))]
    public async Task AProxyErrorPageOnAnItemOperationLeavesThePlayerInTheWorld(
        string description,
        HttpStatusCode status,
        bool html)
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Proxy Hero");
        harness.GameService.OnItemOperation = (_, _, _) => Task.FromResult(Answer(status, html));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.SendItemIntent(RealtimeProtocolRejectionTests.CreateItemIntent());
        await harness.PollUntilAsync(() => client.ItemResults.Count == 1, $"the result of {description}");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(150));

        var result = Assert.Single(client.ItemResults);
        Assert.False(result.Succeeded);
        Assert.Equal("auth_service_unavailable", result.Error.Code);
        Assert.True(result.RequiresInventoryRefresh, "An uncertain outcome must ask the client to refresh.");
        Assert.Null(client.ServerDisconnect);
        Assert.False(client.IsDisconnected);
        Assert.Single(harness.SessionStore.ListActiveSessions());
    }

    // A checkpoint that meets the proxy's error page is sent again with the same state, and nobody is
    // disconnected for it.
    [Theory]
    [MemberData(nameof(OutageAnswers))]
    public async Task AProxyErrorPageOnACheckpointIsRetriedAndEndsNoSession(
        string description,
        HttpStatusCode status,
        bool html)
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            CharacterCheckpointInterval = TimeSpan.FromMilliseconds(300)
        });
        var character = harness.AddCharacter("Checkpoint Hero");
        var calls = 0;
        harness.GameService.OnCheckpoint = (request, _) => Task.FromResult(
            Interlocked.Increment(ref calls) == 1
                ? Answer(status, html)
                : ScriptedGameServiceHandler.Json(new SimulationCharacterCheckpointResponse(request.States.Count, 0)));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        await harness.PollUntilAsync(
            () => harness.GameService.Checkpoints.Count >= 2,
            $"the checkpoint after {description}");

        var first = Assert.Single(harness.GameService.Checkpoints[0].States);
        var again = Assert.Single(harness.GameService.Checkpoints[1].States);
        Assert.Equal(first.SimulationSessionId, again.SimulationSessionId);
        Assert.True(again.Sequence > first.Sequence);
        Assert.Null(client.ServerDisconnect);
        Assert.Single(harness.SessionStore.ListActiveSessions());
    }

    // A session heartbeat request that meets the proxy's error page goes again within its round, and
    // every session keeps its lease instead of being refused.
    [Theory]
    [MemberData(nameof(OutageAnswers))]
    public async Task AProxyErrorPageOnTheSessionHeartbeatIsRetriedAndRefusesNoSession(
        string description,
        HttpStatusCode status,
        bool html)
    {
        var requests = 0;
        var sessions = new ActiveSimulationSessionStore();
        var itemStates = new CharacterItemStateStore();
        var handler = new FixedAnswer(() => Interlocked.Increment(ref requests) == 1
            ? Answer(status, html)
            : null);
        var session = new ActiveSimulationSession(
            Guid.NewGuid(),
            "proxy-token",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Proxy Hero",
            "local-shard-1",
            "development-world-1",
            WorkerId,
            "proxy-runtime",
            DateTime.UtcNow,
            DateTime.UtcNow.AddSeconds(1),
            new PlayerCarryState(1, 10, 200),
            false);
        Assert.Equal(ActiveSimulationSessionRegistration.Joined, sessions.Register(session));
        itemStates.Register(
            session.CharacterId,
            session.SimulationSessionId,
            CharacterItemState.Create(session.CarryState, []),
            out _);
        handler.Success = request => SessionHeartbeatAnswer(request, session);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://game-service.test") };
        var client = new GameServiceClient(httpClient);
        using var service = new SimulationSessionHeartbeatService(
            client,
            new SimulationSessionReleaseService(client, sessions, new CharacterSessionStores()),
            sessions,
            itemStates,
            CreateConfig() with { SimulationSessionHeartbeatRetryDelay = TimeSpan.FromMilliseconds(20) },
            NullLogger<SimulationSessionHeartbeatService>.Instance);

        await service.HeartbeatRoundAsync(TestContext.Current.CancellationToken);

        Assert.True(requests >= 2, $"{description} was not sent again.");
        Assert.True(sessions.IsCurrent(session, DateTime.UtcNow));
        Assert.True(sessions.IsCurrentGeneration(session));
        Assert.False(sessions.TryTakeInvalidation(session.SimulationSessionId, out _));
        Assert.True(sessions.TryGet(session.CharacterId, out var current));
        Assert.True(current!.SessionExpiresAt > session.SessionExpiresAt, "The retried request did not renew the lease.");
    }

    private static HttpResponseMessage SessionHeartbeatAnswer(HttpRequestMessage request, ActiveSimulationSession session)
    {
        var body = request.Content!.ReadFromJsonAsync<SimulationSessionHeartbeatRequest>().GetAwaiter().GetResult()!;
        return ScriptedGameServiceHandler.Json(new SimulationSessionHeartbeatResponse(
            body.Sessions.Select(entry => new SimulationSessionHeartbeatResult(
                entry.SimulationSessionId,
                new SimulationSessionLeaseResponse(
                    entry.SimulationSessionId,
                    session.CharacterId,
                    session.ShardId,
                    session.WorkerId,
                    session.WorkerRuntimeId,
                    DateTime.UtcNow.AddSeconds(30),
                    session.CarryState.ItemStateRevision,
                    session.CarryState.CarriedWeight,
                    session.CarryState.CarryCapacity,
                    false)
                {
                    LeaseDurationMilliseconds = 30_000
                },
                null)).ToArray()));
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, bool html)
    {
        var response = new HttpResponseMessage(status);
        if (html)
        {
            response.Content = new StringContent(
                $"<html><body><h1>{(int)status}</h1></body></html>",
                Encoding.UTF8,
                "text/html");
        }

        return response;
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string code)
    {
        return new HttpResponseMessage(status)
        {
            Content = JsonContent.Create(new { code, detail = "Refused." })
        };
    }

    private static GameServiceClient CreateClient(HttpMessageHandler handler)
    {
        return new GameServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://game-service.test") });
    }

    private static async Task<Outcome> CallAsync(GameServiceClient client, string operation)
    {
        var sessionId = Guid.NewGuid();
        var cancellationToken = CancellationToken.None;
        switch (operation)
        {
            case "registry heartbeat":
                return Describe(await client.HeartbeatSimulationWorkerAsync(
                    WorkerId,
                    new SimulationWorkerHeartbeatRequest(
                        "local-fleet",
                        "local-node-1",
                        "local-shard-1",
                        "development-world-1",
                        "runtime-1",
                        DateTime.UtcNow,
                        "127.0.0.1",
                        27015,
                        100,
                        0,
                        RealtimeProtocol.Version,
                        GameSimulationCompatibility.Revision,
                        "collision",
                        "items",
                        "quests",
                        Mmorpg.Server.Common.Contracts.ServiceContract.Version),
                    cancellationToken));
            case "item operation":
                return Describe(await client.MutateSimulationItemsAsync(
                    sessionId,
                    new SimulationItemOperationRequest(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        WorkerId,
                        "runtime-1",
                        "local-shard-1",
                        "token",
                        new SimulationItemAccessRequest(false, false, false),
                        "relocate",
                        1),
                    cancellationToken));
            case "checkpoint":
                return Describe(await client.CheckpointCharacterStatesAsync(
                    WorkerId,
                    new SimulationCharacterCheckpointRequest(
                        "runtime-1",
                        [new SimulationCharacterCheckpointEntry(Guid.NewGuid(), 1, 1f, 0f, 2f, 90f, 50, 40)]),
                    cancellationToken));
            case "session heartbeat":
                return Describe(await client.HeartbeatSimulationSessionsAsync(
                    WorkerId,
                    new SimulationSessionHeartbeatRequest(
                        "runtime-1",
                        [new SimulationSessionHeartbeatEntry(sessionId, "token")]),
                    cancellationToken));
            case "join ticket":
                return Describe(await client.ConsumeJoinTicketAsync(
                    new RealtimeJoinRequest(
                        Guid.NewGuid(),
                        Enumerable.Range(0, RealtimeProtocol.JoinNonceSize).Select(value => (byte)value).ToArray(),
                        Enumerable.Range(100, RealtimeProtocol.JoinProofSize).Select(value => (byte)value).ToArray()),
                    WorkerId,
                    "runtime-1",
                    "local-shard-1",
                    cancellationToken));
            case "corpse restore":
                return Describe(await client.RestoreDurableCorpsesAsync(
                    WorkerId,
                    "runtime-1",
                    "local-shard-1",
                    cancellationToken));
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private static Outcome Describe<T>(GameServiceResult<T> result)
    {
        return new Outcome(result.Succeeded, result.Error?.Code, result.StatusCode, result.FailureReason, result.Error?.Message ?? string.Empty);
    }

    private sealed record Outcome(bool Succeeded, string? Code, int StatusCode, string? Reason, string Message);

    private static SimulationWorkerConfig CreateConfig()
    {
        return new SimulationWorkerConfig(
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
            TimeSpan.FromHours(1),
            "127.0.0.1:6379",
            TimeSpan.FromSeconds(1));
    }

    // Answers every request with the same thing, or with Success when it returns null.
    private sealed class FixedAnswer(Func<HttpResponseMessage?> answer) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Success { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = answer();
            if (response is null && Success is { } success)
            {
                response = success(request);
            }

            return Task.FromResult(response
                ?? throw new InvalidOperationException("The test gave no answer for " + request.RequestUri));
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw exception;
        }
    }

    // Answers the first heartbeat with what the test scripts, and the second only once its gate opens.
    private sealed class RegistryAnswer(Func<Task<HttpResponseMessage?>> script, Task secondGate) : HttpMessageHandler
    {
        private int heartbeats;

        public TaskCompletionSource SecondHeartbeatArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                return Answer(HttpStatusCode.NotFound, html: false);
            }

            var scripted = await script();
            if (scripted is not null)
            {
                return scripted;
            }

            if (Interlocked.Increment(ref heartbeats) == 1)
            {
                SecondHeartbeatArrived.TrySetResult();
                await secondGate.WaitAsync(cancellationToken);
            }

            var heartbeat = (await request.Content!.ReadFromJsonAsync<SimulationWorkerHeartbeatRequest>(cancellationToken))!;
            var now = DateTime.UtcNow;
            return ScriptedGameServiceHandler.Json(new SimulationWorkerHeartbeatResponse(
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
                now.AddSeconds(30),
                150));
        }
    }

    private sealed class StoppableLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            stopping.Cancel();
        }
    }
}
