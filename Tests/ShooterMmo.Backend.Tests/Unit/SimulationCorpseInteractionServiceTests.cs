using System.Net;
using System.Net.Http.Json;
using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using SimulationWorker.Auth;
using SimulationWorker.Corpses;
using SimulationWorker.Items;
using SimulationWorker.Sessions;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class SimulationCorpseInteractionServiceTests
{
    [Theory]
    [InlineData("corpse_expired", true, false)]
    [InlineData("corpse_invalidated", true, false)]
    [InlineData("corpse_not_found", true, false)]
    [InlineData("wrong_simulation_worker", true, false)]
    [InlineData("worker_runtime_changed", false, true)]
    [InlineData("simulation_session_invalid", false, true)]
    public async Task StableAuthorityErrorsCloseOrDisconnectAsRequired(
        string code,
        bool shouldClose,
        bool shouldDisconnect)
    {
        var service = CreateService(new ProblemHandler(code));
        var intent = RealtimeCorpseInteractionIntent.CreateOpen(
            Guid.NewGuid(),
            Guid.NewGuid());

        var result = await service.OpenAsync(
            CreateSession(),
            intent,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(shouldClose, result.ShouldCloseView);
        Assert.Equal(shouldDisconnect, result.ShouldDisconnect);
    }

    [Theory]
    [InlineData("item_quantity_changed", true, true)]
    [InlineData("item_already_looted", true, true)]
    [InlineData("bag_state_changed", true, true)]
    [InlineData("carry_weight_limit_exceeded", false, true)]
    public async Task MutationConflictsExposeDeterministicRefreshScopes(
        string code,
        bool corpseRefresh,
        bool inventoryRefresh)
    {
        var service = CreateService(new ProblemHandler(code));
        var intent = RealtimeCorpseInteractionIntent.CreateLootItem(
            Guid.NewGuid(),
            Guid.NewGuid(),
            4,
            Guid.NewGuid(),
            2,
            Guid.NewGuid(),
            3,
            0,
            Guid.Empty,
            0);

        var result = await service.MutateAsync(
            CreateSession(),
            intent,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(corpseRefresh, result.RequiresCorpseRefresh);
        Assert.Equal(inventoryRefresh, result.RequiresInventoryRefresh);
        Assert.False(result.ShouldDisconnect);
    }

    [Fact]
    public async Task SyntheticSessionIsRejectedWithoutCallingDurableAuthority()
    {
        var handler = new CountingHandler();
        var session = CreateSession() with { IsSyntheticBot = true };
        var service = CreateService(handler);

        var result = await service.OpenAsync(
            session,
            RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("corpse_interaction_unavailable", result.Error!.Code);
        Assert.Equal(0, handler.RequestCount);
    }

    private static SimulationCorpseInteractionService CreateService(HttpMessageHandler handler)
    {
        return new SimulationCorpseInteractionService(new AuthServiceClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://auth-service.test")
        }));
    }

    private static ActiveSimulationSession CreateSession()
    {
        return new ActiveSimulationSession(
            Guid.NewGuid(),
            "simulation-session-token",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Corpse Hero",
            "local-shard-1",
            "local-world-1",
            "local-simulation-worker-1",
            "runtime-1",
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(1),
            new PlayerCarryState(4, 50, 200),
            false);
    }

    private sealed class ProblemHandler(string code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new
                {
                    status = 409,
                    code,
                    detail = "Stable corpse interaction rejection."
                })
            });
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
