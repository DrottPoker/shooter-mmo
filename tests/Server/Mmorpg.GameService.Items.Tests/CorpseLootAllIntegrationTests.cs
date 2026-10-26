using Dapper;
using Mmorpg.GameService.Items.Tests.ItemTransactions;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

// Loot all takes everything that fits from a corpse in one transaction, and a corpse Bag
// moves whole into an empty Bag slot or, once empty, like any other item.
public sealed class CorpseLootAllIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task LootAllEquipsFreeSlotsTopsUpArrowsAndFillsTheInventory()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var source = await world.RegisterAsync("loot_all_source");
        var looter = await world.RegisterAsync("loot_all_looter");
        var sourceInventory = (await world.LoadStateAsync(source.Character.Id)).PermanentInventoryContainerId;
        var looterInventory = (await world.LoadStateAsync(looter.Character.Id)).PermanentInventoryContainerId;
        var sword = await GrantAndEquipAsync(world, source.Character.Id, "fixture.sword", 1, "main_hand");
        var ring = await GrantAndEquipAsync(world, source.Character.Id, "fixture.ring", 1, "ring_1");
        var arrows = await world.GrantAsync(source.Character.Id, "fixture.arrow", 20, sourceInventory, 3);
        var ore = await world.GrantAsync(source.Character.Id, "fixture.ore", 30, sourceInventory, 4);
        var equippedArrows = await GrantAndEquipAsync(world, looter.Character.Id, "fixture.arrow", 45, "ammunition");
        var corpseId = await world.ProcessDeathAsync(source.Character.Id);

        var result = await LootAllAsync(world, looter.Character.Id, corpseId);

        ItemScenarioWorld.AssertCommitted(result);
        Assert.Equal(sword, await LoadEquippedItemAsync(world, looter.Character.Id, "main_hand"));
        Assert.Equal(ring, await LoadEquippedItemAsync(world, looter.Character.Id, "ring_1"));
        Assert.Equal(50, (await world.LoadItemAsync(equippedArrows)).Quantity);
        // The rest of the stack moves itself after five arrows topped up the equipped stack.
        var placedArrows = await world.LoadItemAsync(await world.FindItemAsync(looterInventory, 0));
        Assert.Equal(arrows, placedArrows.ItemInstanceId);
        Assert.Equal(15, placedArrows.Quantity);
        Assert.Equal(looterInventory, (await world.LoadItemAsync(ore)).ContainerId);
        Assert.Equal(1, (await world.LoadItemAsync(ore)).ContainerSlotIndex);
        Assert.Equal(0, await CountCorpseItemsAsync(world, corpseId));
    }

    [PostgresIntegrationFact]
    public async Task LootAllTakesOnlyWhatTheHardCapAllowsAndLeavesTheRest()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var source = await world.RegisterAsync("loot_all_heavy_source");
        var looter = await world.RegisterAsync("loot_all_heavy_looter");
        var sourceInventory = (await world.LoadStateAsync(source.Character.Id)).PermanentInventoryContainerId;
        var looterInventory = (await world.LoadStateAsync(looter.Character.Id)).PermanentInventoryContainerId;
        var corpseOre = await world.GrantAsync(source.Character.Id, "fixture.ore", 20, sourceInventory, 0);
        // 40 ore weigh 240 of the 280 that capacity 200 allows at the hard cap, so 6 more fit.
        var carriedOre = await world.GrantAsync(looter.Character.Id, "fixture.ore", 40, looterInventory, 0);
        var corpseId = await world.ProcessDeathAsync(source.Character.Id);

        var result = await LootAllAsync(world, looter.Character.Id, corpseId);

        ItemScenarioWorld.AssertCommitted(result);
        Assert.Equal(46, (await world.LoadItemAsync(carriedOre)).Quantity);
        Assert.Equal(14, (await world.LoadItemAsync(corpseOre)).Quantity);
    }

    [PostgresIntegrationFact]
    public async Task LootAllTakesACorpseBagWithItsContentsIntoAnEmptyBagSlot()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var source = await world.RegisterAsync("loot_all_bag_source");
        var looter = await world.RegisterAsync("loot_all_bag_looter");
        var pack = await GrantAndEquipAsync(world, source.Character.Id, "fixture.pack", 1, "bag");
        var packContents = (await world.LoadItemAsync(pack)).BagContentsContainerId!.Value;
        var ore = await world.GrantAsync(source.Character.Id, "fixture.ore", 5, packContents, 1);
        var corpseId = await world.ProcessDeathAsync(source.Character.Id);
        Assert.Equal(packContents, (await world.LoadCorpseSectionsAsync(corpseId)).BagContainerId);

        var result = await LootAllAsync(world, looter.Character.Id, corpseId);

        ItemScenarioWorld.AssertCommitted(result);
        Assert.Equal(pack, await LoadEquippedItemAsync(world, looter.Character.Id, "bag"));
        Assert.Equal(packContents, (await world.LoadItemAsync(ore)).ContainerId);
        Assert.NotEqual(packContents, (await world.LoadCorpseSectionsAsync(corpseId)).BagContainerId);
        Assert.Equal(0, await CountCorpseItemsAsync(world, corpseId));
    }

    [PostgresIntegrationFact]
    public async Task LootAllIsRejectedForAStaleCorpseOrWhenNothingFits()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var source = await world.RegisterAsync("loot_all_full_source");
        var looter = await world.RegisterAsync("loot_all_full_looter");
        var sourceInventory = (await world.LoadStateAsync(source.Character.Id)).PermanentInventoryContainerId;
        var looterInventory = (await world.LoadStateAsync(looter.Character.Id)).PermanentInventoryContainerId;
        var corpseOre = await world.GrantAsync(source.Character.Id, "fixture.ore", 5, sourceInventory, 0);
        // 46 ore weigh 276, so not one more fits under the 280 hard cap.
        await world.GrantAsync(looter.Character.Id, "fixture.ore", 46, looterInventory, 0);
        var corpseId = await world.ProcessDeathAsync(source.Character.Id);

        var stale = await LootAllAsync(world, looter.Character.Id, corpseId, revisionOffset: -1);
        var full = await LootAllAsync(world, looter.Character.Id, corpseId);

        Assert.Equal(ItemTransactionErrorCodes.ItemStateConflict, stale.Error!.Code);
        Assert.Equal(ItemTransactionErrorCodes.InventoryFull, full.Error!.Code);
        Assert.Equal(5, (await world.LoadItemAsync(corpseOre)).Quantity);
    }

    [PostgresIntegrationFact]
    public async Task ACorpseBagWithContentsEquipsIntoAnEmptyBagSlotAndAnEmptyOneLootsLikeAnyItem()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var source = await world.RegisterAsync("corpse_bag_source");
        var looter = await world.RegisterAsync("corpse_bag_looter");
        var sourceInventory = (await world.LoadStateAsync(source.Character.Id)).PermanentInventoryContainerId;
        var looterInventory = (await world.LoadStateAsync(looter.Character.Id)).PermanentInventoryContainerId;
        var pack = await GrantAndEquipAsync(world, source.Character.Id, "fixture.pack", 1, "bag");
        var packContents = (await world.LoadItemAsync(pack)).BagContentsContainerId!.Value;
        var ore = await world.GrantAsync(source.Character.Id, "fixture.ore", 5, packContents, 0);
        var sparePack = await world.GrantAsync(source.Character.Id, "fixture.pack", 1, sourceInventory, 2);
        var corpseId = await world.ProcessDeathAsync(source.Character.Id);

        var equipped = await LootToEquipmentAsync(world, looter.Character.Id, corpseId, pack, "bag");
        var spare = await LootIntoContainerAsync(world, looter.Character.Id, corpseId, sparePack, looterInventory, 3);

        ItemScenarioWorld.AssertCommitted(equipped);
        ItemScenarioWorld.AssertCommitted(spare);
        Assert.Equal(pack, await LoadEquippedItemAsync(world, looter.Character.Id, "bag"));
        Assert.Equal(packContents, (await world.LoadItemAsync(ore)).ContainerId);
        Assert.NotEqual(packContents, (await world.LoadCorpseSectionsAsync(corpseId)).BagContainerId);
        Assert.Equal(looterInventory, (await world.LoadItemAsync(sparePack)).ContainerId);
    }

    [PostgresIntegrationFact]
    public async Task LiveMobLootAllGrantsWholeEntriesOnceAndASingleGrantCannotRepeatThem()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("live_loot_all_looter");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        var arrows = new MobLootGrantEntry(Guid.NewGuid(), "fixture.arrow", 8);
        var ore = new MobLootGrantEntry(Guid.NewGuid(), "fixture.ore", 3);
        var corpseId = Guid.NewGuid();

        var first = await GrantLootAllAsync(world, session, characterId, corpseId, [arrows, ore]);
        var again = await GrantLootAllAsync(world, session, characterId, corpseId, [arrows, ore]);
        var single = await GrantSingleAsync(world, session, characterId, corpseId, ore, inventoryId, 5);

        ItemScenarioWorld.AssertCommitted(first);
        Assert.NotNull(await LoadEquippedItemAsync(world, characterId, "ammunition"));
        Assert.Equal(3, (await world.LoadItemAsync(await world.FindItemAsync(inventoryId, 0))).Quantity);
        ItemScenarioWorld.AssertCommitted(again);
        Assert.Equal(3, (await world.LoadItemAsync(await world.FindItemAsync(inventoryId, 0))).Quantity);
        Assert.Equal(ItemTransactionErrorCodes.ItemAlreadyLooted, single.Error!.Code);
        Assert.Equal(2, await CountGrantsAsync(world, [arrows.GrantId, ore.GrantId]));
    }

    private static async Task<ItemTransactionResult> LootAllAsync(
        ItemScenarioWorld world,
        Guid looterId,
        Guid corpseId,
        long revisionOffset = 0)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        var revision = await connection.QuerySingleAsync<long>(
            "select revision from corpses where id = @CorpseId;",
            new { CorpseId = corpseId });
        return await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<LootAllCorpseItemsCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new LootAllCorpseItemsCommand(looterId, corpseId, revision + revisionOffset)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> LootToEquipmentAsync(
        ItemScenarioWorld world,
        Guid looterId,
        Guid corpseId,
        Guid itemInstanceId,
        string equipmentSlotId)
    {
        var item = await world.LoadItemAsync(itemInstanceId);
        return await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<LootCorpseItemToEquipmentCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new LootCorpseItemToEquipmentCommand(
                    looterId,
                    corpseId,
                    itemInstanceId,
                    item.Revision,
                    equipmentSlotId,
                    null,
                    null)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> LootIntoContainerAsync(
        ItemScenarioWorld world,
        Guid looterId,
        Guid corpseId,
        Guid itemInstanceId,
        Guid containerId,
        int slotIndex)
    {
        var item = await world.LoadItemAsync(itemInstanceId);
        return await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<LootCorpseItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new LootCorpseItemCommand(
                    looterId,
                    corpseId,
                    itemInstanceId,
                    item.Revision,
                    containerId,
                    await world.LoadContainerRevisionAsync(containerId),
                    slotIndex,
                    null,
                    null)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> GrantLootAllAsync(
        ItemScenarioWorld world,
        ConsumedSimulationJoinTicketResponse session,
        Guid characterId,
        Guid corpseId,
        IReadOnlyList<MobLootGrantEntry> loot)
    {
        var state = await world.LoadStateAsync(characterId);
        return await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantMobLootAllCommand>(
                Guid.NewGuid(),
                ItemScenarioWorld.SimulationActor(session),
                new GrantMobLootAllCommand(characterId, state.Revision, corpseId, "mob.feral_wolf", loot)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> GrantSingleAsync(
        ItemScenarioWorld world,
        ConsumedSimulationJoinTicketResponse session,
        Guid characterId,
        Guid corpseId,
        MobLootGrantEntry loot,
        Guid containerId,
        int slotIndex)
    {
        var state = await world.LoadStateAsync(characterId);
        return await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantMobLootCommand>(
                loot.GrantId,
                ItemScenarioWorld.SimulationActor(session),
                new GrantMobLootCommand(
                    characterId,
                    state.Revision,
                    loot.GrantId,
                    corpseId,
                    "mob.feral_wolf",
                    loot.DefinitionId,
                    loot.Quantity,
                    containerId,
                    await world.LoadContainerRevisionAsync(containerId),
                    slotIndex)),
            CancellationToken.None);
    }

    private static async Task<Guid> GrantAndEquipAsync(
        ItemScenarioWorld world,
        Guid characterId,
        string definitionId,
        int quantity,
        string equipmentSlotId)
    {
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        var itemId = await world.GrantAsync(characterId, definitionId, quantity, inventoryId, 19);
        await world.EquipAsync(characterId, itemId, equipmentSlotId);
        return itemId;
    }

    private static async Task<Guid?> LoadEquippedItemAsync(
        ItemScenarioWorld world,
        Guid characterId,
        string equipmentSlotId)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<Guid?>(
            """
            select id
            from item_instances
            where equipped_character_id = @CharacterId
              and equipment_slot_id = @EquipmentSlotId;
            """,
            new { CharacterId = characterId, EquipmentSlotId = equipmentSlotId });
    }

    private static async Task<int> CountCorpseItemsAsync(ItemScenarioWorld world, Guid corpseId)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            """
            select count(*)
            from corpse_sections section
            join item_instances item on item.container_id = section.container_id
            where section.corpse_id = @CorpseId;
            """,
            new { CorpseId = corpseId });
    }

    private static async Task<int> CountGrantsAsync(ItemScenarioWorld world, Guid[] grantIds)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            "select count(*) from live_mob_loot_grants where grant_id = any(@GrantIds);",
            new { GrantIds = grantIds });
    }
}
