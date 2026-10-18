using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Tests.ItemTransactions;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

// Equipment slots that hold a stack, such as the ammunition slot, slots that an equipped item
// blocks, and level requirements. The fixture ammunition slot holds 50 arrows while an arrow
// stack holds 60, and the fixture helm requires level 3.
public sealed class EquipmentStackIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task ArrowStackEquipsTopsUpAndSplitsBackIntoCarriedStorage()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("stack_player");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var inventoryId = state.PermanentInventoryContainerId;
        var equipped = await world.GrantAsync(characterId, "fixture.arrow", 30, inventoryId, 0);
        var topUp = await world.GrantAsync(characterId, "fixture.arrow", 20, inventoryId, 1);
        var overflow = await world.GrantAsync(characterId, "fixture.arrow", 5, inventoryId, 2);
        Assert.Equal(55, await LoadCarriedWeightAsync(world, characterId));

        await world.EquipAsync(characterId, equipped, "ammunition");
        Assert.Equal(25, await LoadCarriedWeightAsync(world, characterId));

        ItemScenarioWorld.AssertCommitted(await MergeAsync(world, characterId, topUp, equipped));
        Assert.Equal(50, (await world.LoadItemAsync(equipped)).Quantity);
        Assert.Equal(5, await LoadCarriedWeightAsync(world, characterId));

        var full = await MergeAsync(world, characterId, overflow, equipped);
        Assert.False(full.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemStackLimitExceeded, full.Error!.Code);

        ItemScenarioWorld.AssertCommitted(await SplitAsync(
            world,
            characterId,
            equipped,
            15,
            inventoryId,
            1));
        Assert.Equal(35, (await world.LoadItemAsync(equipped)).Quantity);
        var split = await world.FindItemAsync(inventoryId, 1);
        Assert.Equal(15, (await world.LoadItemAsync(split)).Quantity);
        Assert.Equal(20, await LoadCarriedWeightAsync(world, characterId));

        ItemScenarioWorld.AssertCommitted(await MergeAsync(world, characterId, equipped, split));
        Assert.Equal(50, (await world.LoadItemAsync(split)).Quantity);
        Assert.Equal(55, await LoadCarriedWeightAsync(world, characterId));
        Assert.Equal(0, await CountEquippedAsync(world, characterId));
    }

    [PostgresIntegrationFact]
    public async Task StackLargerThanTheEquipmentSlotIsRejected()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("stack_limit_player");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var arrows = await world.GrantAsync(
            characterId,
            "fixture.arrow",
            55,
            state.PermanentInventoryContainerId,
            0);

        var result = await EquipAsync(world, characterId, arrows, "ammunition");

        Assert.False(result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemStackLimitExceeded, result.Error!.Code);
        Assert.Equal(0, await CountEquippedAsync(world, characterId));
    }

    [PostgresIntegrationFact]
    public async Task TwoHandedWeaponAndOffHandItemCannotBeEquippedTogether()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("blocking_player");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var inventoryId = state.PermanentInventoryContainerId;
        var bow = await world.GrantAsync(characterId, "fixture.bow", 1, inventoryId, 0);
        var shield = await world.GrantAsync(characterId, "fixture.shield", 1, inventoryId, 1);
        var sword = await world.GrantAsync(characterId, "fixture.sword", 1, inventoryId, 2);

        await world.EquipAsync(characterId, bow, "main_hand");
        var blockedShield = await EquipAsync(world, characterId, shield, "off_hand");
        var blockedSword = await EquipAsync(world, characterId, sword, "off_hand");
        Assert.Equal(ItemTransactionErrorCodes.EquipmentSlotBlocked, blockedShield.Error!.Code);
        Assert.Equal(ItemTransactionErrorCodes.EquipmentSlotBlocked, blockedSword.Error!.Code);

        ItemScenarioWorld.AssertCommitted(await UnequipAsync(world, characterId, bow, inventoryId, 0));
        await world.EquipAsync(characterId, shield, "off_hand");
        var blockedBow = await EquipAsync(world, characterId, bow, "main_hand");
        Assert.Equal(ItemTransactionErrorCodes.EquipmentSlotBlocked, blockedBow.Error!.Code);

        await world.EquipAsync(characterId, sword, "main_hand");
        Assert.Equal(2, await CountEquippedAsync(world, characterId));
    }

    [PostgresIntegrationFact]
    public async Task EquipmentAboveTheCharacterLevelIsRejectedUntilTheLevelIsReached()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("level_player");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var helm = await world.GrantAsync(
            characterId,
            "fixture.helm",
            1,
            state.PermanentInventoryContainerId,
            0);

        var rejected = await EquipAsync(world, characterId, helm, "head");
        Assert.False(rejected.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.EquipmentLevelRequirementNotMet, rejected.Error!.Code);
        Assert.Equal(0, await CountEquippedAsync(world, characterId));

        await SetLevelAsync(context, characterId, 2);
        var stillRejected = await EquipAsync(world, characterId, helm, "head");
        Assert.Equal(ItemTransactionErrorCodes.EquipmentLevelRequirementNotMet, stillRejected.Error!.Code);

        await SetLevelAsync(context, characterId, 3);
        ItemScenarioWorld.AssertCommitted(await EquipAsync(world, characterId, helm, "head"));
        Assert.Equal(1, await CountEquippedAsync(world, characterId));
    }

    [PostgresIntegrationFact]
    public async Task CorpseAmmunitionSlotKeepsTheEquipmentSlotLimit()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("corpse_arrow_player");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var inventoryId = state.PermanentInventoryContainerId;
        var equipped = await world.GrantAsync(characterId, "fixture.arrow", 40, inventoryId, 0);
        var carried = await world.GrantAsync(characterId, "fixture.arrow", 15, inventoryId, 1);
        await world.EquipAsync(characterId, equipped, "ammunition");

        var corpseId = await world.ProcessDeathAsync(characterId);
        var sections = await world.LoadCorpseSectionsAsync(corpseId);
        var ammunitionSlotIndex = await LoadCorpseEquipmentSlotIndexAsync(
            world,
            sections.EquipmentContainerId,
            "ammunition");
        Assert.Equal(
            equipped,
            await world.FindItemAsync(sections.EquipmentContainerId, ammunitionSlotIndex));
        Assert.Equal(40, (await world.LoadItemAsync(equipped)).Quantity);

        var tooMany = await MoveWithinCorpseAsync(
            world,
            characterId,
            corpseId,
            carried,
            null,
            sections.EquipmentContainerId,
            ammunitionSlotIndex,
            equipped);
        Assert.False(tooMany.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemStackLimitExceeded, tooMany.Error!.Code);

        ItemScenarioWorld.AssertCommitted(await MoveWithinCorpseAsync(
            world,
            characterId,
            corpseId,
            carried,
            10,
            sections.EquipmentContainerId,
            ammunitionSlotIndex,
            equipped));
        Assert.Equal(50, (await world.LoadItemAsync(equipped)).Quantity);
        Assert.Equal(5, (await world.LoadItemAsync(carried)).Quantity);
    }

    private static async Task SetLevelAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        int level)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        Assert.True(await context.Services.GetRequiredService<ICharacterProgression>().SetLevelAsync(
            new PostgresTransactionScope(connection, transaction),
            characterId,
            level,
            CancellationToken.None));
        await transaction.CommitAsync();
    }

    private static async Task<ItemTransactionResult> EquipAsync(
        ItemScenarioWorld world,
        Guid characterId,
        Guid itemInstanceId,
        string equipmentSlotId)
    {
        var state = await world.LoadStateAsync(characterId);
        var item = await world.LoadItemAsync(itemInstanceId);
        return await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<EquipItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new EquipItemCommand(
                    characterId,
                    state.Revision,
                    itemInstanceId,
                    item.Revision,
                    equipmentSlotId)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> UnequipAsync(
        ItemScenarioWorld world,
        Guid characterId,
        Guid itemInstanceId,
        Guid destinationContainerId,
        int destinationSlotIndex)
    {
        var state = await world.LoadStateAsync(characterId);
        var item = await world.LoadItemAsync(itemInstanceId);
        return await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<UnequipItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new UnequipItemCommand(
                    characterId,
                    state.Revision,
                    itemInstanceId,
                    item.Revision,
                    destinationContainerId,
                    destinationSlotIndex)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> MergeAsync(
        ItemScenarioWorld world,
        Guid characterId,
        Guid sourceItemInstanceId,
        Guid targetItemInstanceId)
    {
        var state = await world.LoadStateAsync(characterId);
        var source = await world.LoadItemAsync(sourceItemInstanceId);
        var target = await world.LoadItemAsync(targetItemInstanceId);
        return await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<MergeItemStacksCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new MergeItemStacksCommand(
                    characterId,
                    state.Revision,
                    sourceItemInstanceId,
                    source.Revision,
                    targetItemInstanceId,
                    target.Revision)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> SplitAsync(
        ItemScenarioWorld world,
        Guid characterId,
        Guid itemInstanceId,
        int quantity,
        Guid destinationContainerId,
        int destinationSlotIndex)
    {
        var state = await world.LoadStateAsync(characterId);
        var item = await world.LoadItemAsync(itemInstanceId);
        return await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<SplitItemStackCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new SplitItemStackCommand(
                    characterId,
                    state.Revision,
                    itemInstanceId,
                    item.Revision,
                    quantity,
                    destinationContainerId,
                    destinationSlotIndex)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> MoveWithinCorpseAsync(
        ItemScenarioWorld world,
        Guid characterId,
        Guid corpseId,
        Guid itemInstanceId,
        int? quantity,
        Guid destinationContainerId,
        int destinationSlotIndex,
        Guid targetItemInstanceId)
    {
        var item = await world.LoadItemAsync(itemInstanceId);
        var target = await world.LoadItemAsync(targetItemInstanceId);
        var destinationRevision = await world.LoadContainerRevisionAsync(destinationContainerId);
        return quantity is int partialQuantity
            ? await world.Context.ItemTransactionService.ExecuteAsync(
                new ItemTransactionRequest<MoveCorpsePartialStackCommand>(
                    Guid.NewGuid(),
                    ItemTransactionActor.ForSystem(),
                    new MoveCorpsePartialStackCommand(
                        characterId,
                        corpseId,
                        itemInstanceId,
                        item.Revision,
                        partialQuantity,
                        destinationContainerId,
                        destinationRevision,
                        destinationSlotIndex,
                        targetItemInstanceId,
                        target.Revision)),
                CancellationToken.None)
            : await world.Context.ItemTransactionService.ExecuteAsync(
                new ItemTransactionRequest<MoveCorpseItemCommand>(
                    Guid.NewGuid(),
                    ItemTransactionActor.ForSystem(),
                    new MoveCorpseItemCommand(
                        characterId,
                        corpseId,
                        itemInstanceId,
                        item.Revision,
                        destinationContainerId,
                        destinationRevision,
                        destinationSlotIndex,
                        targetItemInstanceId,
                        target.Revision)),
                CancellationToken.None);
    }

    private static async Task<long> LoadCarriedWeightAsync(
        ItemScenarioWorld world,
        Guid characterId)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<long>(
            "select carried_weight from character_item_states where character_id = @CharacterId;",
            new { CharacterId = characterId });
    }

    private static async Task<int> CountEquippedAsync(ItemScenarioWorld world, Guid characterId)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            "select count(*) from item_instances where equipped_character_id = @CharacterId;",
            new { CharacterId = characterId });
    }

    private static async Task<int> LoadCorpseEquipmentSlotIndexAsync(
        ItemScenarioWorld world,
        Guid containerId,
        string equipmentSlotId)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            """
            select slot_index
            from item_container_slots
            where container_id = @ContainerId
              and equipment_slot_id = @EquipmentSlotId;
            """,
            new { ContainerId = containerId, EquipmentSlotId = equipmentSlotId });
    }
}
