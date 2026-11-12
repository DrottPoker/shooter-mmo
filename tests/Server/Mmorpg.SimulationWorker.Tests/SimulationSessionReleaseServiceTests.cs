using System.Net;
using System.Net.Http.Json;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class SimulationSessionReleaseServiceTests
{
    [Fact]
    public async Task StaleReconnectGenerationCannotRemoveCurrentCarryState()
    {
        using var httpClient = new HttpClient(new ConflictReleaseHandler())
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var sessionStore = new ActiveSimulationSessionStore();
        var itemStateStore = new CharacterItemStateStore();
        var service = new SimulationSessionReleaseService(
            new GameServiceClient(httpClient),
            sessionStore,
            CharacterStateTestData.SessionStores(itemStateStore));
        var characterId = Guid.NewGuid();
        var simulationSessionId = Guid.NewGuid();
        var stale = CreateSession(
            characterId,
            simulationSessionId,
            "stale-session-token",
            PlayerCarryState.Default);
        var currentCarryState = new PlayerCarryState(1, 210, 200);
        var current = CreateSession(
            characterId,
            simulationSessionId,
            "current-session-token",
            currentCarryState);
        Assert.Equal(ActiveSimulationSessionRegistration.Joined, sessionStore.Register(stale));
        itemStateStore.Register(
            characterId,
            simulationSessionId,
            CharacterStateTestData.ItemState(stale.CarryState),
            out _);
        Assert.Equal(
            ActiveSimulationSessionRegistration.Reconnected,
            sessionStore.Register(current));
        itemStateStore.Register(
            characterId,
            simulationSessionId,
            CharacterStateTestData.ItemState(currentCarryState),
            out _);

        var staleRelease = await service.ReleaseAsync(stale, CancellationToken.None);

        Assert.True(staleRelease.Succeeded);
        Assert.True(sessionStore.TryGet(characterId, out var preservedSession));
        Assert.Equal(current.SimulationSessionToken, preservedSession!.SimulationSessionToken);
        Assert.True(itemStateStore.TryGet(
            characterId,
            simulationSessionId,
            out var preservedCarryState));
        Assert.Same(currentCarryState, preservedCarryState);

        var currentRelease = await service.ReleaseAsync(current, CancellationToken.None);

        Assert.True(currentRelease.Succeeded);
        Assert.False(sessionStore.TryGet(characterId, out _));
        Assert.False(itemStateStore.TryGet(characterId, simulationSessionId, out _));
    }

    // A join whose peer left before it bound releases its session, and the quest log that the
    // join read goes with it instead of waiting for the character to join again.
    [Fact]
    public async Task ADetachedReleaseRemovesTheQuestLogOfTheSession()
    {
        using var httpClient = new HttpClient(new ConflictReleaseHandler())
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var sessionStore = new ActiveSimulationSessionStore();
        var questLogs = new CharacterQuestLogStore();
        var service = new SimulationSessionReleaseService(
            new GameServiceClient(httpClient),
            sessionStore,
            CharacterSessionStores.Create(new CharacterItemStateStore(), new CharacterLookStore(), questLogs),
            []);
        var session = CreateSession(Guid.NewGuid(), Guid.NewGuid(), "session-token", PlayerCarryState.Default);
        sessionStore.Register(session);
        questLogs.Register(
            session.CharacterId,
            session.SimulationSessionId,
            CharacterQuestLog.From(new SimulationQuestLogResponse(session.CharacterId, "catalog-revision", 3, [], [])));

        await service.ReleaseDetachedAsync(session, CancellationToken.None);

        Assert.False(questLogs.TryGet(session.CharacterId, session.SimulationSessionId, out _));
    }

    [Fact]
    public void ReconnectWithANewTokenSupersedesTheOlderConnectionOnly()
    {
        var sessionStore = new ActiveSimulationSessionStore();
        var characterId = Guid.NewGuid();
        var simulationSessionId = Guid.NewGuid();
        var original = CreateSession(
            characterId,
            simulationSessionId,
            "original-token",
            PlayerCarryState.Default);
        var reconnected = CreateSession(
            characterId,
            simulationSessionId,
            "reconnected-token",
            PlayerCarryState.Default);
        var otherSession = CreateSession(
            characterId,
            Guid.NewGuid(),
            "other-token",
            PlayerCarryState.Default);

        sessionStore.Register(original);
        Assert.False(sessionStore.IsSupersededByReconnect(original));
        sessionStore.Register(reconnected);

        Assert.True(sessionStore.IsSupersededByReconnect(original));
        Assert.False(sessionStore.IsSupersededByReconnect(reconnected));
        Assert.False(sessionStore.IsSupersededByReconnect(otherSession));
    }

    // The loop captured the character's final state; the release carries it exactly, and the
    // service forgets it once GameService answered.
    [Fact]
    public async Task AReleaseCarriesTheCapturedFinalStateAndForgetsItOnceAnswered()
    {
        var handler = new RecordingReleaseHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://game-service.test") };
        var sessionStore = new ActiveSimulationSessionStore();
        var service = new SimulationSessionReleaseService(
            new GameServiceClient(httpClient),
            sessionStore,
            CharacterStateTestData.SessionStores(new CharacterItemStateStore()));
        var session = CreateSession(Guid.NewGuid(), Guid.NewGuid(), "session-token", PlayerCarryState.Default);
        sessionStore.Register(session);
        var state = new CharacterStateSnapshot(0.1f, BitConverter.Int32BitsToSingle(0x3F800001), -7.25f, 359.5f, 42, 0);
        service.FinalStates.Capture(session.CharacterId, session.SimulationSessionId, new CharacterFinalState(9, state));

        var result = await service.ReleaseAsync(session, CancellationToken.None);

        Assert.True(result.Succeeded);
        var sent = Assert.Single(handler.Releases).FinalState;
        Assert.NotNull(sent);
        Assert.Equal(
            (9L, 0.1f, BitConverter.Int32BitsToSingle(0x3F800001), -7.25f, 359.5f, 42, 0),
            (sent.Sequence, sent.PositionX, sent.PositionY, sent.PositionZ, sent.YawDegrees, sent.Health, sent.Mana));
        Assert.Equal(0, service.FinalStates.Count);
    }

    // A detached release sends the final state with every attempt and forgets it after the last.
    [Fact]
    public async Task ADetachedReleaseSendsTheFinalStateWithEveryAttempt()
    {
        var handler = new RecordingReleaseHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://game-service.test") };
        var sessionStore = new ActiveSimulationSessionStore();
        var service = new SimulationSessionReleaseService(
            new GameServiceClient(httpClient),
            sessionStore,
            CharacterStateTestData.SessionStores(new CharacterItemStateStore()),
            [TimeSpan.Zero]);
        var session = CreateSession(Guid.NewGuid(), Guid.NewGuid(), "session-token", PlayerCarryState.Default);
        sessionStore.Register(session);
        service.FinalStates.Capture(
            session.CharacterId,
            session.SimulationSessionId,
            new CharacterFinalState(3, new CharacterStateSnapshot(1f, 2f, 3f, 4f, 5, 6)));

        var result = await service.ReleaseDetachedAsync(session, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([3L, 3L], handler.Releases.Select(release => release.FinalState!.Sequence));
        Assert.Equal(0, service.FinalStates.Count);
    }

    // A release that fails for good forgets the state it sent, but never a newer one that the
    // loop captured meanwhile, and a release without a state sends none.
    [Fact]
    public async Task AReleaseThatGivesUpForgetsOnlyTheStateItSent()
    {
        var handler = new RecordingReleaseHandler(HttpStatusCode.ServiceUnavailable);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://game-service.test") };
        var service = new SimulationSessionReleaseService(
            new GameServiceClient(httpClient),
            new ActiveSimulationSessionStore(),
            CharacterStateTestData.SessionStores(new CharacterItemStateStore()),
            []);
        var session = CreateSession(Guid.NewGuid(), Guid.NewGuid(), "session-token", PlayerCarryState.Default);
        var other = CreateSession(Guid.NewGuid(), Guid.NewGuid(), "other-token", PlayerCarryState.Default);
        var store = service.FinalStates;
        store.Capture(session.CharacterId, session.SimulationSessionId, new CharacterFinalState(1, default));

        var failed = await service.ReleaseAsync(session, CancellationToken.None);
        Assert.False(failed.Succeeded);
        Assert.True(store.TryGet(session.CharacterId, session.SimulationSessionId, out _));

        handler.BeforeAnswer = () => store.Capture(
            session.CharacterId,
            session.SimulationSessionId,
            new CharacterFinalState(2, default));
        var gaveUp = await service.ReleaseDetachedAsync(session, CancellationToken.None);
        Assert.False(gaveUp.Succeeded);
        Assert.True(store.TryGet(session.CharacterId, session.SimulationSessionId, out var kept));
        Assert.Equal(2, kept.Sequence);

        handler.BeforeAnswer = null;
        await service.ReleaseDetachedAsync(other, CancellationToken.None);
        Assert.Null(handler.Releases[^1].FinalState);
    }

    [Fact]
    public void TheFinalStateStoreKeepsTheNewerStateOfASession()
    {
        var store = new CharacterFinalStateStore();
        var characterId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        store.Capture(characterId, sessionId, new CharacterFinalState(5, new CharacterStateSnapshot(5f, 0f, 0f, 0f, 5, 5)));
        store.Capture(characterId, sessionId, new CharacterFinalState(4, new CharacterStateSnapshot(4f, 0f, 0f, 0f, 4, 4)));
        store.Remove(characterId, sessionId, 4);

        Assert.True(store.TryGet(characterId, sessionId, out var kept));
        Assert.Equal(5f, kept.State.PositionX);
        Assert.False(store.TryGet(characterId, Guid.NewGuid(), out _));
        store.Remove(characterId, sessionId, 5);
        Assert.Equal(0, store.Count);
    }

    private static ActiveSimulationSession CreateSession(
        Guid characterId,
        Guid simulationSessionId,
        string simulationSessionToken,
        PlayerCarryState carryState)
    {
        return new ActiveSimulationSession(
            simulationSessionId,
            simulationSessionToken,
            Guid.NewGuid(),
            characterId,
            "Reconnect Hero",
            "local-shard-1",
            "development-world-1",
            "local-simulation-worker-1",
            "runtime-1",
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(1),
            carryState,
            true);
    }

    // Answers each release with the next status, the last one for every later release, and
    // records the request bodies.
    private sealed class RecordingReleaseHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int answered;

        public List<SimulationSessionReleaseRequest> Releases { get; } = [];

        public Action? BeforeAnswer { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Releases.Add((await request.Content!.ReadFromJsonAsync<SimulationSessionReleaseRequest>(cancellationToken))!);
            BeforeAnswer?.Invoke();
            var status = statuses[Math.Min(answered++, statuses.Length - 1)];
            if (status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(status)
                {
                    Content = JsonContent.Create(new { code = "database_unavailable", detail = "PostgreSQL is unavailable." })
                };
            }

            var simulationSessionId = Guid.Parse(request.RequestUri!.Segments[3].TrimEnd('/'));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SimulationSessionLeaseResponse(
                    simulationSessionId,
                    Guid.NewGuid(),
                    "local-shard-1",
                    "local-simulation-worker-1",
                    "runtime-1",
                    DateTime.UtcNow,
                    0,
                    0,
                    200,
                    true)
                {
                    FinalStateSaved = true
                })
            };
        }
    }

    private sealed class ConflictReleaseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new
                {
                    code = "simulation_session_changed",
                    detail = "The simulation session generation changed."
                })
            });
        }
    }
}
