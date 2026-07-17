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

    [PostgresIntegrationFact]
    public async Task CorpseContainerSupportsDepositMergeAndAtomicSwapInBothDirections()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var source = await context.RegisterPlayerAsync(
            "transfer-source@example.com",
            "transfer_source",
            "Transfer Source");
        var player = await context.RegisterPlayerAsync(
            "transfer-player@example.com",
            "transfer_player",
            "Transfer Player");
        var sourceInventory = (await GetSnapshotAsync(context, source)).PermanentInventory;
        var corpseTool = await GrantAsync(
            context,
            source,
            "tool.starter_pickaxe",
            1,
            sourceInventory.ContainerId,
            0);
        var corpseOre = await GrantAsync(
            context,
            source,
            "material.iron_ore",
            5,
            sourceInventory.ContainerId,
            2);
        var death = await CreateCorpseAsync(context, source);
        var corpseInventoryId = await LoadCorpseSectionContainerAsync(
            context,
            death.Corpse.CorpseId,
            "general_inventory");

        var playerInventory = (await GetSnapshotAsync(context, player)).PermanentInventory;
        var playerRing = await GrantAsync(
            context,
            player,
            "ring.starter_band",
            1,
            playerInventory.ContainerId,
            0);
        var playerMedical = await GrantAsync(
            context,
            player,
            "medical.field_dressing",
            1,
            playerInventory.ContainerId,
            1);
        var playerOre = await GrantAsync(
            context,
            player,
            "material.iron_ore",
            4,
            playerInventory.ContainerId,
            2);
        var emptySlotDeposit = await GrantAsync(
            context,
            player,
            "armor.starter_vest",
            1,
            playerInventory.ContainerId,
            3);

        var lootSwap = await LootWithTargetAsync(
            context,
            player.Character.Id,
            death.Corpse.CorpseId,
            await LoadItemAsync(context, corpseTool),
            playerInventory.ContainerId,
            0,
            await LoadItemAsync(context, playerRing));
        Assert.True(lootSwap.Succeeded, lootSwap.Error?.Message);
        await AssertLocationAsync(context, corpseTool, playerInventory.ContainerId, 0);
        await AssertLocationAsync(context, playerRing, corpseInventoryId, 0);

        var depositSwap = await DepositWithTargetAsync(
            context,
            player.Character.Id,
            death.Corpse.CorpseId,
            await LoadItemAsync(context, playerMedical),
            corpseInventoryId,
            0,
            await LoadItemAsync(context, playerRing));
        Assert.True(depositSwap.Succeeded, depositSwap.Error?.Message);
        await AssertLocationAsync(context, playerMedical, corpseInventoryId, 0);
        await AssertLocationAsync(context, playerRing, playerInventory.ContainerId, 1);

        var partialDeposit = await DepositPartialWithTargetAsync(
            context,
            player.Character.Id,
            death.Corpse.CorpseId,
            await LoadItemAsync(context, playerOre),
            2,
            corpseInventoryId,
            2,
            await LoadItemAsync(context, corpseOre));
        Assert.True(partialDeposit.Succeeded, partialDeposit.Error?.Message);
        Assert.Equal(2, (await LoadItemAsync(context, playerOre)).Quantity);
        Assert.Equal(7, (await LoadItemAsync(context, corpseOre)).Quantity);

        var deposit = await DepositAsync(
            context,
            player.Character.Id,
            death.Corpse.CorpseId,
            await LoadItemAsync(context, emptySlotDeposit),
            corpseInventoryId,
            3);
        Assert.True(deposit.Succeeded, deposit.Error?.Message);
        await AssertLocationAsync(context, emptySlotDeposit, corpseInventoryId, 3);

        var lootBack = await LootAsync(
            context,
            player.Character.Id,
            death.Corpse.CorpseId,
            await LoadItemAsync(context, emptySlotDeposit),
            playerInventory.ContainerId,
            3);
        Assert.True(lootBack.Succeeded, lootBack.Error?.Message);
        await AssertLocationAsync(context, emptySlotDeposit, playerInventory.ContainerId, 3);
    }

    [PostgresIntegrationFact]
    public async Task CorpseContainerSupportsInternalRearrangementAndTypedEquipmentSlots()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var source = await context.RegisterPlayerAsync(
            "internal-move-source@example.com",
            "internal_move_source",
            "Internal Move Source");
        var viewer = await context.RegisterPlayerAsync(
            "internal-move-viewer@example.com",
            "internal_move_viewer",
            "Internal Move Viewer");
        var sourceInventory = (await GetSnapshotAsync(context, source)).PermanentInventory;
        var ring = await GrantAsync(
            context,
            source,
            "ring.starter_band",
            1,
            sourceInventory.ContainerId,
            0);
        var vest = await GrantAsync(
            context,
            source,
            "armor.starter_vest",
            1,
            sourceInventory.ContainerId,
            1);
        var ore = await GrantAsync(
            context,
            source,
            "material.iron_ore",
            6,
            sourceInventory.ContainerId,
            2);
        var death = await CreateCorpseAsync(context, source);
        var corpseInventoryId = await LoadCorpseSectionContainerAsync(
            context,
            death.Corpse.CorpseId,
            "general_inventory");
        var corpseEquipmentId = await LoadCorpseSectionContainerAsync(
            context,
            death.Corpse.CorpseId,
            "equipment");
        var viewerRevision = (await GetSnapshotAsync(context, viewer)).ItemStateRevision;

        var moved = await MoveWithinCorpseAsync(
            context,
            viewer.Character.Id,
            death.Corpse.CorpseId,
            await LoadItemAsync(context, ring),
            corpseInventoryId,
            5);
        Assert.True(moved.Succeeded, moved.Error?.Message);
        Assert.Empty(moved.CharacterRevisions);
        await AssertLocationAsync(context, ring, corpseInventoryId, 5);

        var split = await MovePartialWithinCorpseAsync(
            context,
            viewer.Character.Id,
            death.Corpse.CorpseId,
            await LoadItemAsync(context, ore),
            2,
            corpseInventoryId,
            6);
        Assert.True(split.Succeeded, split.Error?.Message);
        Assert.Empty(split.CharacterRevisions);
        var splitItemId = Assert.Single(
            split.ItemRevisions,
            revision => revision.ItemInstanceId != ore).ItemInstanceId;
        Assert.Equal(4, (await LoadItemAsync(context, ore)).Quantity);
        Assert.Equal(2, (await LoadItemAsync(context, splitItemId)).Quantity);

        var merged = await MoveWithinCorpseWithTargetAsync(
            context,
            viewer.Character.Id,
            death.Corpse.CorpseId,
            await LoadItemAsync(context, splitItemId),
            corpseInventoryId,
            2,
            await LoadItemAsync(context, ore));
        Assert.True(merged.Succeeded, merged.Error?.Message);
        Assert.Empty(merged.CharacterRevisions);
        Assert.Equal(6, (await LoadItemAsync(context, ore)).Quantity);

        var swapped = await MoveWithinCorpseWithTargetAsync(
            context,
            viewer.Character.Id,
            death.Corpse.CorpseId,
            await LoadItemAsync(context, ring),
            corpseInventoryId,
            1,
            await LoadItemAsync(context, vest));
        Assert.True(swapped.Succeeded, swapped.Error?.Message);
        Assert.Empty(swapped.CharacterRevisions);
        await AssertLocationAsync(context, ring, corpseInventoryId, 1);
        await AssertLocationAsync(context, vest, corpseInventoryId, 5);

        var ringSlotIndex = await LoadEquipmentSlotIndexAsync(context, "ring_1");
        var ringEquipped = await MoveWithinCorpseAsync(
            context,
            viewer.Character.Id,
            death.Corpse.CorpseId,
            await LoadItemAsync(context, ring),
            corpseEquipmentId,
            ringSlotIndex);
        Assert.True(ringEquipped.Succeeded, ringEquipped.Error?.Message);
        await AssertLocationAsync(context, ring, corpseEquipmentId, ringSlotIndex);

        var headSlotIndex = await LoadEquipmentSlotIndexAsync(context, "head");
        var incompatible = await MoveWithinCorpseAsync(
            context,
            viewer.Character.Id,
            death.Corpse.CorpseId,
            await LoadItemAsync(context, vest),
            corpseEquipmentId,
            headSlotIndex);
        Assert.False(incompatible.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemSlotIncompatible, incompatible.Error!.Code);
        await AssertLocationAsync(context, vest, corpseInventoryId, 5);

        var bodySlotIndex = await LoadEquipmentSlotIndexAsync(context, "body_armor");
        var vestEquipped = await MoveWithinCorpseAsync(
            context,
            viewer.Character.Id,
            death.Corpse.CorpseId,
            await LoadItemAsync(context, vest),
            corpseEquipmentId,
            bodySlotIndex);
        Assert.True(vestEquipped.Succeeded, vestEquipped.Error?.Message);
        await AssertLocationAsync(context, vest, corpseEquipmentId, bodySlotIndex);
        Assert.Equal(viewerRevision, (await GetSnapshotAsync(context, viewer)).ItemStateRevision);
    }

    [PostgresIntegrationFact]
    public async Task CorpseDepositEnforcesPolicyAndSwapHardCapWithoutPartialMutation()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var source = await context.RegisterPlayerAsync(
            "deposit-guard-source@example.com",
            "deposit_guard_source",
            "Deposit Guard Source");
        var protectedPlayer = await context.RegisterPlayerAsync(
            "deposit-protected@example.com",
            "deposit_protected",
            "Deposit Protected");
        var cappedPlayer = await context.RegisterPlayerAsync(
            "deposit-capped@example.com",
            "deposit_capped",
            "Deposit Capped");
        var sourceInventory = (await GetSnapshotAsync(context, source)).PermanentInventory;
        var corpseTool = await GrantAsync(
            context,
            source,
            "tool.starter_pickaxe",
            1,
            sourceInventory.ContainerId,
            0);
        var death = await CreateCorpseAsync(context, source);
        var corpseInventoryId = await LoadCorpseSectionContainerAsync(
            context,
            death.Corpse.CorpseId,
            "general_inventory");

        var protectedInventory = (await GetSnapshotAsync(context, protectedPlayer)).PermanentInventory;
        var protectedItem = await GrantAsync(
            context,
            protectedPlayer,
            "ring.starter_band",
            1,
            protectedInventory.ContainerId,
            1);
        var protectedSnapshot = await GetSnapshotAsync(context, protectedPlayer);
        var policyResult = await context.ItemPolicyService.ApplyProtectedOnDeathAsync(
            Guid.NewGuid(),
            protectedPlayer.Character.Id,
            protectedSnapshot.ItemStateRevision,
            protectedItem,
            (await LoadItemAsync(context, protectedItem)).Revision,
            ItemPolicySourceKinds.CatalogDefault,
            "corpse-deposit-policy-test",
            CancellationToken.None);
        Assert.True(policyResult.Succeeded, policyResult.Error?.Message);

        var policyRejected = await DepositAsync(
            context,
            protectedPlayer.Character.Id,
            death.Corpse.CorpseId,
            await LoadItemAsync(context, protectedItem),
            corpseInventoryId,
            1);
        Assert.False(policyRejected.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemPolicyRestricted, policyRejected.Error!.Code);
        await AssertLocationAsync(context, protectedItem, protectedInventory.ContainerId, 1);

        var cappedInventory = (await GetSnapshotAsync(context, cappedPlayer)).PermanentInventory;
        await GrantAsync(
            context,
            cappedPlayer,
            "material.iron_ore",
            46,
            cappedInventory.ContainerId,
            0);
        var cappedTarget = await GrantAsync(
            context,
            cappedPlayer,
            "medical.field_dressing",
            2,
            cappedInventory.ContainerId,
            1);
        var cappedSnapshot = await GetSnapshotAsync(context, cappedPlayer);
        Assert.Equal(280, cappedSnapshot.CarriedWeight);
        Assert.Equal(200, cappedSnapshot.CarryCapacity);

        var capRejected = await LootWithTargetAsync(
            context,
            cappedPlayer.Character.Id,
            death.Corpse.CorpseId,
            await LoadItemAsync(context, corpseTool),
            cappedInventory.ContainerId,
            1,
            await LoadItemAsync(context, cappedTarget));
        Assert.False(capRejected.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.CarryWeightLimitExceeded, capRejected.Error!.Code);
        await AssertLocationAsync(context, corpseTool, corpseInventoryId, 0);
        await AssertLocationAsync(context, cappedTarget, cappedInventory.ContainerId, 1);
    }

    [PostgresIntegrationFact]
    public async Task DepositRacingLootCommitsOneDupeSafeContainerOutcome()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var source = await context.RegisterPlayerAsync(
            "deposit-race-source@example.com",
            "deposit_race_source",
            "Deposit Race Source");
        var depositor = await context.RegisterPlayerAsync(
            "deposit-race-depositor@example.com",
            "deposit_race_depositor",
            "Deposit Race Depositor");
        var looter = await context.RegisterPlayerAsync(
            "deposit-race-looter@example.com",
            "deposit_race_looter",
            "Deposit Race Looter");
        var sourceInventory = (await GetSnapshotAsync(context, source)).PermanentInventory;
        var corpseItem = await GrantAsync(
            context,
            source,
            "tool.starter_pickaxe",
            1,
            sourceInventory.ContainerId,
            0);
        var death = await CreateCorpseAsync(context, source);
        var corpseInventoryId = await LoadCorpseSectionContainerAsync(
            context,
            death.Corpse.CorpseId,
            "general_inventory");
        var depositorInventory = (await GetSnapshotAsync(context, depositor)).PermanentInventory;
        var depositedItem = await GrantAsync(
            context,
            depositor,
            "ring.starter_band",
            1,
            depositorInventory.ContainerId,
            0);
        var looterInventory = (await GetSnapshotAsync(context, looter)).PermanentInventory;
        var expectedCorpseItem = await LoadItemAsync(context, corpseItem);

        var race = await Task.WhenAll(
            DepositWithTargetAsync(
                context,
                depositor.Character.Id,
                death.Corpse.CorpseId,
                await LoadItemAsync(context, depositedItem),
                corpseInventoryId,
                0,
                expectedCorpseItem),
            LootAsync(
                context,
                looter.Character.Id,
                death.Corpse.CorpseId,
                expectedCorpseItem,
                looterInventory.ContainerId,
                0));

        Assert.Single(race, result => result.Succeeded);
        Assert.Single(race, result => !result.Succeeded);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            2,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_instances where id = any(@ItemIds);",
                new { ItemIds = new[] { corpseItem, depositedItem } }));
        Assert.Equal(
            2,
            await connection.ExecuteScalarAsync<int>(
                "select count(distinct container_id::text || ':' || container_slot_index::text) from item_instances where id = any(@ItemIds);",
                new { ItemIds = new[] { corpseItem, depositedItem } }));
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

    private static async Task<ItemTransactionResult> LootWithTargetAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        Guid corpseId,
        ItemRow item,
        Guid destinationContainerId,
        int destinationSlotIndex,
        ItemRow target)
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
                    target.ItemInstanceId,
                    target.Revision)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> DepositAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        Guid corpseId,
        ItemRow item,
        Guid destinationContainerId,
        int destinationSlotIndex)
    {
        return await DepositWithTargetAsync(
            context,
            characterId,
            corpseId,
            item,
            destinationContainerId,
            destinationSlotIndex,
            null);
    }

    private static async Task<ItemTransactionResult> DepositWithTargetAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        Guid corpseId,
        ItemRow item,
        Guid destinationContainerId,
        int destinationSlotIndex,
        ItemRow? target)
    {
        return await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<DepositCorpseItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new DepositCorpseItemCommand(
                    characterId,
                    corpseId,
                    item.ItemInstanceId,
                    item.Revision,
                    destinationContainerId,
                    await LoadContainerRevisionAsync(context, destinationContainerId),
                    destinationSlotIndex,
                    target?.ItemInstanceId,
                    target?.Revision)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> DepositPartialWithTargetAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        Guid corpseId,
        ItemRow item,
        int quantity,
        Guid destinationContainerId,
        int destinationSlotIndex,
        ItemRow target)
    {
        return await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<DepositCorpsePartialStackCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new DepositCorpsePartialStackCommand(
                    characterId,
                    corpseId,
                    item.ItemInstanceId,
                    item.Revision,
                    quantity,
                    destinationContainerId,
                    await LoadContainerRevisionAsync(context, destinationContainerId),
                    destinationSlotIndex,
                    target.ItemInstanceId,
                    target.Revision)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> MoveWithinCorpseAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        Guid corpseId,
        ItemRow item,
        Guid destinationContainerId,
        int destinationSlotIndex)
    {
        return await MoveWithinCorpseWithTargetAsync(
            context,
            characterId,
            corpseId,
            item,
            destinationContainerId,
            destinationSlotIndex,
            null);
    }

    private static async Task<ItemTransactionResult> MoveWithinCorpseWithTargetAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        Guid corpseId,
        ItemRow item,
        Guid destinationContainerId,
        int destinationSlotIndex,
        ItemRow? target)
    {
        return await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<MoveCorpseItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new MoveCorpseItemCommand(
                    characterId,
                    corpseId,
                    item.ItemInstanceId,
                    item.Revision,
                    destinationContainerId,
                    await LoadContainerRevisionAsync(context, destinationContainerId),
                    destinationSlotIndex,
                    target?.ItemInstanceId,
                    target?.Revision)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> MovePartialWithinCorpseAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        Guid corpseId,
        ItemRow item,
        int quantity,
        Guid destinationContainerId,
        int destinationSlotIndex)
    {
        return await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<MoveCorpsePartialStackCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new MoveCorpsePartialStackCommand(
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
                item.container_id as "ContainerId",
                item.container_slot_index as "ContainerSlotIndex",
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

    private static async Task<Guid> LoadCorpseSectionContainerAsync(
        PostgresIntegrationTestContext context,
        Guid corpseId,
        string sectionKind)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<Guid>(
            "select container_id from corpse_sections where corpse_id = @CorpseId and section_kind = @SectionKind;",
            new { CorpseId = corpseId, SectionKind = sectionKind });
    }

    private static async Task<int> LoadEquipmentSlotIndexAsync(
        PostgresIntegrationTestContext context,
        string equipmentSlotId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "select sort_order from equipment_slots where id = @EquipmentSlotId;",
            new { EquipmentSlotId = equipmentSlotId });
    }

    private static async Task AssertLocationAsync(
        PostgresIntegrationTestContext context,
        Guid itemId,
        Guid containerId,
        int slotIndex)
    {
        var item = await LoadItemAsync(context, itemId);
        Assert.Equal(containerId, item.ContainerId);
        Assert.Equal(slotIndex, item.ContainerSlotIndex);
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

        public Guid? ContainerId { get; set; }

        public int? ContainerSlotIndex { get; set; }

        public Guid? BagContentsContainerId { get; set; }

        public long? BagContentsRevision { get; set; }
    }
}
