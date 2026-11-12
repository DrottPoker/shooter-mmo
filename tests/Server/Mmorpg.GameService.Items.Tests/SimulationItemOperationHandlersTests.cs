using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Testing;
using Npgsql;

namespace Mmorpg.GameService.Items.Tests;

// Pins the kinds that the worker item route runs itself and the start failures of
// ISimulationItemOperationHandler, through which a module above Items adds an operation kind to
// the route: two handlers of one kind, a handler of a kind that the route runs itself, or a
// handler without a kind stop the start with a message that names both sides.
public sealed class SimulationItemOperationHandlersTests
{
    [Fact]
    public void TheRouteRunsTheKindsOfItsOwnCommands()
    {
        Assert.Equal(
            new[]
            {
                ItemOperationKinds.ApplyItemPolicy,
                ItemOperationKinds.ClaimRecoveryDelivery,
                ItemOperationKinds.Destroy,
                ItemOperationKinds.Equip,
                ItemOperationKinds.MergeStacks,
                ItemOperationKinds.Relocate,
                ItemOperationKinds.RemoveInsurancePolicy,
                ItemOperationKinds.SplitStack,
                ItemOperationKinds.SwapContainerItems,
                ItemOperationKinds.Unequip
            }.Order(StringComparer.Ordinal),
            SimulationItemMutationService.OwnKinds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TwoHandlersOfOneKindFailNamingBoth()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new SimulationItemOperationHandlers(
            [new FirstHandler("test_kind"), new SecondHandler("test_kind")]));

        Assert.Equal(
            "Two worker item operation handlers share the operation kind 'test_kind': FirstHandler and SecondHandler.",
            exception.Message);
    }

    [Fact]
    public void AHandlerOfAKindThatTheRouteRunsFailsNamingIt()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new SimulationItemOperationHandlers(
            [new FirstHandler(ItemOperationKinds.Relocate)]));

        Assert.Equal(
            "The worker item operation handler FirstHandler handles 'relocate', which Items' SimulationItemMutationService runs itself.",
            exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" test_kind")]
    public void AHandlerWithoutAPlainKindFails(string kind)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new SimulationItemOperationHandlers(
            [new FirstHandler(kind)]));

        Assert.Equal(
            "The worker item operation handler FirstHandler names no operation kind or one with surrounding white space.",
            exception.Message);
    }

    [Fact]
    public void TheModulesAddTheirKinds()
    {
        var handlers = new SimulationItemOperationHandlers([new FirstHandler("test_kind")]);

        Assert.Equal(["test_kind"], handlers.Kinds);
        Assert.IsType<FirstHandler>(handlers.Find("test_kind"));
        Assert.Null(handlers.Find(ItemOperationKinds.Relocate));
    }

    // The route builds its handlers when its endpoints are mapped, so a conflict stops the start
    // before the first request.
    [Fact]
    public async Task AConflictingHandlerStopsTheStart()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddGameServiceForTests(dataSource);
        builder.Services.AddSingleton<ISimulationItemOperationHandler>(new FirstHandler(ItemOperationKinds.Destroy));
        await using var application = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => application.MapSimulationItemEndpoints());

        Assert.Contains("FirstHandler handles 'destroy'", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FirstHandler(string kind) : ISimulationItemOperationHandler
    {
        public string OperationKind { get; } = kind;

        public Task<ServiceResult<SimulationItemOperationResponse>> ExecuteAsync(
            SimulationItemOperation operation,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The handler tests never run a handler.");
        }
    }

    private sealed class SecondHandler(string kind) : ISimulationItemOperationHandler
    {
        public string OperationKind { get; } = kind;

        public Task<ServiceResult<SimulationItemOperationResponse>> ExecuteAsync(
            SimulationItemOperation operation,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The handler tests never run a handler.");
        }
    }
}
