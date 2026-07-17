using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    [Fact]
    public async Task DepositIntentMapsEveryTransferExpectationToDurableAuthority()
    {
        var handler = new CapturingProblemHandler();
        var service = CreateService(handler);
        var itemId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var intent = RealtimeCorpseInteractionIntent.CreateDepositPartialStack(
            Guid.NewGuid(),
            Guid.NewGuid(),
            12,
            itemId,
            7,
            3,
            destinationId,
            8,
            4,
            targetId,
            6);

        await service.MutateAsync(CreateSession(), intent, CancellationToken.None);

        Assert.NotNull(handler.Body);
        Assert.Equal("deposit_partial_stack", handler.Body.Value.GetProperty("operationKind").GetString());
        Assert.Equal(intent.ExpectedCorpseRevision, handler.Body.Value.GetProperty("expectedCorpseRevision").GetInt64());
        Assert.Equal(itemId, handler.Body.Value.GetProperty("itemInstanceId").GetGuid());
        Assert.Equal(intent.ExpectedItemRevision, handler.Body.Value.GetProperty("expectedItemRevision").GetInt64());
        Assert.Equal(3, handler.Body.Value.GetProperty("quantity").GetInt32());
        Assert.Equal(destinationId, handler.Body.Value.GetProperty("destinationContainerId").GetGuid());
        Assert.Equal(
            intent.ExpectedDestinationContainerRevision,
            handler.Body.Value.GetProperty("expectedDestinationContainerRevision").GetInt64());
        Assert.Equal(intent.DestinationSlotIndex, handler.Body.Value.GetProperty("destinationSlotIndex").GetInt32());
        Assert.Equal(targetId, handler.Body.Value.GetProperty("targetItemInstanceId").GetGuid());
        Assert.Equal(
            intent.ExpectedTargetItemRevision,
            handler.Body.Value.GetProperty("expectedTargetItemRevision").GetInt64());
    }

    [Fact]
    public async Task InternalMoveIntentMapsEveryTransferExpectationToDurableAuthority()
    {
        var handler = new CapturingProblemHandler();
        var service = CreateService(handler);
        var itemId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var intent = RealtimeCorpseInteractionIntent.CreateMovePartialStack(
            Guid.NewGuid(),
            Guid.NewGuid(),
            14,
            itemId,
            9,
            2,
            destinationId,
            11,
            5,
            targetId,
            8);

        var result = await service.MutateAsync(
            CreateSession(),
            intent,
            CancellationToken.None);

        Assert.NotNull(handler.Body);
        Assert.Equal(
            "move_partial_stack",
            handler.Body.Value.GetProperty("operationKind").GetString());
        Assert.Equal(itemId, handler.Body.Value.GetProperty("itemInstanceId").GetGuid());
        Assert.Equal(2, handler.Body.Value.GetProperty("quantity").GetInt32());
        Assert.Equal(
            destinationId,
            handler.Body.Value.GetProperty("destinationContainerId").GetGuid());
        Assert.Equal(5, handler.Body.Value.GetProperty("destinationSlotIndex").GetInt32());
        Assert.Equal(targetId, handler.Body.Value.GetProperty("targetItemInstanceId").GetGuid());
        Assert.False(result.RequiresInventoryRefresh);
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

    private sealed class CapturingProblemHandler : HttpMessageHandler
    {
        public JsonElement? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadFromJsonAsync<JsonElement>(
                cancellationToken: cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new
                {
                    status = 409,
                    code = "item_state_conflict",
                    detail = "Captured request."
                })
            };
        }
    }
}
