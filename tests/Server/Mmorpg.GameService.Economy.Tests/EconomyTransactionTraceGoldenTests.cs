using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Mmorpg.WorldData.Items;
using static Mmorpg.GameService.Testing.ItemScenarioWorld;

namespace Mmorpg.GameService.Economy.Tests;

// Pins the exact statements, transaction boundaries, stored outcome, and audit journal of the
// vendor and gathering commands, as ItemTransactionTraceGoldenTests does for the item commands. A
// refactor must keep every golden file byte-identical.
public sealed class EconomyTransactionTraceGoldenTests
{
    private const string Ore = "fixture.ore";
    private const string Potion = "fixture.potion";
    private const string Pickaxe = "fixture.pickaxe";
    private const string Vendor = "vendor.fixture_store";

    [PostgresIntegrationFact]
    public async Task VendorAndGatheringCommandsMatchTheirGoldenTraces()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(context, typeof(EconomyTransactionTraceGoldenTests).Assembly);
        var player = await world.RegisterAsync("golden_economy");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var inventoryId = state.PermanentInventoryContainerId;
        await world.ExecuteSqlAsync(
            "update characters set currency = 500 where id = @CharacterId;",
            new { CharacterId = characterId });
        var pickaxeId = await world.GrantAsync(characterId, Pickaxe, 1, inventoryId, 0);
        await world.EquipAsync(characterId, pickaxeId, ItemEquipmentSlotIds.Tool);
        var oreId = await world.GrantAsync(characterId, Ore, 3, inventoryId, 1);
        var session = await world.JoinAsync(player);
        var vendorAccess = VendorAccess.Claim(ItemTransactionLiveAccess.None, Vendor);

        state = await world.LoadStateAsync(characterId);
        var purchaseId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("vendor_purchase.success", purchaseId, service => service.ExecuteAsync(
            new ItemTransactionRequest<PurchaseFromVendorCommand>(
                purchaseId,
                SimulationActor(session, vendorAccess),
                new PurchaseFromVendorCommand(characterId, state.Revision, Vendor, Potion, 2, 50)),
            CancellationToken.None)));

        var gatherId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("gather_resource.success", gatherId, service => service.ExecuteAsync(
            new ItemTransactionRequest<GatherResourceCommand>(
                gatherId,
                SimulationActor(session),
                new GatherResourceCommand(characterId, "resource.golden", "golden.node:0000", "pickaxe", Ore, 1)),
            CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        var ore = await world.LoadItemAsync(oreId);
        var partialSaleId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("vendor_sale.partial_stack", partialSaleId, service => service.ExecuteAsync(
            new ItemTransactionRequest<SellToVendorCommand>(
                partialSaleId,
                SimulationActor(session, vendorAccess),
                new SellToVendorCommand(characterId, state.Revision, Vendor, oreId, ore.Revision, 2, 8)),
            CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        ore = await world.LoadItemAsync(oreId);
        var saleId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("vendor_sale.success", saleId, service => service.ExecuteAsync(
            new ItemTransactionRequest<SellToVendorCommand>(
                saleId,
                SimulationActor(session, vendorAccess),
                new SellToVendorCommand(characterId, state.Revision, Vendor, oreId, ore.Revision, 2, 8)),
            CancellationToken.None)));
    }
}
