using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

// The worker's registry heartbeat, outage monitor, and session heartbeat in fake time against a
// GameService that falls silent. The worker plays on through a silence shorter than its outage
// tolerance, retries the registry heartbeat every two seconds, keeps every session, and is as
// before after the first successful heartbeat; a longer silence stops it in order, and a runtime
// that another one replaced during the silence stops at once.
public sealed class GameServiceOutageTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(SimulationWorkerConfig.DefaultGameServiceOutageToleranceSeconds);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(SimulationWorkerConfig.DefaultRegistryHeartbeatRetryMilliseconds);

    [Theory]
    [InlineData(15)]
    [InlineData(60)]
    [InlineData(115)]
    public async Task TheWorkerPlaysOnThroughASilenceShorterThanItsToleranceAndIsAsBeforeAfterIt(int silentSeconds)
    {
        await using var worker = await OutageWorker.StartAsync();
        var session = worker.AddSession();
        var silenceStarted = worker.Time.Elapsed;

        worker.GameService.Silent = true;
        await worker.AdvanceAsync(TimeSpan.FromSeconds(silentSeconds));

        Assert.False(worker.Lifetime.StopRequested.Task.IsCompleted);
        Assert.False(worker.Availability.IsAvailable);
        Assert.True(worker.Sessions.IsCurrent(session, worker.UtcNow));
        Assert.Single(worker.Sessions.ListActiveSessions());
        // The local registration lease ran out after its 30 seconds, which only closes joins.
        Assert.Equal(silentSeconds < 29, worker.Lease.IsValid);
        // The first heartbeat of the silence came on the interval, every other one two seconds later.
        var failed = worker.GameService.Heartbeats.Where(time => time > silenceStarted).ToArray();
        Assert.Equal(silenceStarted + Interval, failed[0]);
        Assert.All(failed.Zip(failed.Skip(1)), pair => Assert.Equal(Retry, pair.Second - pair.First));
        Assert.Equal(((silentSeconds - 10) / 2) + 1, failed.Length);
        // No session heartbeat round went while GameService counted as unavailable.
        var unavailableSince = failed[0];
        Assert.DoesNotContain(worker.GameService.SessionHeartbeats, time => time > unavailableSince);

        worker.GameService.Silent = false;
        await worker.AdvanceAsync(Retry);

        var answered = worker.GameService.Heartbeats.Last();
        Assert.True(answered - silenceStarted <= TimeSpan.FromSeconds(silentSeconds) + Retry);
        Assert.True(worker.Availability.IsAvailable);
        Assert.True(worker.Lease.IsValid);

        // The heartbeat is back on its interval, and a session heartbeat round renewed the session
        // within it: right after the answer when a round waited for it, else at its time.
        await worker.AdvanceAsync(Interval);
        Assert.Equal(answered + Interval, worker.GameService.Heartbeats.Last());
        var round = worker.GameService.SessionHeartbeats.First(time => time >= answered);
        Assert.True(round <= answered + Interval);
        if (silentSeconds >= 20)
        {
            Assert.InRange(round, answered, answered + TimeSpan.FromSeconds(1));
        }

        Assert.True(worker.Sessions.TryGet(session.CharacterId, out var renewed));
        Assert.Equal(worker.UtcAt(worker.GameService.SessionHeartbeats.Last()).AddSeconds(150), renewed!.SessionExpiresAt);
        Assert.False(worker.Lifetime.StopRequested.Task.IsCompleted);
        Assert.Equal(SimulationWorkerStopReason.ServerShutdownCode, worker.StopReason.Code);
        Assert.Single(worker.AvailabilityLogger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Single(worker.AvailabilityLogger.Entries, entry => entry.Level == LogLevel.Information);
    }

    [Fact]
    public async Task ASilenceLongerThanTheToleranceStopsTheWorkerAndTellsThePlayersWhy()
    {
        await using var worker = await OutageWorker.StartAsync();
        worker.AddSession();
        var lastAnswered = worker.GameService.Heartbeats.Last();

        worker.GameService.Silent = true;
        await worker.AdvanceAsync(TimeSpan.FromSeconds(125), stopsOnTheWay: true);

        Assert.True(worker.Lifetime.StopRequested.Task.IsCompleted);
        var stoppedAt = worker.Lifetime.StoppedAt!.Value;
        Assert.InRange(stoppedAt - lastAnswered, Tolerance, Tolerance + TimeSpan.FromSeconds(1));
        Assert.Equal(GameServiceAvailability.OutageStopCode, worker.StopReason.Code);
        Assert.Single(worker.MonitorLogger.Entries, entry => entry.Level == LogLevel.Critical);
        // No session was invalidated on the way.
        Assert.Single(worker.Sessions.ListActiveSessions());
    }

    [Fact]
    public async Task ARuntimeThatAnotherOneReplacedDuringTheSilenceStopsAtItsFirstAnswer()
    {
        await using var worker = await OutageWorker.StartAsync();
        worker.GameService.Silent = true;
        await worker.AdvanceAsync(TimeSpan.FromSeconds(40));
        Assert.False(worker.Lifetime.StopRequested.Task.IsCompleted);

        worker.GameService.Silent = false;
        worker.GameService.RuntimeChanged = true;
        await worker.AdvanceAsync(Retry, stopsOnTheWay: true);

        Assert.True(worker.Lifetime.StopRequested.Task.IsCompleted);
        Assert.Equal(worker.GameService.Heartbeats.Last(), worker.Lifetime.StoppedAt);
        Assert.False(worker.Lease.HasLease);
        Assert.Equal(SimulationWorkerStopReason.ServerShutdownCode, worker.StopReason.Code);
    }

    // A durable call whose outcome is uncertain while GameService still counts as available asks
    // for the next registry heartbeat at once, so the worker learns of an outage within about one
    // timeout instead of waiting for the interval.
    [Fact]
    public async Task AnUncertainDurableCallAsksForTheNextHeartbeatAtOnce()
    {
        await using var worker = await OutageWorker.StartAsync();
        var lastAnswered = worker.GameService.Heartbeats.Last();
        await worker.AdvanceAsync(TimeSpan.FromSeconds(3));
        worker.GameService.Silent = true;

        var release = await worker.Client.ReleaseSimulationSessionAsync(
            Guid.NewGuid(),
            RealtimeServerHarness.RuntimeId,
            "token",
            CancellationToken.None);
        Assert.Equal("auth_service_unavailable", release.Error!.Code);
        await worker.SettleAsync();

        // Three seconds after the last heartbeat instead of ten.
        Assert.Equal(lastAnswered + TimeSpan.FromSeconds(3), worker.GameService.Heartbeats.Last());
        Assert.False(worker.Availability.IsAvailable);
    }

    // A worker whose realtime loop hangs keeps its port bound and its heartbeat service alive, so
    // without this GameService would go on placing players on a worker that simulates nothing. The
    // renewal stops while the loop is silent beyond the limit, the registry lease ends, and GameService
    // takes the Shard offline by its rules; the worker neither stops itself nor loses what it holds.
    [Fact]
    public async Task ASilentLoopStopsTheRenewalsSoTheRegistryLeaseEndsAndTheWorkerNeitherStopsNorLetsGoOfItsSessions()
    {
        using var renewals = new RenewalCounter();
        await using var worker = await OutageWorker.StartAsync(new RealtimeLoopLiveness());
        var session = worker.AddSession();
        await worker.AdvanceWithTicksAsync(TimeSpan.FromSeconds(25));
        var beforeHang = worker.GameService.Heartbeats.Length;
        Assert.True(worker.Lease.IsValid);
        Assert.True(worker.Availability.IsAvailable);
        Assert.True(beforeHang >= 3);

        // The loop hangs: no tick completes from here on, for far longer than the outage tolerance.
        await worker.AdvanceAsync(TimeSpan.FromSeconds(200));

        // At most the one heartbeat that was already due went out before the silence passed the limit.
        Assert.InRange(worker.GameService.Heartbeats.Length - beforeHang, 0, 1);
        Assert.False(worker.Lease.IsValid, "The registry lease must end, so GameService takes the Shard offline.");
        // Durable actions pause as in an outage, since a lease that is stale at GameService would end
        // the sessions of the players who ask.
        Assert.False(worker.Availability.IsAvailable);
        // The worker never kills itself: the outage monitor does not count a silence that the worker chose.
        Assert.False(worker.Lifetime.StopRequested.Task.IsCompleted);
        Assert.True(worker.Availability.TimeSinceLastHeartbeat is { } age && age < Tolerance);
        Assert.Single(worker.Sessions.ListActiveSessions());
        Assert.True(worker.Sessions.IsCurrentGeneration(session));
        // One error line for the whole episode, and one count.
        Assert.Single(worker.HeartbeatLogger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal(1, renewals.Count);
    }

    // When the loop ticks again the worker registers again, as after any lapse: the next heartbeat
    // renews the same runtime, GameService accepts it however long it was gone, and the sessions that
    // the worker holds are untouched by it. A session whose own lease ran out meanwhile is refused by
    // the session heartbeat and ended there, as every refused session is.
    [Fact]
    public async Task ALoopThatTicksAgainRegistersAgainAndKeepsItsSessions()
    {
        await using var worker = await OutageWorker.StartAsync(new RealtimeLoopLiveness());
        var session = worker.AddSession();
        await worker.AdvanceWithTicksAsync(TimeSpan.FromSeconds(15));
        await worker.AdvanceAsync(TimeSpan.FromSeconds(60));
        Assert.False(worker.Lease.IsValid);
        Assert.False(worker.Availability.IsAvailable);
        var silent = worker.GameService.Heartbeats.Length;

        // The loop completes a tick, and the next look of the heartbeat service finds it alive.
        worker.Liveness!.TickCompleted(worker.Time.GetTimestamp());
        await worker.AdvanceWithTicksAsync(TimeSpan.FromSeconds(3));

        Assert.True(worker.GameService.Heartbeats.Length > silent, "The worker did not register again.");
        Assert.True(worker.Lease.IsValid);
        Assert.True(worker.Availability.IsAvailable);
        Assert.False(worker.Lifetime.StopRequested.Task.IsCompleted);
        Assert.Single(worker.Sessions.ListActiveSessions());
        Assert.True(worker.Sessions.IsCurrentGeneration(session));
        Assert.Single(
            worker.HeartbeatLogger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("ticks again", StringComparison.Ordinal));
        // The heartbeat is back on its interval.
        var registered = worker.GameService.Heartbeats.Last();
        await worker.AdvanceWithTicksAsync(Interval);
        Assert.Equal(registered + Interval, worker.GameService.Heartbeats.Last());
    }

    // A pause shorter than the limit, such as a garbage collection or a long catch-up, is not a hang.
    [Fact]
    public async Task ALoopThatIsSilentForLessThanTheLimitIsNotHeldBack()
    {
        await using var worker = await OutageWorker.StartAsync(new RealtimeLoopLiveness());
        await worker.AdvanceWithTicksAsync(TimeSpan.FromSeconds(12));

        // A silence of four seconds, which a ready worker allows, over the time of one heartbeat.
        await worker.AdvanceAsync(TimeSpan.FromSeconds(4));
        var heartbeats = worker.GameService.Heartbeats.Length;
        await worker.AdvanceWithTicksAsync(TimeSpan.FromSeconds(20));

        Assert.True(worker.GameService.Heartbeats.Length >= heartbeats + 2);
        Assert.True(worker.Lease.IsValid);
        Assert.True(worker.Availability.IsAvailable);
        Assert.DoesNotContain(worker.HeartbeatLogger.Entries, entry => entry.Level == LogLevel.Error);
    }

    // A loop that never completes a tick is counted from the moment the transport listened: the worker
    // that is only starting is not held back, and one whose loop never starts does not register anew.
    [Fact]
    public async Task ALoopThatNeverTicksIsCountedFromTheMomentTheTransportListened()
    {
        await using var worker = await OutageWorker.StartAsync(new RealtimeLoopLiveness());
        // The first heartbeat went out at once, before any silence could be counted.
        Assert.Single(worker.GameService.Heartbeats);

        await worker.AdvanceAsync(TimeSpan.FromSeconds(40));

        Assert.Single(worker.GameService.Heartbeats);
        Assert.False(worker.Lease.IsValid);
        Assert.False(worker.Lifetime.StopRequested.Task.IsCompleted);
    }

    // Counts the episodes of a withheld renewal that the heartbeat service reports.
    private sealed class RenewalCounter : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener listener = new();
        private long count;

        public RenewalCounter()
        {
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == GameServiceCallMetrics.MeterName
                    && instrument.Name == "simulation_worker.registry.renewals_withheld")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref count, value));
            listener.Start();
        }

        public long Count => Interlocked.Read(ref count);

        public void Dispose()
        {
            listener.Dispose();
        }
    }

    // The registry heartbeat, the outage monitor, and the session heartbeat of one worker, all on
    // a stepped clock, against a GameService that answers or stays silent.
    private sealed class OutageWorker : IAsyncDisposable
    {
        private static readonly DateTimeOffset Start = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        private readonly HttpClient httpClient;
        private readonly SimulationWorkerHeartbeatService heartbeat;
        private readonly GameServiceOutageMonitor monitor;
        private readonly SimulationSessionHeartbeatService sessionHeartbeat;
        private readonly RealtimeTransportReadiness readiness = new();

        private OutageWorker(RealtimeLoopLiveness? liveness)
        {
            Liveness = liveness;
            GameService = new OutageGameService(Time);
            httpClient = new HttpClient(GameService) { BaseAddress = new Uri("http://game-service.test") };
            Availability = new GameServiceAvailability(Time, AvailabilityLogger);
            Client = new GameServiceClient(httpClient, Availability);
            Lease = new SimulationWorkerRegistrationLease(Time);
            var config = RealtimeServerHarness.CreateConfig(27015, new RealtimeServerHarnessOptions()) with
            {
                RegistryHeartbeatInterval = Interval,
                SimulationSessionHeartbeatInterval = Interval
            };
            heartbeat = new SimulationWorkerHeartbeatService(
                Client,
                config,
                new SimulationWorkerIdentity(RealtimeServerHarness.RuntimeId, Start.UtcDateTime.AddMinutes(-1)),
                Sessions,
                readiness,
                CollisionTestWorldFactory.Create(),
                ItemCatalogTestData.CompileFixture(),
                QuestCatalogTestData.CompileFixture(),
                CombatRulesLoader.Load(Path.Combine(AppContext.BaseDirectory, SimulationWorkerConfig.DefaultCombatRulesPath)),
                new SimulationShardRules(),
                HeartbeatLogger,
                Lease,
                Lifetime,
                Availability,
                StopReason,
                Time,
                liveness,
                CallMetrics);
            monitor = new GameServiceOutageMonitor(Availability, config, StopReason, Lifetime, MonitorLogger, Time);
            sessionHeartbeat = new SimulationSessionHeartbeatService(
                Client,
                new SimulationSessionReleaseService(
                    Client,
                    Sessions,
                    CharacterStateTestData.SessionStores(ItemStates),
                    availability: Availability),
                Sessions,
                ItemStates,
                config,
                new CapturingLogger<SimulationSessionHeartbeatService>(),
                Availability,
                Time);
        }

        public SteppedTimeProvider Time { get; } = new(Start);

        // Null in the tests of the outage, where no loop runs; the tests of a silent loop give one.
        public RealtimeLoopLiveness? Liveness { get; }

        public GameServiceCallMetrics CallMetrics { get; } = new();

        public DateTime UtcNow => Time.GetUtcNow().UtcDateTime;

        public DateTime UtcAt(TimeSpan elapsed) => Start.UtcDateTime + elapsed;

        public OutageGameService GameService { get; }

        public GameServiceClient Client { get; }

        public GameServiceAvailability Availability { get; }

        public SimulationWorkerRegistrationLease Lease { get; }

        public SimulationWorkerStopReason StopReason { get; } = new();

        public StopRecordingLifetime Lifetime { get; } = new();

        public ActiveSimulationSessionStore Sessions { get; } = new();

        public CharacterItemStateStore ItemStates { get; } = new();

        public CapturingLogger<GameServiceAvailability> AvailabilityLogger { get; } = new();

        public CapturingLogger<SimulationWorkerHeartbeatService> HeartbeatLogger { get; } = new();

        public CapturingLogger<GameServiceOutageMonitor> MonitorLogger { get; } = new();

        // Starts the services and lets the first heartbeat register the worker.
        public static async Task<OutageWorker> StartAsync(RealtimeLoopLiveness? liveness = null)
        {
            var worker = new OutageWorker(liveness);
            worker.Lifetime.Time = worker.Time;
            await worker.heartbeat.StartAsync(CancellationToken.None);
            await worker.monitor.StartAsync(CancellationToken.None);
            await worker.sessionHeartbeat.StartAsync(CancellationToken.None);
            worker.readiness.MarkListening(27015);
            await worker.SettleAsync();
            Assert.True(worker.Lease.IsValid);
            return worker;
        }

        // Moves the clock like AdvanceAsync, and the realtime loop completes a tick every second of it,
        // as a loop that runs does.
        public async Task AdvanceWithTicksAsync(TimeSpan duration)
        {
            var remaining = duration;
            while (remaining > TimeSpan.Zero)
            {
                var step = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
                Liveness!.TickCompleted(Time.GetTimestamp());
                Time.Advance(step);
                remaining -= step;
                await SettleAsync();
            }
        }

        // A session that joined now with the lease GameService grants.
        public ActiveSimulationSession AddSession()
        {
            var session = RealtimeTestSessions.Create() with
            {
                JoinedAt = UtcNow,
                SessionExpiresAt = UtcNow.AddSeconds(150)
            };
            Sessions.Register(session);
            return session;
        }

        // Moves the clock one second at a time, so the outage monitor checks every second, and
        // lets every service run what came due before the next step.
        public async Task AdvanceAsync(TimeSpan duration, bool stopsOnTheWay = false)
        {
            var remaining = duration;
            while (remaining > TimeSpan.Zero)
            {
                var step = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
                Time.Advance(step);
                remaining -= step;
                await SettleAsync(stopsOnTheWay ? 2 : 3);
            }
        }

        // Every service waits on the clock again and no request is in flight. A service that
        // stopped waits for nothing, so after a stop fewer timers may remain.
        public async Task SettleAsync(int idleTimers = 3)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var quiet = 0;
            while (quiet < 3)
            {
                await Task.Delay(2, timeout.Token);
                quiet = GameService.InFlight == 0
                    && (Time.PendingTimers >= 3 || (idleTimers < 3 && Lifetime.StopRequested.Task.IsCompleted))
                        ? quiet + 1
                        : 0;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await sessionHeartbeat.StopAsync(CancellationToken.None);
            await monitor.StopAsync(CancellationToken.None);
            await heartbeat.StopAsync(CancellationToken.None);
            heartbeat.Dispose();
            monitor.Dispose();
            sessionHeartbeat.Dispose();
            httpClient.Dispose();
            CallMetrics.Dispose();
        }
    }

    // Answers the registry heartbeat, the session heartbeat, and the offline call, records the
    // clock time of every heartbeat, and fails every call like an unreachable GameService while
    // it is silent.
    private sealed class OutageGameService(SteppedTimeProvider time) : HttpMessageHandler
    {
        private readonly object sync = new();
        private readonly List<TimeSpan> heartbeats = [];
        private readonly List<TimeSpan> sessionHeartbeats = [];
        private int inFlight;

        public bool Silent { get; set; }

        // Another runtime owns the worker id now.
        public bool RuntimeChanged { get; set; }

        public int InFlight => Volatile.Read(ref inFlight);

        // The clock time of every registry heartbeat, also the failed ones.
        public TimeSpan[] Heartbeats
        {
            get
            {
                lock (sync)
                {
                    return [.. heartbeats];
                }
            }
        }

        public TimeSpan[] SessionHeartbeats
        {
            get
            {
                lock (sync)
                {
                    return [.. sessionHeartbeats];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref inFlight);
            try
            {
                return await AnswerAsync(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        }

        private async Task<HttpResponseMessage> AnswerAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var now = time.GetUtcNow().UtcDateTime;
            if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                lock (sync)
                {
                    heartbeats.Add(time.Elapsed);
                }

                if (Silent)
                {
                    throw new HttpRequestException("GameService cannot be reached.");
                }

                if (RuntimeChanged)
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = JsonContent.Create(new
                        {
                            code = "worker_runtime_changed",
                            detail = "Another live runtime currently owns this simulation worker identity."
                        })
                    };
                }

                var heartbeat = (await request.Content!.ReadFromJsonAsync<SimulationWorkerHeartbeatRequest>(cancellationToken))!;
                return Json(new SimulationWorkerHeartbeatResponse(
                    RealtimeServerHarness.WorkerId,
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

            if (path.EndsWith("/session-heartbeat", StringComparison.Ordinal))
            {
                lock (sync)
                {
                    sessionHeartbeats.Add(time.Elapsed);
                }

                if (Silent)
                {
                    throw new HttpRequestException("GameService cannot be reached.");
                }

                var sessions = (await request.Content!.ReadFromJsonAsync<SimulationSessionHeartbeatRequest>(cancellationToken))!;
                return Json(new SimulationSessionHeartbeatResponse(sessions.Sessions
                    .Select(entry => new SimulationSessionHeartbeatResult(
                        entry.SimulationSessionId,
                        new SimulationSessionLeaseResponse(
                            entry.SimulationSessionId,
                            Guid.NewGuid(),
                            RealtimeServerHarness.ShardId,
                            RealtimeServerHarness.WorkerId,
                            sessions.RuntimeId,
                            now.AddSeconds(150),
                            0,
                            0,
                            200,
                            false)
                        {
                            LeaseDurationMilliseconds = 150_000
                        },
                        null))
                    .ToArray()));
            }

            if (Silent)
            {
                throw new HttpRequestException("GameService cannot be reached.");
            }

            if (path.EndsWith("/offline", StringComparison.Ordinal))
            {
                var offline = (await request.Content!.ReadFromJsonAsync<SimulationWorkerOfflineRequest>(cancellationToken))!;
                return Json(new SimulationWorkerOfflineResponse(RealtimeServerHarness.WorkerId, offline.RuntimeId, now));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T value)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        }
    }

    // Records when, on the stepped clock, the worker asked the host to stop.
    private sealed class StopRecordingLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();

        public SteppedTimeProvider? Time { get; set; }

        public TaskCompletionSource StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan? StoppedAt { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            StoppedAt ??= Time?.Elapsed;
            StopRequested.TrySetResult();
            stopping.Cancel();
        }
    }
}
