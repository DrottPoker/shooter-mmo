using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Tests;

// One heartbeat round renews every active session with one GameService request per batch, and
// applies the answer of every session as the heartbeat of a single session did: the renewed lease
// with its account session end and carry state, or the refusal. A request that fails for a passing
// reason goes again within the round's interval, so it costs no lease.
public sealed class SimulationSessionHeartbeatServiceTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "test-runtime";
    private const long LeaseMilliseconds = 30_000;

    [Fact]
    public async Task ARoundSendsTheSessionsInBatchesOfTheBatchSizeAndRenewsEveryOne()
    {
        var gameService = new SessionHeartbeatHandler();
        using var worker = new HeartbeatWorker(gameService, batchSize: 3);
        var sessions = Enumerable.Range(0, 7).Select(_ => worker.AddSession()).ToArray();

        await worker.Service.HeartbeatRoundAsync(TestContext.Current.CancellationToken);

        var requests = gameService.Requests;
        Assert.Equal([3, 3, 1], requests.Select(request => request.Sessions.Count).OrderDescending());
        Assert.All(gameService.Paths, path => Assert.Equal($"/api/simulation-workers/{WorkerId}/session-heartbeat", path));
        Assert.All(requests, request => Assert.Equal(RuntimeId, request.RuntimeId));
        Assert.Equal(
            sessions.Select(session => session.SimulationSessionId).Order(),
            requests.SelectMany(request => request.Sessions).Select(entry => entry.SimulationSessionId).Order());
        Assert.All(sessions, session =>
        {
            Assert.True(worker.Sessions.TryGet(session.CharacterId, out var current));
            Assert.True(current!.SessionExpiresAt > session.SessionExpiresAt);
            Assert.Equal(2, current.CarryState.ItemStateRevision);
        });
    }

    // A refused session ends, an account session end marks its session, and a carry state that
    // contradicts the stored one ends its session; none of them changes what the others get.
    [Fact]
    public async Task EverySessionOfABatchIsAnsweredOnItsOwn()
    {
        var gameService = new SessionHeartbeatHandler();
        using var worker = new HeartbeatWorker(gameService, batchSize: 250);
        var renewed = worker.AddSession();
        var refused = worker.AddSession();
        var replaced = worker.AddSession();
        var conflicting = worker.AddSession();
        gameService.Answer = (entry, lease) =>
            entry.SimulationSessionId == refused.SimulationSessionId
                ? new SimulationSessionHeartbeatResult(
                    entry.SimulationSessionId,
                    null,
                    new SimulationWorkerErrorResponse("invalid_simulation_session", "Gone."))
                : entry.SimulationSessionId == replaced.SimulationSessionId
                    ? new SimulationSessionHeartbeatResult(entry.SimulationSessionId, lease with { AccountSessionEnded = "account_session_replaced" }, null)
                    : entry.SimulationSessionId == conflicting.SimulationSessionId
                        ? new SimulationSessionHeartbeatResult(entry.SimulationSessionId, lease with { ItemStateRevision = 1, CarriedWeight = 99 }, null)
                        : new SimulationSessionHeartbeatResult(entry.SimulationSessionId, lease, null);

        await worker.Service.HeartbeatRoundAsync(TestContext.Current.CancellationToken);

        Assert.Single(gameService.Requests);
        Assert.True(worker.Sessions.IsCurrent(renewed, DateTime.UtcNow));
        Assert.False(worker.Sessions.TryGetAccountSessionEnd(renewed, out _));
        Assert.True(worker.ItemStates.TryGet(renewed.CharacterId, renewed.SimulationSessionId, out _));

        Assert.False(worker.Sessions.IsCurrentGeneration(refused));
        Assert.True(worker.Sessions.TryTakeInvalidation(refused.SimulationSessionId, out var refusal));
        Assert.Equal("session_revoked", refusal!.Code);
        Assert.False(worker.ItemStates.TryGet(refused.CharacterId, refused.SimulationSessionId, out _));

        Assert.True(worker.Sessions.IsCurrent(replaced, DateTime.UtcNow));
        Assert.True(worker.Sessions.TryGetAccountSessionEnd(replaced, out var end));
        Assert.Equal("account_session_replaced", end);

        Assert.False(worker.Sessions.IsCurrentGeneration(conflicting));
        Assert.True(worker.Sessions.TryTakeInvalidation(conflicting.SimulationSessionId, out var conflict));
        Assert.Equal("carry_state_conflict", conflict!.Code);
    }

    // GameService could not be reached: the request goes again after the retry delay, and the
    // lease is renewed once GameService answers.
    [Fact]
    public async Task AFailedRequestGoesAgainWithinTheIntervalAndCostsNoLease()
    {
        var gameService = new SessionHeartbeatHandler { FailuresBeforeAnswer = 2 };
        using var worker = new HeartbeatWorker(gameService, batchSize: 250);
        var session = worker.AddSession();

        await worker.Service.HeartbeatRoundAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, gameService.Requests.Count);
        Assert.True(worker.Sessions.TryGet(session.CharacterId, out var current));
        Assert.True(current!.SessionExpiresAt > session.SessionExpiresAt);
    }

    // A retry names only the sessions that are still current: a session that went meanwhile is
    // not renewed again.
    [Fact]
    public async Task ARetryNamesOnlyTheSessionsThatAreStillCurrent()
    {
        var gameService = new SessionHeartbeatHandler { FailuresBeforeAnswer = 1 };
        using var worker = new HeartbeatWorker(gameService, batchSize: 250);
        var staying = worker.AddSession();
        var leaving = worker.AddSession();
        gameService.OnRequest = _ => worker.Sessions.Remove(
            leaving.CharacterId,
            leaving.SimulationSessionId,
            leaving.SimulationSessionToken);

        await worker.Service.HeartbeatRoundAsync(TestContext.Current.CancellationToken);

        var requests = gameService.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Equal(2, requests[0].Sessions.Count);
        Assert.Equal(staying.SimulationSessionId, Assert.Single(requests[1].Sessions).SimulationSessionId);
    }

    // A failure that the same request cannot get past, such as a malformed request, is not sent
    // again, and it ends no session: the lease runs out by its local deadline unless a later round
    // renews it.
    [Fact]
    public async Task ARequestRefusedAsAWholeIsNotSentAgainAndEndsNoSession()
    {
        var gameService = new SessionHeartbeatHandler
        {
            FailuresBeforeAnswer = int.MaxValue,
            FailureStatus = HttpStatusCode.BadRequest
        };
        using var worker = new HeartbeatWorker(gameService, batchSize: 250);
        var session = worker.AddSession();

        await worker.Service.HeartbeatRoundAsync(TestContext.Current.CancellationToken);

        Assert.Single(gameService.Requests);
        Assert.True(worker.Sessions.TryGet(session.CharacterId, out var current));
        Assert.Equal(session.SessionExpiresAt, current!.SessionExpiresAt);
        Assert.False(worker.Sessions.TryTakeInvalidation(session.SimulationSessionId, out _));
    }

    // The retries of a round end with its interval, so a GameService that stays away costs at
    // most the requests that fit into one interval. The retry delay does not divide the interval:
    // with 200 ms the fifth retry would be due exactly at the interval's end, and a timer that
    // fires a millisecond early then lets a sixth request through. With 300 ms the requests go
    // at 0, 300, 600, and 900 ms, and the next would be far past the end.
    [Fact]
    public async Task RetriesEndWithTheRoundsInterval()
    {
        var gameService = new SessionHeartbeatHandler { FailuresBeforeAnswer = int.MaxValue };
        using var worker = new HeartbeatWorker(
            gameService,
            batchSize: 250,
            interval: TimeSpan.FromSeconds(1),
            retryDelay: TimeSpan.FromMilliseconds(300));
        var session = worker.AddSession();

        await worker.Service.HeartbeatRoundAsync(TestContext.Current.CancellationToken);

        Assert.InRange(gameService.Requests.Count, 2, 4);
        Assert.True(worker.Sessions.TryGet(session.CharacterId, out var current));
        Assert.Equal(session.SessionExpiresAt, current!.SessionExpiresAt);
    }

    // The worker's session store, item states, and heartbeat service over a scripted GameService.
    private sealed class HeartbeatWorker : IDisposable
    {
        private readonly HttpClient httpClient;

        public HeartbeatWorker(
            SessionHeartbeatHandler gameService,
            int batchSize,
            TimeSpan? interval = null,
            TimeSpan? retryDelay = null)
        {
            httpClient = new HttpClient(gameService) { BaseAddress = new Uri("http://game-service.test") };
            var client = new GameServiceClient(httpClient);
            Service = new SimulationSessionHeartbeatService(
                client,
                new SimulationSessionReleaseService(client, Sessions, new CharacterSessionStores()),
                Sessions,
                ItemStates,
                CreateConfig(interval ?? TimeSpan.FromSeconds(10)) with
                {
                    SimulationSessionHeartbeatBatchSize = batchSize,
                    SimulationSessionHeartbeatRetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(20)
                },
                NullLogger<SimulationSessionHeartbeatService>.Instance);
        }

        public ActiveSimulationSessionStore Sessions { get; } = new();

        public CharacterItemStateStore ItemStates { get; } = new();

        public SimulationSessionHeartbeatService Service { get; }

        // A joined session whose local lease ends in a second, at item-state revision 1.
        public ActiveSimulationSession AddSession()
        {
            var session = new ActiveSimulationSession(
                Guid.NewGuid(),
                "token-" + Guid.NewGuid().ToString("N"),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Heartbeat Hero",
                "local-shard-1",
                "development-world-1",
                WorkerId,
                RuntimeId,
                DateTime.UtcNow,
                DateTime.UtcNow.AddSeconds(1),
                new PlayerCarryState(1, 10, 200),
                false);
            Assert.Equal(ActiveSimulationSessionRegistration.Joined, Sessions.Register(session));
            ItemStates.Register(
                session.CharacterId,
                session.SimulationSessionId,
                CharacterItemState.Create(session.CarryState, []),
                out _);
            return session;
        }

        public void Dispose()
        {
            Service.Dispose();
            httpClient.Dispose();
        }
    }

    // Answers session heartbeats: first FailuresBeforeAnswer failures, then a renewed lease at
    // item-state revision 2 for every session, unless Answer says otherwise.
    private sealed class SessionHeartbeatHandler : HttpMessageHandler
    {
        private readonly Lock gate = new();
        private readonly List<SimulationSessionHeartbeatRequest> requests = [];
        private readonly List<string> paths = [];

        public int FailuresBeforeAnswer { get; init; }

        public HttpStatusCode FailureStatus { get; init; } = HttpStatusCode.ServiceUnavailable;

        public Func<SimulationSessionHeartbeatEntry, SimulationSessionLeaseResponse, SimulationSessionHeartbeatResult> Answer { get; set; } =
            (entry, lease) => new SimulationSessionHeartbeatResult(entry.SimulationSessionId, lease, null);

        // Runs when a request arrives, before it is answered.
        public Action<SimulationSessionHeartbeatRequest>? OnRequest { get; set; }

        public List<SimulationSessionHeartbeatRequest> Requests
        {
            get
            {
                lock (gate)
                {
                    return [.. requests];
                }
            }
        }

        public List<string> Paths
        {
            get
            {
                lock (gate)
                {
                    return [.. paths];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var heartbeat = (await request.Content!.ReadFromJsonAsync<SimulationSessionHeartbeatRequest>(cancellationToken))!;
            int count;
            lock (gate)
            {
                requests.Add(heartbeat);
                paths.Add(request.RequestUri!.AbsolutePath);
                count = requests.Count;
            }

            OnRequest?.Invoke(heartbeat);
            if (count <= FailuresBeforeAnswer)
            {
                return new HttpResponseMessage(FailureStatus)
                {
                    Content = JsonContent.Create(new { code = "game_service_failure", detail = "GameService failed." })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SimulationSessionHeartbeatResponse(heartbeat.Sessions
                    .Select(entry => Answer(entry, Lease(entry, heartbeat.RuntimeId)))
                    .ToArray()))
            };
        }

        private static SimulationSessionLeaseResponse Lease(SimulationSessionHeartbeatEntry entry, string runtimeId)
        {
            return new SimulationSessionLeaseResponse(
                entry.SimulationSessionId,
                Guid.NewGuid(),
                "local-shard-1",
                WorkerId,
                runtimeId,
                DateTime.UtcNow.AddMilliseconds(LeaseMilliseconds),
                2,
                10,
                200,
                false)
            {
                LeaseDurationMilliseconds = LeaseMilliseconds
            };
        }
    }

    private static SimulationWorkerConfig CreateConfig(TimeSpan interval)
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
            interval,
            TimeSpan.FromHours(1),
            "127.0.0.1:6379",
            TimeSpan.FromSeconds(1));
    }
}
