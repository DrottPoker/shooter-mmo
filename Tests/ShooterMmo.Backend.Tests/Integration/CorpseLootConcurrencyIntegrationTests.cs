using AuthService.Items;
using Dapper;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class CorpseLootConcurrencyIntegrationTests
{
    private const string ShardId = "local-shard-1";
    private const string CorpsePresentationKey = "corpse.generic_loot_crate";

    [PostgresIntegrationFact]
    public async Task UnrelatedConcurrentLootCommitsAndSameItemHasOneWinner()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var source = await context.RegisterPlayerAsync(
            "loot-source@example.com",
            "loot_source",
            "Loot Source");
        var firstLooter = await context.RegisterPlayerAsync(
            "loot-first@example.com",
            "loot_first",
            "First Looter");
        var secondLooter = await context.RegisterPlayerAsync(
            "loot-second@example.com",
            "loot_second",
            "Second Looter");
        var sourceSnapshot = await GetSnapshotAsync(context, source);
        var firstItem = await GrantAsync(
            context,
            source,
            "medical.field_dressing",
            1,
            sourceSnapshot.PermanentInventory.ContainerId,
            0);
        var secondItem = await GrantAsync(
            context,
            source,
            "tool.starter_pickaxe",
            1,
            sourceSnapshot.PermanentInventory.ContainerId,
            1);
        var contestedItem = await GrantAsync(
            context,
            source,
            "ring.starter_band",
            1,
            sourceSnapshot.PermanentInventory.ContainerId,
            2);
        var death = await CreateCorpseAsync(context, source);
        var firstDestination = (await GetSnapshotAsync(context, firstLooter)).PermanentInventory;
        var secondDestination = (await GetSnapshotAsync(context, secondLooter)).PermanentInventory;

        var unrelated = await Task.WhenAll(
            LootAsync(
                context,
                firstLooter.Character.Id,
                death.Corpse.CorpseId,
                firstItem,
                firstDestination.ContainerId,
                0),
            LootAsync(
                context,
                secondLooter.Character.Id,
                death.Corpse.CorpseId,
                secondItem,
                secondDestination.ContainerId,
                0));
        Assert.All(unrelated, result => Assert.True(result.Succeeded, result.Error?.Message));

        var race = await Task.WhenAll(
            LootAsync(
                context,
                firstLooter.Character.Id,
                death.Corpse.CorpseId,
                contestedItem,
                firstDestination.ContainerId,
                1),
            LootAsync(
                context,
                secondLooter.Character.Id,
                death.Corpse.CorpseId,
                contestedItem,
                secondDestination.ContainerId,
                1));

        var winner = Assert.Single(race, result => result.Succeeded);
        Assert.NotNull(winner);
        var loser = Assert.Single(race, result => !result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemAlreadyLooted, loser.Error!.Code);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_instances where id = @ItemId;",
                new { ItemId = contestedItem }));
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from item_instances
                where id = @ItemId
                  and container_id = any(@DestinationIds);
                """,
                new
                {
                    ItemId = contestedItem,
                    DestinationIds = new[]
                    {
                        firstDestination.ContainerId,
                        secondDestination.ContainerId
                    }
                }));
    }

    [PostgresIntegrationFact]
    public async Task ConcurrentPartialStackLootNeverDuplicatesOrGoesNegativeAndCanRefresh()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var source = await context.RegisterPlayerAsync(
            "stack-source@example.com",
            "stack_source",
            "Stack Source");
        var firstLooter = await context.RegisterPlayerAsync(
            "stack-first@example.com",
            "stack_first",
            "First Stack Looter");
        var secondLooter = await context.RegisterPlayerAsync(
            "stack-second@example.com",
            "stack_second",
            "Second Stack Looter");
        var sourceSnapshot = await GetSnapshotAsync(context, source);
        var stackId = await GrantAsync(
            context,
            source,
            "material.iron_ore",
            10,
            sourceSnapshot.PermanentInventory.ContainerId,
            0);
        var death = await CreateCorpseAsync(context, source);
        var firstDestination = (await GetSnapshotAsync(context, firstLooter)).PermanentInventory;
        var secondDestination = (await GetSnapshotAsync(context, secondLooter)).PermanentInventory;
        var initialItem = await LoadItemAsync(context, stackId);

        var race = await Task.WhenAll(
            LootPartialAsync(
                context,
                firstLooter.Character.Id,
                death.Corpse.CorpseId,
                initialItem,
                6,
                firstDestination.ContainerId,
                0),
            LootPartialAsync(
                context,
                secondLooter.Character.Id,
                death.Corpse.CorpseId,
                initialItem,
                6,
                secondDestination.ContainerId,
                0));

        Assert.Single(race, result => result.Succeeded);
        var stale = Assert.Single(race, result => !result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemStateConflict, stale.Error!.Code);
        await AssertDefinitionQuantityAsync(
            context,
            "material.iron_ore",
            10,
            stackId,
            firstDestination.ContainerId,
            secondDestination.ContainerId);

        var staleLooter = race[0].Succeeded ? secondLooter : firstLooter;
        var staleDestination = race[0].Succeeded ? secondDestination : firstDestination;
        var refreshedItem = await LoadItemAsync(context, stackId);
        var retry = await LootPartialAsync(
            context,
            staleLooter.Character.Id,
            death.Corpse.CorpseId,
            refreshedItem,
            2,
            staleDestination.ContainerId,
            0);
        Assert.True(retry.Succeeded, retry.Error?.Message);
        await AssertDefinitionQuantityAsync(
            context,
            "material.iron_ore",
            10,
            stackId,
            firstDestination.ContainerId,
            secondDestination.ContainerId);
    }

    [PostgresIntegrationFact]
    public async Task BagSwapRacingChildLootCommitsOneCompatibleAggregateOutcome()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var source = await context.RegisterPlayerAsync(
            "bag-source@example.com",
            "bag_source",
            "Bag Source");
        var bagLooter = await context.RegisterPlayerAsync(
            "bag-looter@example.com",
            "bag_looter",
            "Bag Looter");
        var childLooter = await context.RegisterPlayerAsync(
            "child-looter@example.com",
            "child_looter",
            "Child Looter");
        var corpseBag = await EquipNewBagAsync(context, source);
        var sourceWithBag = await GetSnapshotAsync(context, source);
        var child = await GrantAsync(
            context,
            source,
            "medical.field_dressing",
            2,
            sourceWithBag.EquippedBag!.Contents.ContainerId,
            0);
        var death = await CreateCorpseAsync(context, source);
        var playerBag = await EquipNewBagAsync(context, bagLooter);
        var childDestination = (await GetSnapshotAsync(context, childLooter)).PermanentInventory;
        var corpseBagState = await LoadItemAsync(context, corpseBag);
        var playerBagState = await LoadItemAsync(context, playerBag);
        var childState = await LoadItemAsync(context, child);

        var race = await Task.WhenAll(
            SwapBagAsync(
                context,
                bagLooter.Character.Id,
                death.Corpse.CorpseId,
                corpseBagState,
                playerBagState),
            LootAsync(
                context,
                childLooter.Character.Id,
                death.Corpse.CorpseId,
                childState,
                childDestination.ContainerId,
                0));

        Assert.Single(race, result => result.Succeeded);
        Assert.Single(race, result => !result.Succeeded);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            2,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_instances where id = any(@BagIds);",
                new { BagIds = new[] { corpseBag, playerBag } }));
        Assert.Equal(
            2,
            await connection.ExecuteScalarAsync<int>(
                """
                select count(distinct container.id)
                from item_containers container
                where container.bound_bag_item_instance_id = any(@BagIds)
                  and container.container_type = 'bag_contents';
                """,
                new { BagIds = new[] { corpseBag, playerBag } }));
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_instances where id = @ChildId and quantity = 2;",
                new { ChildId = child }));
    }

    [PostgresIntegrationFact]
    public async Task ConcurrentBagSwapsCannotSplitEitherAggregate()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var source = await context.RegisterPlayerAsync(
            "swap-source@example.com",
            "swap_source",
            "Swap Source");
        var firstLooter = await context.RegisterPlayerAsync(
            "swap-first@example.com",
            "swap_first",
            "First Swap Looter");
        var secondLooter = await context.RegisterPlayerAsync(
            "swap-second@example.com",
            "swap_second",
            "Second Swap Looter");
        var corpseBag = await EquipNewBagAsync(context, source);
        var sourceSnapshot = await GetSnapshotAsync(context, source);
        await GrantAsync(
            context,
            source,
            "material.iron_ore",
            3,
            sourceSnapshot.EquippedBag!.Contents.ContainerId,
            0);
        var death = await CreateCorpseAsync(context, source);
        var firstBag = await EquipNewBagAsync(context, firstLooter);
        var secondBag = await EquipNewBagAsync(context, secondLooter);
        var corpseBagState = await LoadItemAsync(context, corpseBag);

        var race = await Task.WhenAll(
            SwapBagAsync(
                context,
                firstLooter.Character.Id,
                death.Corpse.CorpseId,
                corpseBagState,
                await LoadItemAsync(context, firstBag)),
            SwapBagAsync(
                context,
                secondLooter.Character.Id,
                death.Corpse.CorpseId,
                corpseBagState,
                await LoadItemAsync(context, secondBag)));

        Assert.Single(race, result => result.Succeeded);
        var loser = Assert.Single(race, result => !result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.BagStateChanged, loser.Error!.Code);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            3,
            await connection.ExecuteScalarAsync<int>(
                """
                select count(distinct container.bound_bag_item_instance_id)
                from item_containers container
                where container.bound_bag_item_instance_id = any(@BagIds)
                  and container.container_type = 'bag_contents';
                """,
                new { BagIds = new[] { corpseBag, firstBag, secondBag } }));
        Assert.Equal(
            3,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_instances where id = any(@BagIds);",
                new { BagIds = new[] { corpseBag, firstBag, secondBag } }));
    }

    [PostgresIntegrationFact]
    public async Task HardCapRejectsMoreWeightAndDeadCharacterCompetesNormally()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var source = await context.RegisterPlayerAsync(
            "dead-source@example.com",
            "dead_source",
            "Dead Source");
        var otherLooter = await context.RegisterPlayerAsync(
            "dead-other@example.com",
            "dead_other",
            "Other Looter");
        var cappedLooter = await context.RegisterPlayerAsync(
            "capped-looter@example.com",
            "capped_looter",
            "Capped Looter");
        var sourceSnapshot = await GetSnapshotAsync(context, source);
        var contested = await GrantAsync(
            context,
            source,
            "tool.starter_pickaxe",
            1,
            sourceSnapshot.PermanentInventory.ContainerId,
            0);
        var weighted = await GrantAsync(
            context,
            source,
            "medical.field_dressing",
            1,
            sourceSnapshot.PermanentInventory.ContainerId,
            1);
        var death = await CreateCorpseAsync(context, source);
        var deadDestination = (await GetSnapshotAsync(context, source)).PermanentInventory;
        var otherDestination = (await GetSnapshotAsync(context, otherLooter)).PermanentInventory;

        var race = await Task.WhenAll(
            LootAsync(
                context,
                source.Character.Id,
                death.Corpse.CorpseId,
                contested,
                deadDestination.ContainerId,
                0),
            LootAsync(
                context,
                otherLooter.Character.Id,
                death.Corpse.CorpseId,
                contested,
                otherDestination.ContainerId,
                0));
        Assert.Single(race, result => result.Succeeded);
        Assert.Single(race, result => !result.Succeeded);

        var cappedDestination = (await GetSnapshotAsync(context, cappedLooter)).PermanentInventory;
        await GrantAsync(
            context,
            cappedLooter,
            "material.iron_ore",
            46,
            cappedDestination.ContainerId,
            0);
        await GrantAsync(
            context,
            cappedLooter,
            "medical.field_dressing",
            2,
            cappedDestination.ContainerId,
            1);
        var cappedSnapshot = await GetSnapshotAsync(context, cappedLooter);
        Assert.Equal(280, cappedSnapshot.CarriedWeight);
        Assert.Equal(200, cappedSnapshot.CarryCapacity);

        var rejected = await LootAsync(
            context,
            cappedLooter.Character.Id,
            death.Corpse.CorpseId,
            weighted,
            cappedDestination.ContainerId,
            2);
        Assert.False(rejected.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.CarryWeightLimitExceeded, rejected.Error!.Code);
        Assert.Equal(death.Corpse.CorpseId, await FindCorpseForItemAsync(context, weighted));
    }

    private static async Task<ItemTransactionResult> LootAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        Guid corpseId,
        Guid itemId,
        Guid destinationContainerId,
        int destinationSlotIndex)
    {
        return await LootAsync(
            context,
            characterId,
            corpseId,
            await LoadItemAsync(context, itemId),
            destinationContainerId,
            destinationSlotIndex);
    }

    private static async Task<ItemTransactionResult> LootAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        Guid corpseId,
        ItemRow item,
        Guid destinationContainerId,
        int destinationSlotIndex)
    {
        return await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<LootCorpseItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new LootCorpseItemCommand(
                    characterId,
                    corpseId,
                    item.ItemInstanceId,
                    item.Revision,
                    destinationContainerId,
                    await LoadContainerRevisionAsync(context, destinationContainerId),
                    destinationSlotIndex,
                    null,
                    null)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> LootPartialAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        Guid corpseId,
        ItemRow item,
        int quantity,
        Guid destinationContainerId,
        int destinationSlotIndex)
    {
        return await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<LootCorpsePartialStackCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new LootCorpsePartialStackCommand(
                    characterId,
                    corpseId,
                    item.ItemInstanceId,
                    item.Revision,
                    quantity,
                    destinationContainerId,
                    await LoadContainerRevisionAsync(context, destinationContainerId),
                    destinationSlotIndex,
                    null,
                    null)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> SwapBagAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        Guid corpseId,
        ItemRow corpseBag,
        ItemRow playerBag)
    {
        return await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<SwapCorpseBagCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new SwapCorpseBagCommand(
                    characterId,
                    corpseId,
                    corpseBag.ItemInstanceId,
                    corpseBag.Revision,
                    corpseBag.BagContentsContainerId!.Value,
                    corpseBag.BagContentsRevision!.Value,
                    playerBag.ItemInstanceId,
                    playerBag.Revision,
                    playerBag.BagContentsContainerId!.Value,
                    playerBag.BagContentsRevision!.Value)),
            CancellationToken.None);
    }

    private static async Task<Guid> EquipNewBagAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var snapshot = await GetSnapshotAsync(context, player);
        var bagId = await GrantAsync(
            context,
            player,
            "bag.field_pack",
            1,
            snapshot.PermanentInventory.ContainerId,
            0);
        snapshot = await GetSnapshotAsync(context, player);
        var result = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<EquipItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new EquipItemCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    bagId,
                    (await LoadItemAsync(context, bagId)).Revision,
                    "bag")),
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return bagId;
    }

    private static async Task<Guid> GrantAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player,
        string definitionId,
        int quantity,
        Guid destinationContainerId,
        int destinationSlotIndex)
    {
        var snapshot = await GetSnapshotAsync(context, player);
        var result = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    definitionId,
                    quantity,
                    destinationContainerId,
                    destinationSlotIndex)),
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return Assert.Single(result.ItemRevisions).ItemInstanceId;
    }

    private static async Task<PlayerDeathPartitionResponse> CreateCorpseAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var snapshot = await GetSnapshotAsync(context, player);
        var result = await context.CorpseService.ProcessSystemDeathAsync(
            Guid.NewGuid(),
            new ProcessPlayerDeathCommand(
                Guid.NewGuid(),
                player.Character.Id,
                snapshot.ItemStateRevision,
                ShardId,
                0,
                0,
                -1,
                0,
                0,
                0,
                1,
                CorpsePresentationKey),
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!;
    }

    private static async Task<CharacterInventorySnapshotResponse> GetSnapshotAsync(
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

    private static async Task<ItemRow> LoadItemAsync(
        PostgresIntegrationTestContext context,
        Guid itemId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<ItemRow>(
            """
            select
                item.id as "ItemInstanceId",
                item.revision as "Revision",
                item.quantity as "Quantity",
                bag_contents.id as "BagContentsContainerId",
                bag_contents.revision as "BagContentsRevision"
            from item_instances item
            left join item_containers bag_contents
              on bag_contents.bound_bag_item_instance_id = item.id
             and bag_contents.container_type = 'bag_contents'
            where item.id = @ItemId;
            """,
            new { ItemId = itemId });
    }

    private static async Task<long> LoadContainerRevisionAsync(
        PostgresIntegrationTestContext context,
        Guid containerId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long>(
            "select revision from item_containers where id = @ContainerId;",
            new { ContainerId = containerId });
    }

    private static async Task AssertDefinitionQuantityAsync(
        PostgresIntegrationTestContext context,
        string definitionId,
        int expectedQuantity,
        Guid sourceItemId,
        params Guid[] destinationContainerIds)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var quantities = (await connection.QueryAsync<int>(
            """
            select quantity
            from item_instances
            where definition_id = @DefinitionId
              and (id = @SourceItemId or container_id = any(@DestinationContainerIds));
            """,
            new
            {
                DefinitionId = definitionId,
                SourceItemId = sourceItemId,
                DestinationContainerIds = destinationContainerIds
            })).ToArray();
        Assert.All(quantities, quantity => Assert.True(quantity > 0));
        Assert.Equal(expectedQuantity, quantities.Sum());
    }

    private static async Task<Guid?> FindCorpseForItemAsync(
        PostgresIntegrationTestContext context,
        Guid itemId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<Guid?>(
            """
            select section.corpse_id
            from item_instances item
            join corpse_sections section on section.container_id = item.container_id
            where item.id = @ItemId;
            """,
            new { ItemId = itemId });
    }

    private sealed class ItemRow
    {
        public Guid ItemInstanceId { get; set; }

        public long Revision { get; set; }

        public int Quantity { get; set; }

        public Guid? BagContentsContainerId { get; set; }

        public long? BagContentsRevision { get; set; }
    }
}
