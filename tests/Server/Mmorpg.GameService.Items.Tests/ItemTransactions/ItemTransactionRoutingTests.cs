using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Testing;
using Npgsql;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// Pins the persisted operation kind of every item command. An account actor with a
// derived (version 5) operation id is rejected before any database access, so the
// complete routing table runs without PostgreSQL.
public sealed class ItemTransactionRoutingTests
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
        var service = ActivatorUtilities.CreateInstance<ItemTransactionService>(services);

        var result = await Routes[operationKind](
            service,
            ItemTransactionActor.ForAccount(Guid.NewGuid()));

        Assert.False(result.Succeeded);
        Assert.Equal(DerivedOperationId, result.OperationId);
        Assert.Equal(operationKind, result.OperationKind);
        Assert.Equal(ItemTransactionErrorCodes.ItemOperationIdInvalid, result.Error!.Code);
    }

    [Fact]
    public void RoutingTableCoversEveryPublicOverloadAndKnownMetricKind()
    {
        var overloads = typeof(ItemTransactionService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => string.Equals(method.Name, "ExecuteAsync", StringComparison.Ordinal))
            .ToArray();
        var requestTypes = overloads
            .Select(method =>
            {
                var parameters = method.GetParameters();
                Assert.Equal(2, parameters.Length);
                Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
                Assert.Equal(typeof(Task<ItemTransactionResult>), method.ReturnType);
                var requestType = parameters[0].ParameterType;
                Assert.True(requestType.IsGenericType);
                Assert.Equal(typeof(ItemTransactionRequest<>), requestType.GetGenericTypeDefinition());
                return requestType.GetGenericArguments()[0];
            })
            .ToArray();

        Assert.Equal(33, overloads.Length);
        Assert.Equal(33, requestTypes.Distinct().Count());
        Assert.Equal(33, Routes.Count);

        var knownKinds = (IReadOnlySet<string>)typeof(ItemOperationsMetrics)
            .GetField("KnownOperationKinds", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        Assert.Equal(
            knownKinds.Order(StringComparer.Ordinal),
            Routes.Keys.Order(StringComparer.Ordinal));
    }

    private static Task<ItemTransactionResult> Run<TCommand>(
        ItemTransactionService service,
        ItemTransactionActor actor,
        TCommand command,
        Func<ItemTransactionService, ItemTransactionRequest<TCommand>, Task<ItemTransactionResult>> execute)
        where TCommand : notnull
    {
        return execute(service, new ItemTransactionRequest<TCommand>(DerivedOperationId, actor, command));
    }

    private static readonly IReadOnlyDictionary<
        string,
        Func<ItemTransactionService, ItemTransactionActor, Task<ItemTransactionResult>>> Routes =
        new Dictionary<string, Func<ItemTransactionService, ItemTransactionActor, Task<ItemTransactionResult>>>(
            StringComparer.Ordinal)
        {
            [ItemOperationKinds.Grant] = (service, actor) => Run(
                service,
                actor,
                new GrantItemCommand(Id, 1, "fixture.ore", 1, Id, 0),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.Relocate] = (service, actor) => Run(
                service,
                actor,
                new RelocateItemCommand(Id, 1, Id, 1, Id, 0),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.Equip] = (service, actor) => Run(
                service,
                actor,
                new EquipItemCommand(Id, 1, Id, 1, "ring_1"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.Unequip] = (service, actor) => Run(
                service,
                actor,
                new UnequipItemCommand(Id, 1, Id, 1, Id, 0),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.SplitStack] = (service, actor) => Run(
                service,
                actor,
                new SplitItemStackCommand(Id, 1, Id, 1, 1, Id, 0),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.MergeStacks] = (service, actor) => Run(
                service,
                actor,
                new MergeItemStacksCommand(Id, 1, Id, 1, Id, 1),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.SwapContainerItems] = (service, actor) => Run(
                service,
                actor,
                new SwapContainerItemsCommand(Id, 1, Id, 1, Id, 1),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.ConsumeQuantity] = (service, actor) => Run(
                service,
                actor,
                new ConsumeItemQuantityCommand(Id, 1, Id, 1, 1),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.ConsumeEquippedAmmunition] = (service, actor) => Run(
                service,
                actor,
                new ConsumeEquippedAmmunitionCommand(Id, 1, Id, 1, 1),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.Destroy] = (service, actor) => Run(
                service,
                actor,
                new DestroyItemCommand(Id, 1, Id, 1, "routing"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.SwapBagAggregates] = (service, actor) => Run(
                service,
                actor,
                new SwapBagAggregatesCommand(Id, 1, Id, 1, 1, Id, 1, Id, 1, 1),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.AddRecoveryDelivery] = (service, actor) => Run(
                service,
                actor,
                new AddRecoveryDeliveryCommand(Id, 1, "routing", "routing", null, null, []),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.ClaimRecoveryDelivery] = (service, actor) => Run(
                service,
                actor,
                new ClaimRecoveryDeliveryCommand(Id, 1, Id, 1, Id, []),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.ChangeSecureContainerTier] = (service, actor) => Run(
                service,
                actor,
                new ChangeSecureContainerTierCommand(Id, 1, "secure_container.base", []),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.ApplyItemPolicy] = (service, actor) => Run(
                service,
                actor,
                new ApplyItemPolicyCommand(Id, 1, Id, 1, "insured", "routing", "routing"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.RemoveInsurancePolicy] = (service, actor) => Run(
                service,
                actor,
                new RemoveInsurancePolicyCommand(Id, 1, Id, 1),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.AbandonQuestItems] = (service, actor) => Run(
                service,
                actor,
                new AbandonQuestItemsCommand(Id, 1, "routing"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.ProcessPlayerDeath] = (service, actor) => Run(
                service,
                actor,
                new ProcessPlayerDeathCommand(Id, Id, 1, "shard", 0, 0, 0, 0, 0, 0, 1, "corpse"),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.CreatePersistentMobCorpse] = (service, actor) => Run(
                service,
                actor,
                new CreatePersistentMobCorpseCommand(
                    Id, "worker", "runtime", "shard", "mob", "Mob", 0, 0, 0, 0, 0, 0, 1, "corpse", 60, 30, []),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.GrantMobLoot] = (service, actor) => Run(
                service,
                actor,
                new GrantMobLootCommand(Id, 1, Id, Id, "mob", "fixture.ore", 1, Id, 1, 0),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.ExpireCorpse] = (service, actor) => Run(
                service,
                actor,
                new ExpireCorpseCommand(Id),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.ExpireRecoveryDelivery] = (service, actor) => Run(
                service,
                actor,
                new ExpireRecoveryDeliveryCommand(Id),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.LootCorpseItem] = (service, actor) => Run(
                service,
                actor,
                new LootCorpseItemCommand(Id, Id, Id, 1, Id, 1, 0, null, null),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.LootCorpsePartialStack] = (service, actor) => Run(
                service,
                actor,
                new LootCorpsePartialStackCommand(Id, Id, Id, 1, 1, Id, 1, 0, null, null),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.LootCorpseItemToEquipment] = (service, actor) => Run(
                service,
                actor,
                new LootCorpseItemToEquipmentCommand(Id, Id, Id, 1, "main_hand", null, null),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.DepositCorpseItem] = (service, actor) => Run(
                service,
                actor,
                new DepositCorpseItemCommand(Id, Id, Id, 1, Id, 1, 0, null, null),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.DepositCorpsePartialStack] = (service, actor) => Run(
                service,
                actor,
                new DepositCorpsePartialStackCommand(Id, Id, Id, 1, 1, Id, 1, 0, null, null),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.MoveCorpseItem] = (service, actor) => Run(
                service,
                actor,
                new MoveCorpseItemCommand(Id, Id, Id, 1, Id, 1, 0, null, null),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.MoveCorpsePartialStack] = (service, actor) => Run(
                service,
                actor,
                new MoveCorpsePartialStackCommand(Id, Id, Id, 1, 1, Id, 1, 0, null, null),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.SwapCorpseBag] = (service, actor) => Run(
                service,
                actor,
                new SwapCorpseBagCommand(Id, Id, Id, 1, Id, 1, Id, 1, Id, 1),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.VendorPurchase] = (service, actor) => Run(
                service,
                actor,
                new PurchaseFromVendorCommand(Id, 1, "vendor.fixture_store", "fixture.arrow", 1, 1),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.VendorSale] = (service, actor) => Run(
                service,
                actor,
                new SellToVendorCommand(Id, 1, "vendor.fixture_store", Id, 1, 1, 0),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None)),
            [ItemOperationKinds.GatherResource] = (service, actor) => Run(
                service,
                actor,
                new GatherResourceCommand(Id, "resource.fixture", "node:0000", "pickaxe", "fixture.ore", 1),
                (target, request) => target.ExecuteAsync(request, CancellationToken.None))
        };
}
