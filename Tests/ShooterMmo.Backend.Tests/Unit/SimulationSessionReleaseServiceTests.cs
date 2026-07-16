using System.Net;
using System.Net.Http.Json;
using ShooterMmo.GameSimulation;
using SimulationWorker.Auth;
using SimulationWorker.Items;
using SimulationWorker.Sessions;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class SimulationSessionReleaseServiceTests
{
    [Fact]
    public async Task StaleReconnectGenerationCannotRemoveCurrentCarryState()
    {
        using var httpClient = new HttpClient(new ConflictReleaseHandler())
        {
            BaseAddress = new Uri("http://auth-service.test")
        };
        var sessionStore = new ActiveSimulationSessionStore();
        var carryStateStore = new CarryStateStore();
        var service = new SimulationSessionReleaseService(
            new AuthServiceClient(httpClient),
            sessionStore,
            carryStateStore);
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
        carryStateStore.Register(
            characterId,
            simulationSessionId,
            stale.CarryState,
            out _);
        Assert.Equal(
            ActiveSimulationSessionRegistration.Reconnected,
            sessionStore.Register(current));
        carryStateStore.Register(
            characterId,
            simulationSessionId,
            currentCarryState,
            out _);

        var staleRelease = await service.ReleaseAsync(stale, CancellationToken.None);

        Assert.True(staleRelease.Succeeded);
        Assert.True(sessionStore.TryGet(characterId, out var preservedSession));
        Assert.Equal(current.SimulationSessionToken, preservedSession!.SimulationSessionToken);
        Assert.True(carryStateStore.TryGet(
            characterId,
            simulationSessionId,
            out var preservedCarryState));
        Assert.Same(currentCarryState, preservedCarryState);

        var currentRelease = await service.ReleaseAsync(current, CancellationToken.None);

        Assert.True(currentRelease.Succeeded);
        Assert.False(sessionStore.TryGet(characterId, out _));
        Assert.False(carryStateStore.TryGet(characterId, simulationSessionId, out _));
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
            "local-world-1",
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
