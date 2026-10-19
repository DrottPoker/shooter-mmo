using System.Net;
using System.Net.Http.Json;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
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
            itemStateStore);
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
