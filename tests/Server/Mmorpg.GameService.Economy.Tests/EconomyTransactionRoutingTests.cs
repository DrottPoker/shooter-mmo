using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Testing;
using Npgsql;

namespace Mmorpg.GameService.Economy.Tests;

// Pins the persisted operation kind of every vendor and gathering command, as
// ItemTransactionRoutingTests does for the item commands. An account actor with a derived
// (version 5) operation id is rejected before any database access, so the complete routing table
// runs without PostgreSQL.
public sealed class EconomyTransactionRoutingTests
{
    private static readonly Guid DerivedOperationId = Guid.Parse("8f14e45f-ceea-567a-9a6f-b3f0a2c4d1e8");
    private static readonly Guid Id = Guid.Parse("0f0e0d0c-0b0a-4908-8706-050403020100");

    public static TheoryData<string> OperationKinds()
    {
        return new TheoryData<string>(Routes.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(OperationKinds))]
    public async Task EveryCommandRoutesToItsPersistedOperationKind(string operationKind)
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        await using var services = GameServiceTestComposition.BuildServiceProvider(dataSource);
        await using var scope = services.CreateAsyncScope();
        var transactions = scope.ServiceProvider.GetRequiredService<IItemTransactions>();

        var result = await Routes[operationKind](
            transactions,
            ItemTransactionActor.ForAccount(Guid.NewGuid()));

        Assert.False(result.Succeeded);
        Assert.Equal(DerivedOperationId, result.OperationId);
        Assert.Equal(operationKind, result.OperationKind);
        Assert.Equal(ItemTransactionErrorCodes.ItemOperationIdInvalid, result.Error!.Code);
    }

    [Fact]
    public async Task RoutingTableCoversEveryEconomyRegistration()
    {
        await using var composition = new OfflineItemComposition();
        var registrations = composition.RegistrationsOf(GameServiceAssemblies.Economy);

        Assert.Equal(3, registrations.Count);
        Assert.Equal(3, registrations.Select(registration => registration.CommandType).Distinct().Count());
        Assert.Equal(3, Routes.Count);
        Assert.Equal(
            registrations.Select(registration => registration.OperationKind).Order(StringComparer.Ordinal),
            Routes.Keys.Order(StringComparer.Ordinal));
    }

    private static Task<ItemTransactionResult> Run<TCommand>(
        IItemTransactions service,
        ItemTransactionActor actor,
        TCommand command)
        where TCommand : notnull
    {
        return service.ExecuteAsync(
            new ItemTransactionRequest<TCommand>(DerivedOperationId, actor, command),
            CancellationToken.None);
    }

    private static readonly IReadOnlyDictionary<
        string,
        Func<IItemTransactions, ItemTransactionActor, Task<ItemTransactionResult>>> Routes =
        new Dictionary<string, Func<IItemTransactions, ItemTransactionActor, Task<ItemTransactionResult>>>(
            StringComparer.Ordinal)
        {
            [EconomyOperationKinds.VendorPurchase] = (service, actor) => Run(
                service,
                actor,
                new PurchaseFromVendorCommand(Id, 1, "vendor.fixture_store", "fixture.arrow", 1, 1)),
            [EconomyOperationKinds.VendorSale] = (service, actor) => Run(
                service,
                actor,
                new SellToVendorCommand(Id, 1, "vendor.fixture_store", Id, 1, 1, 0)),
            [EconomyOperationKinds.GatherResource] = (service, actor) => Run(
                service,
                actor,
                new GatherResourceCommand(Id, "resource.fixture", "node:0000", "pickaxe", "fixture.ore", 1))
        };
}
