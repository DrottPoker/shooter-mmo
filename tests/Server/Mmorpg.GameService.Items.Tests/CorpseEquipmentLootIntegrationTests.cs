using Dapper;
using Mmorpg.GameService.Items.Tests.ItemTransactions;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

// Looting a corpse item straight into an equipment slot: into an empty slot, as a top-up of the
// equipped stack, or as a swap that moves the equipped item into the corpse slot the loot left.
public sealed class CorpseEquipmentLootIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task ACorpseItemEquipsIntoAnEmptySlot()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var scenario = await CreateLootScenarioAsync(world, "empty");

        var result = await LootToEquipmentAsync(world, scenario, scenario.BowId, "main_hand", null);

        ItemScenarioWorld.AssertCommitted(result);
        Assert.Equal(scenario.BowId, await LoadEquippedItemAsync(world, scenario.LooterId, "main_hand"));
        Assert.Null((await world.LoadItemAsync(scenario.BowId)).ContainerId);
    }

    [PostgresIntegrationFact]
    public async Task ACorpseItemSwapsWithTheEquippedItemIntoTheCorpseSlot()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var scenario = await CreateLootScenarioAsync(world, "swap");
        var bowBefore = await world.LoadItemAsync(scenario.BowId);
        var sword = await GrantAndEquipAsync(world, scenario.LooterId, "fixture.sword", 1, "main_hand");

        var result = await LootToEquipmentAsync(world, scenario, scenario.BowId, "main_hand", sword);

        ItemScenarioWorld.AssertCommitted(result);
        Assert.Equal(scenario.BowId, await LoadEquippedItemAsync(world, scenario.LooterId, "main_hand"));
        var swordAfter = await world.LoadItemAsync(sword);
        Assert.Equal(bowBefore.ContainerId, swordAfter.ContainerId);
        Assert.Equal(bowBefore.ContainerSlotIndex, swordAfter.ContainerSlotIndex);
        Assert.Contains(result.ItemRevisions, item => item.ItemInstanceId == sword);
    }

    [PostgresIntegrationFact]
    public async Task CorpseArrowsTopUpTheEquippedStackOfTheSameDefinition()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var scenario = await CreateLootScenarioAsync(world, "arrows");
        var arrowsBefore = await world.LoadItemAsync(scenario.ArrowsId);
        var equippedArrows = await GrantAndEquipAsync(world, scenario.LooterId, "fixture.arrow", 30, "ammunition");

        var result = await LootToEquipmentAsync(world, scenario, scenario.ArrowsId, "ammunition", equippedArrows);

        ItemScenarioWorld.AssertCommitted(result);
        Assert.Equal(30 + arrowsBefore.Quantity, (await world.LoadItemAsync(equippedArrows)).Quantity);
        Assert.Equal(equippedArrows, await LoadEquippedItemAsync(world, scenario.LooterId, "ammunition"));
        Assert.Equal(0, await CountItemsAsync(world, scenario.ArrowsId));
    }

    [PostgresIntegrationFact]
    public async Task CorpseArrowsThatCannotTopUpTheEquippedStackAreRejectedInsteadOfSwapped()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var scenario = await CreateLootScenarioAsync(world, "full_arrows");
        var arrowsBefore = await world.LoadItemAsync(scenario.ArrowsId);
        // 45 equipped and 20 looted arrows exceed the 60 an arrow stack holds, so before this rule the
        // stacks would have swapped and left the 45 arrows in the corpse.
        var equippedArrows = await GrantAndEquipAsync(world, scenario.LooterId, "fixture.arrow", 45, "ammunition");

        var result = await LootToEquipmentAsync(world, scenario, scenario.ArrowsId, "ammunition", equippedArrows);

        Assert.False(result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemStackLimitExceeded, result.Error!.Code);
        Assert.Equal(45, (await world.LoadItemAsync(equippedArrows)).Quantity);
        Assert.Equal(equippedArrows, await LoadEquippedItemAsync(world, scenario.LooterId, "ammunition"));
        Assert.Equal(arrowsBefore.ContainerId, (await world.LoadItemAsync(scenario.ArrowsId)).ContainerId);
    }

    [PostgresIntegrationFact]
    public async Task AStaleEquipmentSlotViewIsRejectedWithoutChanges()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var scenario = await CreateLootScenarioAsync(world, "stale");
        var bowBefore = await world.LoadItemAsync(scenario.BowId);
        var sword = await GrantAndEquipAsync(world, scenario.LooterId, "fixture.sword", 1, "main_hand");

        // The client still believes the main hand is empty.
        var result = await LootToEquipmentAsync(world, scenario, scenario.BowId, "main_hand", null);

        Assert.False(result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemStateConflict, result.Error!.Code);
        Assert.Equal(sword, await LoadEquippedItemAsync(world, scenario.LooterId, "main_hand"));
        Assert.Equal(bowBefore.ContainerId, (await world.LoadItemAsync(scenario.BowId)).ContainerId);
    }

    [PostgresIntegrationFact]
    public async Task CorpseLootFollowsTheEquipRulesForBlockedSlotsAndLevels()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var scenario = await CreateLootScenarioAsync(world, "rules");
        await GrantAndEquipAsync(world, scenario.LooterId, "fixture.shield", 1, "off_hand");

        var blocked = await LootToEquipmentAsync(world, scenario, scenario.BowId, "main_hand", null);
        var tooLowLevel = await LootToEquipmentAsync(world, scenario, scenario.HelmId, "head", null);

        Assert.Equal(ItemTransactionErrorCodes.EquipmentSlotBlocked, blocked.Error!.Code);
        Assert.Equal(ItemTransactionErrorCodes.EquipmentLevelRequirementNotMet, tooLowLevel.Error!.Code);
        Assert.NotNull((await world.LoadItemAsync(scenario.BowId)).ContainerId);
        Assert.NotNull((await world.LoadItemAsync(scenario.HelmId)).ContainerId);
    }

    [PostgresIntegrationFact]
    public async Task LiveMobLootFillsAnEmptySlotOrTopsUpTheNamedStackButNeverSwaps()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("live_equipment_looter");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;

        var granted = await GrantLiveLootAsync(world, session, characterId, "fixture.arrow", 8, "ammunition", null);
        ItemScenarioWorld.AssertCommitted(granted);
        var arrows = Assert.Single(granted.ItemRevisions).ItemInstanceId;
        Assert.Equal(arrows, await LoadEquippedItemAsync(world, characterId, "ammunition"));
        Assert.Empty(granted.ContainerRevisions);

        // Without a named stack the slot must be empty.
        var unnamed = await GrantLiveLootAsync(world, session, characterId, "fixture.arrow", 5, "ammunition", null);
        Assert.Equal(ItemTransactionErrorCodes.EquipmentSlotOccupied, unnamed.Error!.Code);

        // Naming the equipped stack tops it up within the 50 the fixture ammunition slot holds.
        ItemScenarioWorld.AssertCommitted(
            await GrantLiveLootAsync(world, session, characterId, "fixture.arrow", 5, "ammunition", arrows));
        Assert.Equal(13, (await world.LoadItemAsync(arrows)).Quantity);
        var overLimit = await GrantLiveLootAsync(world, session, characterId, "fixture.arrow", 40, "ammunition", arrows);
        Assert.Equal(ItemTransactionErrorCodes.ItemStackLimitExceeded, overLimit.Error!.Code);

        // Another item never swaps, because the worker cannot take live loot back.
        var swap = await GrantLiveLootAsync(world, session, characterId, "fixture.ore", 2, "ammunition", arrows);
        Assert.Equal(ItemTransactionErrorCodes.EquipmentSlotOccupied, swap.Error!.Code);
        Assert.Equal(13, (await world.LoadItemAsync(arrows)).Quantity);

        // A stack in a container slot tops up the same way.
        var ore = await world.GrantAsync(characterId, "fixture.ore", 3, inventoryId, 2);
        ItemScenarioWorld.AssertCommitted(
            await GrantLiveLootIntoContainerAsync(world, session, characterId, "fixture.ore", 4, inventoryId, 2, ore));
        Assert.Equal(7, (await world.LoadItemAsync(ore)).Quantity);
    }

    private static Task<ItemTransactionResult> GrantLiveLootAsync(
        ItemScenarioWorld world,
        ConsumedSimulationJoinTicketResponse session,
        Guid characterId,
        string definitionId,
        int quantity,
        string equipmentSlotId,
        Guid? targetItemInstanceId)
    {
        return GrantAsync(
            world,
            session,
            characterId,
            definitionId,
            quantity,
            Guid.Empty,
            0,
            -1,
            equipmentSlotId,
            targetItemInstanceId);
    }

    private static async Task<ItemTransactionResult> GrantLiveLootIntoContainerAsync(
        ItemScenarioWorld world,
        ConsumedSimulationJoinTicketResponse session,
        Guid characterId,
        string definitionId,
        int quantity,
        Guid containerId,
        int slotIndex,
        Guid? targetItemInstanceId)
    {
        return await GrantAsync(
            world,
            session,
            characterId,
            definitionId,
            quantity,
            containerId,
            await world.LoadContainerRevisionAsync(containerId),
            slotIndex,
            null,
            targetItemInstanceId);
    }

    private static async Task<ItemTransactionResult> GrantAsync(
        ItemScenarioWorld world,
        ConsumedSimulationJoinTicketResponse session,
        Guid characterId,
        string definitionId,
        int quantity,
        Guid containerId,
        long containerRevision,
        int slotIndex,
        string? equipmentSlotId,
        Guid? targetItemInstanceId)
    {
        var state = await world.LoadStateAsync(characterId);
        long? targetRevision = targetItemInstanceId is Guid targetId
            ? (await world.LoadItemAsync(targetId)).Revision
            : null;
        var grantId = Guid.NewGuid();
        return await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantMobLootCommand>(
                grantId,
                ItemScenarioWorld.SimulationActor(session),
                new GrantMobLootCommand(
                    characterId,
                    state.Revision,
                    grantId,
                    Guid.NewGuid(),
                    "mob.feral_wolf",
                    definitionId,
                    quantity,
                    containerId,
                    containerRevision,
                    slotIndex,
                    equipmentSlotId,
                    targetItemInstanceId,
                    targetRevision)),
            CancellationToken.None);
    }
    private sealed record LootScenario(Guid LooterId, Guid CorpseId, Guid BowId, Guid ArrowsId, Guid HelmId);

    // A dead character leaves a bow, twenty arrows, and a level 3 helm in a corpse, and a second
    // character loots them.
    private static async Task<LootScenario> CreateLootScenarioAsync(ItemScenarioWorld world, string name)
    {
        var source = await world.RegisterAsync("corpse_" + name);
        var looter = await world.RegisterAsync("looter_" + name);
        var sourceId = source.Character.Id;
        var inventoryId = (await world.LoadStateAsync(sourceId)).PermanentInventoryContainerId;
        var bow = await world.GrantAsync(sourceId, "fixture.bow", 1, inventoryId, 0);
        var arrows = await world.GrantAsync(sourceId, "fixture.arrow", 20, inventoryId, 1);
        var helm = await world.GrantAsync(sourceId, "fixture.helm", 1, inventoryId, 2);
        var corpseId = await world.ProcessDeathAsync(sourceId);
        return new LootScenario(looter.Character.Id, corpseId, bow, arrows, helm);
    }

    private static async Task<Guid> GrantAndEquipAsync(
        ItemScenarioWorld world,
        Guid characterId,
        string definitionId,
        int quantity,
        string equipmentSlotId)
    {
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        var itemId = await world.GrantAsync(characterId, definitionId, quantity, inventoryId, 0);
        await world.EquipAsync(characterId, itemId, equipmentSlotId);
        return itemId;
    }

    private static async Task<ItemTransactionResult> LootToEquipmentAsync(
        ItemScenarioWorld world,
        LootScenario scenario,
        Guid itemInstanceId,
        string equipmentSlotId,
        Guid? equippedItemInstanceId)
    {
        var item = await world.LoadItemAsync(itemInstanceId);
        long? equippedRevision = equippedItemInstanceId is Guid equippedId
            ? (await world.LoadItemAsync(equippedId)).Revision
            : null;
        return await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<LootCorpseItemToEquipmentCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new LootCorpseItemToEquipmentCommand(
                    scenario.LooterId,
                    scenario.CorpseId,
                    itemInstanceId,
                    item.Revision,
                    equipmentSlotId,
                    equippedItemInstanceId,
                    equippedRevision)),
            CancellationToken.None);
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

    private static async Task<int> CountItemsAsync(ItemScenarioWorld world, Guid itemInstanceId)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            "select count(*) from item_instances where id = @ItemInstanceId;",
            new { ItemInstanceId = itemInstanceId });
    }
}
