using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Tests.ItemTransactions;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Tests;

// Vendor purchases and sales and gathering through the worker boundary, with the currency
// ledger and carried placement rules they rely on.
public sealed class VendorAndGatheringIntegrationTests
{
    private const string Vendor = "vendor.fixture_store";
    private const string Arrow = "fixture.arrow";
    private const string Potion = "fixture.potion";
    private const string Ore = "fixture.ore";
    private const string Ring = "fixture.ring";
    private const string Pickaxe = "fixture.pickaxe";
    private const string Pack = "fixture.pack";

    [PostgresIntegrationFact]
    public async Task PurchaseDebitsTheWalletAndTopsUpAnExistingStackFirst()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("vendor_buyer");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        await CreditAsync(context, characterId, 500);
        var arrowsId = await world.GrantAsync(characterId, Arrow, 3, state.PermanentInventoryContainerId, 0);
        var session = await world.JoinAsync(player);

        var purchase = await ExecuteAsync(
            context,
            session,
            Request(session, ItemOperationKinds.VendorPurchase, await RevisionAsync(world, characterId))
                with
            { DefinitionId = Arrow, Quantity = 5, ExpectedPrice = 5 });

        Assert.True(purchase.Succeeded, purchase.Error?.Message);
        Assert.Equal(8, (await world.LoadItemAsync(arrowsId)).Quantity);
        Assert.Equal(495, await CurrencyAsync(context, characterId));
        await AssertLedgerMatchesBalanceAsync(context, characterId);
        var ledger = await LedgerAsync(context, characterId);
        Assert.Equal(
            [(CurrencySourceKinds.Development, 500L), (CurrencySourceKinds.VendorPurchase, -5L)],
            ledger.Select(row => (row.SourceKind, row.Delta)));
        Assert.Equal(Vendor, ledger[1].SourceId);
        Assert.Equal(purchase.OperationId, ledger[1].ItemOperationId);
        var snapshot = await SnapshotAsync(context, player);
        Assert.Equal(495, snapshot.Currency);
    }

    [PostgresIntegrationFact]
    public async Task PurchaseRejectsAStalePriceMissingFundsUnknownOffersAndUnknownVendors()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("vendor_rejections");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);
        var revision = await RevisionAsync(world, characterId);

        var noFunds = await ExecuteAsync(
            context,
            session,
            Request(session, ItemOperationKinds.VendorPurchase, revision)
                with
            { DefinitionId = Potion, Quantity = 1, ExpectedPrice = 25 });
        AssertRejected(noFunds, ItemTransactionErrorCodes.CurrencyInsufficient);

        await CreditAsync(context, characterId, 100);
        revision = await RevisionAsync(world, characterId);
        var stalePrice = await ExecuteAsync(
            context,
            session,
            Request(session, ItemOperationKinds.VendorPurchase, revision)
                with
            { DefinitionId = Potion, Quantity = 1, ExpectedPrice = 20 });
        AssertRejected(stalePrice, ItemTransactionErrorCodes.VendorPriceChanged);

        var notOffered = await ExecuteAsync(
            context,
            session,
            Request(session, ItemOperationKinds.VendorPurchase, revision)
                with
            { DefinitionId = Ore, Quantity = 1, ExpectedPrice = 10 });
        AssertRejected(notOffered, ItemTransactionErrorCodes.VendorOfferUnavailable);

        var unknownVendor = await ExecuteAsync(
            context,
            session,
            Request(session, ItemOperationKinds.VendorPurchase, revision, "vendor.missing")
                with
            { DefinitionId = Potion, Quantity = 1, ExpectedPrice = 25 });
        AssertRejected(unknownVendor, ItemTransactionErrorCodes.VendorUnavailable);

        Assert.Equal(100, await CurrencyAsync(context, characterId));
        await AssertLedgerMatchesBalanceAsync(context, characterId);
    }

    [PostgresIntegrationFact]
    public async Task VendorCommandsRequireTheValidatedVendorProfile()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("vendor_access");
        var characterId = player.Character.Id;
        await CreditAsync(context, characterId, 100);
        var session = await world.JoinAsync(player);
        var operationId = Guid.NewGuid();

        var result = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<PurchaseFromVendorCommand>(
                operationId,
                ItemScenarioWorld.SimulationActor(
                    session,
                    new ItemTransactionLiveAccess(false, false, false, false, "vendor.other")),
                new PurchaseFromVendorCommand(
                    characterId,
                    await RevisionAsync(world, characterId),
                    Vendor,
                    Potion,
                    1,
                    25)),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.VendorAccessRequired, result.Error!.Code);
        Assert.Equal(100, await CurrencyAsync(context, characterId));
    }

    [PostgresIntegrationFact]
    public async Task PurchaseUsesMatchingBagSlotsAndRejectsWhenCarriedStorageIsFull()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("vendor_full");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        await CreditAsync(context, characterId, 1000);
        var packId = await world.GrantAsync(characterId, Pack, 1, state.PermanentInventoryContainerId, 0);
        await world.EquipAsync(characterId, packId, ItemEquipmentSlotIds.Bag);
        var session = await world.JoinAsync(player);

        var potion = await ExecuteAsync(
            context,
            session,
            Request(session, ItemOperationKinds.VendorPurchase, await RevisionAsync(world, characterId))
                with
            { DefinitionId = Potion, Quantity = 1, ExpectedPrice = 25 });
        Assert.True(potion.Succeeded, potion.Error?.Message);
        var bagContents = (await world.LoadItemAsync(packId)).BagContentsContainerId!.Value;
        Assert.NotEqual(Guid.Empty, await world.FindItemAsync(bagContents, 4));

        for (var slot = 0; slot < 20; slot++)
        {
            await world.GrantAsync(characterId, Ring, 1, state.PermanentInventoryContainerId, slot);
        }

        for (var slot = 0; slot < 4; slot++)
        {
            await world.GrantAsync(characterId, Ring, 1, bagContents, slot);
        }

        var full = await ExecuteAsync(
            context,
            session,
            Request(session, ItemOperationKinds.VendorPurchase, await RevisionAsync(world, characterId))
                with
            { DefinitionId = Pickaxe, Quantity = 1, ExpectedPrice = 50 });

        AssertRejected(full, ItemTransactionErrorCodes.InventoryFull);
        Assert.Equal(975, await CurrencyAsync(context, characterId));
        await AssertLedgerMatchesBalanceAsync(context, characterId);
    }

    [PostgresIntegrationFact]
    public async Task SaleCreditsPartialAndWholeStacksAndRefusesProtectedEquippedAndBankedItems()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("vendor_seller");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var oreId = await world.GrantAsync(characterId, Ore, 5, state.PermanentInventoryContainerId, 0);
        var protectedRingId = await world.GrantAsync(characterId, Ring, 1, state.PermanentInventoryContainerId, 1);
        await world.ApplyPolicyAsync(characterId, protectedRingId, ItemPolicyIds.ProtectedOnDeath);
        var equippedRingId = await world.GrantAsync(characterId, Ring, 1, state.PermanentInventoryContainerId, 2);
        await world.EquipAsync(characterId, equippedRingId, "ring_1");
        var bankedOreId = await world.GrantAsync(characterId, Ore, 2, state.BankContainerId, 0);
        var session = await world.JoinAsync(player);

        var partial = await SellAsync(context, world, session, characterId, oreId, 3, 12);
        Assert.True(partial.Succeeded, partial.Error?.Message);
        Assert.Equal(2, (await world.LoadItemAsync(oreId)).Quantity);
        var whole = await SellAsync(context, world, session, characterId, oreId, 2, 8);
        Assert.True(whole.Succeeded, whole.Error?.Message);
        Assert.Equal(20, await CurrencyAsync(context, characterId));
        Assert.Equal(
            "vendor_sale",
            await context.ExecuteScalarStringAsync(
                $"select reason from item_destructions where item_instance_id = '{oreId}';"));

        AssertRejected(
            await SellAsync(context, world, session, characterId, protectedRingId, 1, 16),
            ItemTransactionErrorCodes.ItemPolicyRestricted);
        AssertRejected(
            await SellAsync(context, world, session, characterId, equippedRingId, 1, 16),
            ItemTransactionErrorCodes.VendorWillNotBuy);
        AssertRejected(
            await SellAsync(context, world, session, characterId, bankedOreId, 1, 4),
            ItemTransactionErrorCodes.BankAccessRequired);
        Assert.Equal(20, await CurrencyAsync(context, characterId));
        await AssertLedgerMatchesBalanceAsync(context, characterId);
    }

    [PostgresIntegrationFact]
    public async Task ConcurrentSalesOfOneStackSellItOnce()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("vendor_race");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var oreId = await world.GrantAsync(characterId, Ore, 10, state.PermanentInventoryContainerId, 0);
        var session = await world.JoinAsync(player);
        var revision = await RevisionAsync(world, characterId);
        var item = await world.LoadItemAsync(oreId);

        var sales = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ExecuteAsync(
            context,
            session,
            Request(session, ItemOperationKinds.VendorSale, revision)
                with
            {
                ItemInstanceId = oreId,
                ExpectedItemRevision = item.Revision,
                Quantity = 10,
                ExpectedPrice = 40
            })));

        Assert.Single(sales, sale => sale.Succeeded);
        Assert.Equal(40, await CurrencyAsync(context, characterId));
        await AssertLedgerMatchesBalanceAsync(context, characterId);
    }

    [PostgresIntegrationFact]
    public async Task GatheringNeedsTheToolKindInTheToolSlotAndRespectsTheWeightCap()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("gatherer");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var pickaxeId = await world.GrantAsync(characterId, Pickaxe, 1, state.PermanentInventoryContainerId, 0);
        var session = await world.JoinAsync(player);

        AssertRejected(
            await GatherAsync(context, session, 1),
            ItemTransactionErrorCodes.GatheringToolRequired);

        await world.EquipAsync(characterId, pickaxeId, ItemEquipmentSlotIds.Tool);
        var first = await GatherAsync(context, session, 1);
        Assert.True(first.Succeeded, first.Error?.Message);
        var second = await GatherAsync(context, session, 1);
        Assert.True(second.Succeeded, second.Error?.Message);
        // The equipped pickaxe left slot 0 empty, and both yields share one stack there.
        var oreId = await world.FindItemAsync(state.PermanentInventoryContainerId, 0);
        Assert.Equal(2, (await world.LoadItemAsync(oreId)).Quantity);

        // Base capacity 200 allows 280 carried weight. Ore weighs 6, so 46 ore weigh 276.
        await world.GrantAsync(characterId, Ore, 44, state.PermanentInventoryContainerId, 1);
        var overCap = await GatherAsync(context, session, 1);
        AssertRejected(overCap, ItemTransactionErrorCodes.CarryWeightLimitExceeded);
    }

    [PostgresIntegrationFact]
    public async Task GatheringRejectsAQuestOrBagYield()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("gatherer_yields");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var pickaxeId = await world.GrantAsync(characterId, Pickaxe, 1, state.PermanentInventoryContainerId, 0);
        await world.EquipAsync(characterId, pickaxeId, ItemEquipmentSlotIds.Tool);
        var session = await world.JoinAsync(player);

        AssertRejected(
            await GatherAsync(context, session, 1, "fixture.letter"),
            ItemTransactionErrorCodes.GatheringYieldInvalid);
        AssertRejected(
            await GatherAsync(context, session, 1, Pack),
            ItemTransactionErrorCodes.GatheringYieldInvalid);
    }

    [PostgresIntegrationFact]
    public async Task NewCharacterReceivesTheCatalogStartingBalanceWithALedgerRow()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var catalog = await ItemCatalogRuntimeLoader.LoadAsync(
            context.CatalogSource.RuntimeCatalogPath,
            CancellationToken.None);
        var authoring = ItemCatalogRuntimeVerifier.ToAuthoring(catalog);
        authoring.Economy.StartingBalance = 75;
        await context.ItemCatalogSeeder.SeedAsync(
            ItemCatalogCompiler.Compile(authoring),
            CancellationToken.None);

        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("starting_balance");

        Assert.Equal(75, player.Character.Currency);
        Assert.Equal(75, await CurrencyAsync(context, player.Character.Id));
        var ledger = Assert.Single(await LedgerAsync(context, player.Character.Id));
        Assert.Equal(CurrencySourceKinds.StartingBalance, ledger.SourceKind);
        Assert.Equal(75, ledger.Delta);
        Assert.Null(ledger.ItemOperationId);
    }

    [PostgresIntegrationFact]
    public async Task WalletRejectsNegativeBalancesAndOverflowWithoutALedgerRow()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("wallet_limits");
        var characterId = player.Character.Id;
        var wallet = context.Services.GetRequiredService<ICharacterWallet>();
        var source = new CurrencyChangeSource(CurrencySourceKinds.Development, "wallet_test", null);

        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var scope = new PostgresTransactionScope(connection, transaction);
        var debit = await wallet.DebitAsync(scope, characterId, 1, source, CancellationToken.None);
        var credit = await wallet.CreditAsync(scope, characterId, CurrencyRules.MaximumBalance, source, CancellationToken.None);
        var overflow = await wallet.CreditAsync(scope, characterId, 1, source, CancellationToken.None);
        await transaction.CommitAsync();

        Assert.False(debit.Succeeded);
        Assert.True(credit.Succeeded);
        Assert.False(overflow.Succeeded);
        Assert.Equal(CurrencyRules.MaximumBalance, overflow.Before);
        Assert.Equal(VendorPricing.MaximumCurrencyBalance, CurrencyRules.MaximumBalance);
        Assert.Single(await LedgerAsync(context, characterId));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => context.ExecuteAsync(
            $"update characters set currency = -1 where id = '{characterId}';"));
    }

    private static Task<ItemTransactionResult> SellAsync(
        PostgresIntegrationTestContext context,
        ItemScenarioWorld world,
        ConsumedSimulationJoinTicketResponse session,
        Guid characterId,
        Guid itemInstanceId,
        int quantity,
        long expectedPrice)
    {
        return SellCoreAsync();

        async Task<ItemTransactionResult> SellCoreAsync()
        {
            var item = await world.LoadItemAsync(itemInstanceId);
            return await ExecuteAsync(
                context,
                session,
                Request(session, ItemOperationKinds.VendorSale, await RevisionAsync(world, characterId))
                    with
                {
                    ItemInstanceId = itemInstanceId,
                    ExpectedItemRevision = item.Revision,
                    Quantity = quantity,
                    ExpectedPrice = expectedPrice
                });
        }
    }

    private static Task<ItemTransactionResult> GatherAsync(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session,
        int quantity,
        string yieldDefinitionId = Ore)
    {
        return ExecuteAsync(
            context,
            session,
            Request(session, ItemOperationKinds.GatherResource, 0, vendorProfileId: null)
                with
            {
                CapabilityId = "resource.mine",
                DefinitionId = yieldDefinitionId,
                Quantity = quantity,
                ResourceProfileId = "resource.fixture",
                ResourceNodeId = "fixture.node:0000",
                RequiredToolKind = "pickaxe"
            });
    }

    private static SimulationItemOperationRequest Request(
        ConsumedSimulationJoinTicketResponse session,
        string operationKind,
        long expectedCharacterRevision,
        string? vendorProfileId = Vendor)
    {
        return new SimulationItemOperationRequest(
            Guid.NewGuid(),
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            new SimulationItemAccessRequest(false, false, false, false, vendorProfileId),
            operationKind,
            expectedCharacterRevision,
            InteractionSessionId: Guid.NewGuid(),
            CapabilityId: "services.vendor");
    }

    private static async Task<ItemTransactionResult> ExecuteAsync(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session,
        SimulationItemOperationRequest request)
    {
        await using var scope = context.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<SimulationItemMutationService>();
        var result = await service.ExecuteAsync(
            session.WorkerId,
            session.SimulationSessionId,
            request,
            CancellationToken.None);
        return result.Value
            ?? new ItemTransactionResult(
                request.OperationId,
                request.OperationKind!,
                false,
                new ItemTransactionError(result.Error!.Code, result.Error.Message),
                [],
                [],
                [],
                [],
                null);
    }

    private static async Task<long> RevisionAsync(ItemScenarioWorld world, Guid characterId)
    {
        return (await world.LoadStateAsync(characterId)).Revision;
    }

    private static async Task CreditAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        long amount)
    {
        var wallet = context.Services.GetRequiredService<ICharacterWallet>();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var credit = await wallet.CreditAsync(
            new PostgresTransactionScope(connection, transaction),
            characterId,
            amount,
            new CurrencyChangeSource(CurrencySourceKinds.Development, "integration_test", null),
            CancellationToken.None);
        Assert.True(credit.Succeeded);
        await transaction.CommitAsync();
    }

    private static async Task<long> CurrencyAsync(PostgresIntegrationTestContext context, Guid characterId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long>(
            "select currency from characters where id = @CharacterId;",
            new { CharacterId = characterId });
    }

    private static async Task<IReadOnlyList<LedgerRow>> LedgerAsync(
        PostgresIntegrationTestContext context,
        Guid characterId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return (await connection.QueryAsync<LedgerRow>(
            """
            select
                source_kind as "SourceKind",
                source_id as "SourceId",
                item_operation_id as "ItemOperationId",
                delta as "Delta",
                balance_after as "BalanceAfter"
            from character_currency_ledger
            where character_id = @CharacterId
            order by id;
            """,
            new { CharacterId = characterId })).ToArray();
    }

    // Every balance change writes one ledger row, so the deltas always sum to the balance.
    private static async Task AssertLedgerMatchesBalanceAsync(
        PostgresIntegrationTestContext context,
        Guid characterId)
    {
        var ledger = await LedgerAsync(context, characterId);
        var balance = await CurrencyAsync(context, characterId);
        Assert.Equal(balance, ledger.Sum(row => row.Delta));
        Assert.Equal(balance, ledger.Count == 0 ? 0 : ledger[^1].BalanceAfter);
    }

    private static async Task<CharacterInventorySnapshotResponse> SnapshotAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var result = await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!;
    }

    private static void AssertRejected(ItemTransactionResult result, string code)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(code, result.Error!.Code);
    }

    private sealed class LedgerRow
    {
        public string SourceKind { get; set; } = string.Empty;

        public string SourceId { get; set; } = string.Empty;

        public Guid? ItemOperationId { get; set; }

        public long Delta { get; set; }

        public long BalanceAfter { get; set; }
    }
}
