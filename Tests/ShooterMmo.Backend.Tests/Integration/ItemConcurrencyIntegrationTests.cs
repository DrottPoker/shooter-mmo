using AuthService.Characters;
using AuthService.Items;
using Dapper;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class ItemConcurrencyIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task CarryStateCountsEveryCarriedCustodyExactlyOnceAndExcludesExternalCustody()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "carry-state@example.com",
            "carry_state_player",
            "Carry State Hero");
        var accountId = player.Registration.AccountId;
        var characterId = player.Character.Id;
        var actor = ItemTransactionActor.ForAccount(accountId);
        var initial = await LoadStateAsync(context, characterId);

        var equippedBagId = await GrantAndEquipBagAsync(
            context,
            accountId,
            characterId);
        var afterBagEquip = await LoadStateAsync(context, characterId);
        Assert.True(afterBagEquip.Revision > initial.Revision);
        Assert.Equal(0, afterBagEquip.CarriedWeight);
        Assert.Equal(250, afterBagEquip.CarryCapacity);

        await GrantAsync(
            context,
            accountId,
            characterId,
            "material.iron_ore",
            2,
            afterBagEquip.PermanentInventoryContainerId,
            0);
        await GrantAsync(
            context,
            accountId,
            characterId,
            "bag.field_pack",
            1,
            afterBagEquip.PermanentInventoryContainerId,
            1);
        var rifleId = await GrantAsync(
            context,
            accountId,
            characterId,
            "weapon.training_rifle",
            1,
            afterBagEquip.PermanentInventoryContainerId,
            2);
        var state = await LoadStateAsync(context, characterId);
        var rifle = await LoadItemAsync(context, rifleId);
        var equipRifle = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<EquipItemCommand>(
                Guid.NewGuid(),
                actor,
                new EquipItemCommand(
                    characterId,
                    state.Revision,
                    rifleId,
                    rifle.Revision,
                    "primary_weapon")),
            CancellationToken.None);
        AssertSucceeded(equipRifle);

        var bagContainer = await LoadBagContainerAsync(context, equippedBagId);
        await GrantAsync(
            context,
            accountId,
            characterId,
            "ammunition.training_556",
            10,
            bagContainer.ContainerId,
            6);
        state = await LoadStateAsync(context, characterId);
        await GrantAsync(
            context,
            accountId,
            characterId,
            "medical.field_dressing",
            2,
            state.SecureContainerId,
            0);
        await GrantAsync(
            context,
            accountId,
            characterId,
            "material.iron_ore",
            3,
            state.BankContainerId,
            0);

        state = await LoadStateAsync(context, characterId);
        var recoveryItemId = await GrantAsync(
            context,
            accountId,
            characterId,
            "medical.field_dressing",
            1,
            state.PermanentInventoryContainerId,
            2);
        state = await LoadStateAsync(context, characterId);
        var recoveryItem = await LoadItemAsync(context, recoveryItemId);
        var addRecovery = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<AddRecoveryDeliveryCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new AddRecoveryDeliveryCommand(
                    characterId,
                    state.Revision,
                    "system_restore",
                    "carry-state-zero-weight",
                    null,
                    null,
                    [new ItemRevisionExpectation(recoveryItemId, recoveryItem.Revision)])),
            CancellationToken.None);
        AssertSucceeded(addRecovery);

        state = await LoadStateAsync(context, characterId);
        var corpseItemId = await GrantAsync(
            context,
            accountId,
            characterId,
            "material.iron_ore",
            5,
            state.PermanentInventoryContainerId,
            2);
        var beforeCorpseTransfer = await LoadStateAsync(context, characterId);
        Assert.Equal(66, beforeCorpseTransfer.CarriedWeight);

        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                insert into item_containers (id, container_type, slot_capacity)
                values (@ContainerId, 'corpse_inventory', 1);

                insert into item_container_slots (container_id, slot_index, slot_kind)
                values (@ContainerId, 0, 'general');

                update item_instances
                set container_id = @ContainerId,
                    container_slot_index = 0
                where id = @ItemId;
                """,
                new { ContainerId = Guid.NewGuid(), ItemId = corpseItemId });
        }

        state = await LoadStateAsync(context, characterId);
        await GrantAsync(
            context,
            accountId,
            characterId,
            "ring.starter_band",
            1,
            state.PermanentInventoryContainerId,
            2);

        var final = await LoadStateAsync(context, characterId);
        Assert.Equal(37, final.CarriedWeight);
        Assert.Equal(250, final.CarryCapacity);
    }

    [PostgresIntegrationFact]
    public async Task CoreCommandsMutateAtomicallyAuditAndRecomputeCarryState()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var actor = ItemTransactionActor.ForAccount(player.Registration.AccountId);
        var state = await LoadStateAsync(context, player.Character.Id);

        var grantAmmunition = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                actor,
                new GrantItemCommand(
                    player.Character.Id,
                    state.Revision,
                    "ammunition.training_556",
                    10,
                    state.PermanentInventoryContainerId,
                    0)),
            CancellationToken.None);
        AssertSucceeded(grantAmmunition);
        var ammunitionId = Assert.Single(grantAmmunition.ItemRevisions).ItemInstanceId;

        state = await LoadStateAsync(context, player.Character.Id);
        var ammunition = await LoadItemAsync(context, ammunitionId);
        var relocate = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<RelocateItemCommand>(
                Guid.NewGuid(),
                actor,
                new RelocateItemCommand(
                    player.Character.Id,
                    state.Revision,
                    ammunitionId,
                    ammunition.Revision,
                    state.PermanentInventoryContainerId,
                    null)),
            CancellationToken.None);
        AssertSucceeded(relocate);
        Assert.Equal(1, (await LoadItemAsync(context, ammunitionId)).ContainerSlotIndex);

        state = await LoadStateAsync(context, player.Character.Id);
        ammunition = await LoadItemAsync(context, ammunitionId);
        var split = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<SplitItemStackCommand>(
                Guid.NewGuid(),
                actor,
                new SplitItemStackCommand(
                    player.Character.Id,
                    state.Revision,
                    ammunitionId,
                    ammunition.Revision,
                    4,
                    state.PermanentInventoryContainerId,
                    2)),
            CancellationToken.None);
        AssertSucceeded(split);
        Assert.Equal(2, split.ItemRevisions.Count);
        var splitItemId = Assert.Single(
            split.ItemRevisions,
            item => item.ItemInstanceId != ammunitionId).ItemInstanceId;

        state = await LoadStateAsync(context, player.Character.Id);
        ammunition = await LoadItemAsync(context, ammunitionId);
        var splitItem = await LoadItemAsync(context, splitItemId);
        var merge = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<MergeItemStacksCommand>(
                Guid.NewGuid(),
                actor,
                new MergeItemStacksCommand(
                    player.Character.Id,
                    state.Revision,
                    splitItemId,
                    splitItem.Revision,
                    ammunitionId,
                    ammunition.Revision)),
            CancellationToken.None);
        AssertSucceeded(merge);
        Assert.Equal(10, (await LoadItemAsync(context, ammunitionId)).Quantity);
        Assert.Null(await TryLoadItemAsync(context, splitItemId));

        state = await LoadStateAsync(context, player.Character.Id);
        ammunition = await LoadItemAsync(context, ammunitionId);
        var consume = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<ConsumeItemQuantityCommand>(
                Guid.NewGuid(),
                actor,
                new ConsumeItemQuantityCommand(
                    player.Character.Id,
                    state.Revision,
                    ammunitionId,
                    ammunition.Revision,
                    3)),
            CancellationToken.None);
        AssertSucceeded(consume);
        Assert.Equal(7, (await LoadItemAsync(context, ammunitionId)).Quantity);

        state = await LoadStateAsync(context, player.Character.Id);
        var grantRifle = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                actor,
                new GrantItemCommand(
                    player.Character.Id,
                    state.Revision,
                    "weapon.training_rifle",
                    1,
                    state.PermanentInventoryContainerId,
                    3)),
            CancellationToken.None);
        AssertSucceeded(grantRifle);
        var rifleId = Assert.Single(grantRifle.ItemRevisions).ItemInstanceId;

        state = await LoadStateAsync(context, player.Character.Id);
        var rifle = await LoadItemAsync(context, rifleId);
        var equip = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<EquipItemCommand>(
                Guid.NewGuid(),
                actor,
                new EquipItemCommand(
                    player.Character.Id,
                    state.Revision,
                    rifleId,
                    rifle.Revision,
                    "primary_weapon")),
            CancellationToken.None);
        AssertSucceeded(equip);
        Assert.Equal("primary_weapon", (await LoadItemAsync(context, rifleId)).EquipmentSlotId);

        state = await LoadStateAsync(context, player.Character.Id);
        rifle = await LoadItemAsync(context, rifleId);
        var unequip = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<UnequipItemCommand>(
                Guid.NewGuid(),
                actor,
                new UnequipItemCommand(
                    player.Character.Id,
                    state.Revision,
                    rifleId,
                    rifle.Revision,
                    state.PermanentInventoryContainerId,
                    null)),
            CancellationToken.None);
        AssertSucceeded(unequip);
        Assert.Equal(0, (await LoadItemAsync(context, rifleId)).ContainerSlotIndex);

        state = await LoadStateAsync(context, player.Character.Id);
        rifle = await LoadItemAsync(context, rifleId);
        var destroy = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<DestroyItemCommand>(
                Guid.NewGuid(),
                actor,
                new DestroyItemCommand(
                    player.Character.Id,
                    state.Revision,
                    rifleId,
                    rifle.Revision,
                    "player_discard")),
            CancellationToken.None);
        AssertSucceeded(destroy);
        Assert.Null(await TryLoadItemAsync(context, rifleId));

        state = await LoadStateAsync(context, player.Character.Id);
        var grantBag = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                actor,
                new GrantItemCommand(
                    player.Character.Id,
                    state.Revision,
                    "bag.field_pack",
                    1,
                    state.PermanentInventoryContainerId,
                    null)),
            CancellationToken.None);
        AssertSucceeded(grantBag);
        var bagId = Assert.Single(grantBag.ItemRevisions).ItemInstanceId;
        Assert.Equal(0, (await LoadItemAsync(context, bagId)).ContainerSlotIndex);

        state = await LoadStateAsync(context, player.Character.Id);
        var bag = await LoadItemAsync(context, bagId);
        var moveEmptyBag = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<RelocateItemCommand>(
                Guid.NewGuid(),
                actor,
                new RelocateItemCommand(
                    player.Character.Id,
                    state.Revision,
                    bagId,
                    bag.Revision,
                    state.BankContainerId,
                    null)),
            CancellationToken.None);
        AssertSucceeded(moveEmptyBag);

        state = await LoadStateAsync(context, player.Character.Id);
        Assert.Equal(7, state.CarriedWeight);
        Assert.Equal(200, state.CarryCapacity);
        var finalAmmunition = await LoadItemAsync(context, ammunitionId);
        Assert.Equal(7, finalAmmunition.Quantity);
        Assert.Equal(state.PermanentInventoryContainerId, finalAmmunition.ContainerId);
        var finalBag = await LoadItemAsync(context, bagId);
        Assert.Equal(state.BankContainerId, finalBag.ContainerId);
        Assert.Equal(0, finalBag.ContainerSlotIndex);

        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_destructions where item_instance_id = @ItemId;",
                new { ItemId = rifleId }));
        Assert.Equal(11, await connection.ExecuteScalarAsync<int>(
            "select count(*) from item_operations where status = 'committed';"));
        Assert.Equal(13, await connection.ExecuteScalarAsync<int>(
            "select count(*) from item_operation_changes;"));
    }

    [PostgresIntegrationFact]
    public async Task SlotEligibilitySecureRulesAndHardCapRejectWithoutPartialMutation()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var actor = ItemTransactionActor.ForAccount(player.Registration.AccountId);
        var state = await LoadStateAsync(context, player.Character.Id);
        var grantBag = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                actor,
                new GrantItemCommand(
                    player.Character.Id,
                    state.Revision,
                    "bag.field_pack",
                    1,
                    state.PermanentInventoryContainerId,
                    0)),
            CancellationToken.None);
        AssertSucceeded(grantBag);
        var bagId = Assert.Single(grantBag.ItemRevisions).ItemInstanceId;

        state = await LoadStateAsync(context, player.Character.Id);
        var bag = await LoadItemAsync(context, bagId);
        AssertSucceeded(await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<EquipItemCommand>(
                Guid.NewGuid(),
                actor,
                new EquipItemCommand(
                    player.Character.Id,
                    state.Revision,
                    bagId,
                    bag.Revision,
                    "bag")),
            CancellationToken.None));
        var bagContainer = await LoadBagContainerAsync(context, bagId);

        state = await LoadStateAsync(context, player.Character.Id);
        var grantAmmunition = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                actor,
                new GrantItemCommand(
                    player.Character.Id,
                    state.Revision,
                    "ammunition.training_556",
                    60,
                    bagContainer.ContainerId,
                    6)),
            CancellationToken.None);
        AssertSucceeded(grantAmmunition);

        state = await LoadStateAsync(context, player.Character.Id);
        var invalidSpecialized = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                actor,
                new GrantItemCommand(
                    player.Character.Id,
                    state.Revision,
                    "material.iron_ore",
                    1,
                    bagContainer.ContainerId,
                    4)),
            CancellationToken.None);
        AssertRejected(invalidSpecialized, ItemTransactionErrorCodes.ItemSlotIncompatible);

        var secureForbidden = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                actor,
                new GrantItemCommand(
                    player.Character.Id,
                    state.Revision,
                    "weapon.training_rifle",
                    1,
                    state.SecureContainerId,
                    0)),
            CancellationToken.None);
        AssertRejected(secureForbidden, ItemTransactionErrorCodes.SecureContainerItemForbidden);

        var nearCap = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                actor,
                new GrantItemCommand(
                    player.Character.Id,
                    state.Revision,
                    "material.iron_ore",
                    46,
                    state.PermanentInventoryContainerId,
                    1)),
            CancellationToken.None);
        AssertSucceeded(nearCap);
        state = await LoadStateAsync(context, player.Character.Id);
        Assert.Equal(336, state.CarriedWeight);
        Assert.Equal(250, state.CarryCapacity);

        var aboveCap = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                actor,
                new GrantItemCommand(
                    player.Character.Id,
                    state.Revision,
                    "material.iron_ore",
                    3,
                    state.PermanentInventoryContainerId,
                    2)),
            CancellationToken.None);
        AssertRejected(aboveCap, ItemTransactionErrorCodes.CarryWeightLimitExceeded);

        var after = await LoadStateAsync(context, player.Character.Id);
        Assert.Equal(state.Revision, after.Revision);
        Assert.Equal(336, after.CarriedWeight);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from item_instances
                where container_id = @ContainerId
                  and container_slot_index = 2;
                """,
                new { ContainerId = state.PermanentInventoryContainerId }));
        Assert.Equal(
            3,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_operations where status = 'rejected';"));
    }

    [PostgresIntegrationFact]
    public async Task RecoveryDeliveryAddAndClaimPreserveIdentityPoliciesAndWeight()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var accountActor = ItemTransactionActor.ForAccount(player.Registration.AccountId);
        var state = await LoadStateAsync(context, player.Character.Id);
        var grant = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                accountActor,
                new GrantItemCommand(
                    player.Character.Id,
                    state.Revision,
                    "material.iron_ore",
                    5,
                    state.PermanentInventoryContainerId,
                    0)),
            CancellationToken.None);
        AssertSucceeded(grant);
        var itemId = Assert.Single(grant.ItemRevisions).ItemInstanceId;

        state = await LoadStateAsync(context, player.Character.Id);
        var item = await LoadItemAsync(context, itemId);
        var addDelivery = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<AddRecoveryDeliveryCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new AddRecoveryDeliveryCommand(
                    player.Character.Id,
                    state.Revision,
                    "system_restore",
                    "restore-1",
                    null,
                    null,
                    [new ItemRevisionExpectation(itemId, item.Revision)])),
            CancellationToken.None);
        AssertSucceeded(addDelivery);
        var deliveryId = Assert.Single(addDelivery.RecoveryDeliveryIds);
        state = await LoadStateAsync(context, player.Character.Id);
        Assert.Equal(0, state.CarriedWeight);
        item = await LoadItemAsync(context, itemId);
        Assert.Equal(state.RecoveryStorageContainerId, item.ContainerId);

        var claim = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<ClaimRecoveryDeliveryCommand>(
                Guid.NewGuid(),
                accountActor,
                new ClaimRecoveryDeliveryCommand(
                    player.Character.Id,
                    state.Revision,
                    deliveryId,
                    0,
                    state.BankContainerId,
                    [new ItemRevisionExpectation(itemId, item.Revision)])),
            CancellationToken.None);
        AssertSucceeded(claim);
        Assert.Equal(itemId, Assert.Single(claim.ItemRevisions).ItemInstanceId);

        state = await LoadStateAsync(context, player.Character.Id);
        item = await LoadItemAsync(context, itemId);
        Assert.Equal(state.BankContainerId, item.ContainerId);
        Assert.Equal(0, state.CarriedWeight);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.NotNull(await connection.ExecuteScalarAsync<DateTime?>(
            "select claimed_at from recovery_deliveries where id = @DeliveryId;",
            new { DeliveryId = deliveryId }));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from recovery_delivery_items where recovery_delivery_id = @DeliveryId;",
                new { DeliveryId = deliveryId }));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_container_slots where container_id = @ContainerId;",
                new { ContainerId = state.RecoveryStorageContainerId }));
    }

    [PostgresIntegrationFact]
    public async Task SecureTierReductionMovesDescendingSlotsForEveryAccountCharacterWithoutLoss()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var secondCharacterResult = await context.CharacterService.CreateAsync(
            player.Registration.AccountId,
            new CreateCharacterRequest("Second Hero"),
            CancellationToken.None);
        Assert.True(secondCharacterResult.Succeeded, secondCharacterResult.Error?.Message);
        var secondCharacter = secondCharacterResult.Value!;
        await AddTestSecureTierAsync(context, "secure_container.test_six", 6);

        var firstInitialState = await LoadStateAsync(context, player.Character.Id);
        var secondInitialState = await LoadStateAsync(context, secondCharacter.Id);
        var expandOperationId = Guid.NewGuid();
        var expand = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<ChangeSecureContainerTierCommand>(
                expandOperationId,
                ItemTransactionActor.ForAccount(player.Registration.AccountId),
                new ChangeSecureContainerTierCommand(
                    player.Registration.AccountId,
                    0,
                    "secure_container.test_six",
                    [
                        new CharacterRevisionExpectation(
                            secondCharacter.Id,
                            secondInitialState.Revision),
                        new CharacterRevisionExpectation(
                            player.Character.Id,
                            firstInitialState.Revision)
                    ])),
            CancellationToken.None);
        AssertSucceeded(expand);
        Assert.Equal(1, expand.SecureContainerEntitlementRevision);
        Assert.Equal(2, expand.CharacterRevisions.Count);
        var replayedExpand = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<ChangeSecureContainerTierCommand>(
                expandOperationId,
                ItemTransactionActor.ForAccount(player.Registration.AccountId),
                new ChangeSecureContainerTierCommand(
                    player.Registration.AccountId,
                    0,
                    "secure_container.test_six",
                    [
                        new CharacterRevisionExpectation(
                            player.Character.Id,
                            firstInitialState.Revision),
                        new CharacterRevisionExpectation(
                            secondCharacter.Id,
                            secondInitialState.Revision)
                    ])),
            CancellationToken.None);
        AssertSucceeded(replayedExpand);
        Assert.Equal(expand.SecureContainerEntitlementRevision, replayedExpand.SecureContainerEntitlementRevision);

        var firstState = await LoadStateAsync(context, player.Character.Id);
        var secondState = await LoadStateAsync(context, secondCharacter.Id);
        Assert.Equal(6, await LoadContainerCapacityAsync(context, firstState.SecureContainerId));
        Assert.Equal(6, await LoadContainerCapacityAsync(context, secondState.SecureContainerId));

        var firstSlot3 = await GrantAsync(
            context,
            player.Registration.AccountId,
            player.Character.Id,
            "material.iron_ore",
            1,
            firstState.SecureContainerId,
            3);
        var firstSlot4 = await GrantAsync(
            context,
            player.Registration.AccountId,
            player.Character.Id,
            "material.iron_ore",
            2,
            firstState.SecureContainerId,
            4);
        var firstSlot5 = await GrantAsync(
            context,
            player.Registration.AccountId,
            player.Character.Id,
            "material.iron_ore",
            3,
            firstState.SecureContainerId,
            5);
        var secondSlot5 = await GrantAsync(
            context,
            player.Registration.AccountId,
            secondCharacter.Id,
            "material.iron_ore",
            4,
            secondState.SecureContainerId,
            5);

        var reduce = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<ChangeSecureContainerTierCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new ChangeSecureContainerTierCommand(
                    player.Registration.AccountId,
                    1,
                    "secure_container.base",
                    [])),
            CancellationToken.None);
        AssertSucceeded(reduce);
        Assert.Equal(2, reduce.SecureContainerEntitlementRevision);
        Assert.Equal(2, reduce.RecoveryDeliveryIds.Count);

        firstState = await LoadStateAsync(context, player.Character.Id);
        secondState = await LoadStateAsync(context, secondCharacter.Id);
        Assert.Equal(4, await LoadContainerCapacityAsync(context, firstState.SecureContainerId));
        Assert.Equal(4, await LoadContainerCapacityAsync(context, secondState.SecureContainerId));
        Assert.Equal(firstState.SecureContainerId, (await LoadItemAsync(context, firstSlot3)).ContainerId);
        Assert.Equal(firstState.RecoveryStorageContainerId, (await LoadItemAsync(context, firstSlot4)).ContainerId);
        Assert.Equal(firstState.RecoveryStorageContainerId, (await LoadItemAsync(context, firstSlot5)).ContainerId);
        Assert.Equal(secondState.RecoveryStorageContainerId, (await LoadItemAsync(context, secondSlot5)).ContainerId);
        Assert.Equal(6, firstState.CarriedWeight);
        Assert.Equal(0, secondState.CarriedWeight);

        await using var connection = await context.DataSource.OpenConnectionAsync();
        var firstDeliveryOrder = (await connection.QueryAsync<DeliveryOrderRow>(
            """
            select
                link.item_instance_id as "ItemInstanceId",
                link.item_order as "ItemOrder"
            from recovery_deliveries delivery
            join recovery_delivery_items link on link.recovery_delivery_id = delivery.id
            where delivery.character_id = @CharacterId
              and delivery.source_kind = 'secure_capacity_reduction'
            order by link.item_order;
            """,
            new { CharacterId = player.Character.Id })).ToArray();
        Assert.Equal([firstSlot5, firstSlot4], firstDeliveryOrder.Select(row => row.ItemInstanceId));
        Assert.Equal([0, 1], firstDeliveryOrder.Select(row => row.ItemOrder));
        Assert.Equal(
            4,
            await connection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from item_instances
                where id = any(@ItemIds);
                """,
                new { ItemIds = new[] { firstSlot3, firstSlot4, firstSlot5, secondSlot5 } }));
    }

    [PostgresIntegrationFact]
    public async Task BagSwapMovesCompleteAggregatesAndARejectedSwapChangesNothing()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var secondCharacterResult = await context.CharacterService.CreateAsync(
            player.Registration.AccountId,
            new CreateCharacterRequest("Bag Partner"),
            CancellationToken.None);
        Assert.True(secondCharacterResult.Succeeded, secondCharacterResult.Error?.Message);
        var secondCharacter = secondCharacterResult.Value!;
        var firstBagId = await GrantAndEquipBagAsync(
            context,
            player.Registration.AccountId,
            player.Character.Id);
        var secondBagId = await GrantAndEquipBagAsync(
            context,
            player.Registration.AccountId,
            secondCharacter.Id);
        var firstBagContainer = await LoadBagContainerAsync(context, firstBagId);
        var secondBagContainer = await LoadBagContainerAsync(context, secondBagId);
        var firstChildId = await GrantAsync(
            context,
            player.Registration.AccountId,
            player.Character.Id,
            "ammunition.training_556",
            10,
            firstBagContainer.ContainerId,
            6);
        var secondChildId = await GrantAsync(
            context,
            player.Registration.AccountId,
            secondCharacter.Id,
            "material.iron_ore",
            2,
            secondBagContainer.ContainerId,
            5);

        var firstState = await LoadStateAsync(context, player.Character.Id);
        var secondState = await LoadStateAsync(context, secondCharacter.Id);
        var firstBag = await LoadItemAsync(context, firstBagId);
        var secondBag = await LoadItemAsync(context, secondBagId);
        firstBagContainer = await LoadBagContainerAsync(context, firstBagId);
        secondBagContainer = await LoadBagContainerAsync(context, secondBagId);
        var nonEmptyUnequip = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<UnequipItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(player.Registration.AccountId),
                new UnequipItemCommand(
                    player.Character.Id,
                    firstState.Revision,
                    firstBagId,
                    firstBag.Revision,
                    firstState.PermanentInventoryContainerId,
                    0)),
            CancellationToken.None);
        AssertRejected(nonEmptyUnequip, ItemTransactionErrorCodes.BagNotEmpty);

        var swap = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<SwapBagAggregatesCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(player.Registration.AccountId),
                new SwapBagAggregatesCommand(
                    player.Character.Id,
                    firstState.Revision,
                    firstBagId,
                    firstBag.Revision,
                    firstBagContainer.Revision,
                    secondCharacter.Id,
                    secondState.Revision,
                    secondBagId,
                    secondBag.Revision,
                    secondBagContainer.Revision)),
            CancellationToken.None);
        AssertSucceeded(swap);

        firstState = await LoadStateAsync(context, player.Character.Id);
        secondState = await LoadStateAsync(context, secondCharacter.Id);
        firstBag = await LoadItemAsync(context, firstBagId);
        secondBag = await LoadItemAsync(context, secondBagId);
        Assert.Equal(secondCharacter.Id, firstBag.EquippedCharacterId);
        Assert.Equal(player.Character.Id, secondBag.EquippedCharacterId);
        Assert.Equal(firstBagContainer.ContainerId, (await LoadItemAsync(context, firstChildId)).ContainerId);
        Assert.Equal(secondBagContainer.ContainerId, (await LoadItemAsync(context, secondChildId)).ContainerId);
        Assert.Equal(12, firstState.CarriedWeight);
        Assert.Equal(10, secondState.CarriedWeight);

        var currentFirstContainer = await LoadBagContainerAsync(context, secondBagId);
        var currentSecondContainer = await LoadBagContainerAsync(context, firstBagId);
        var failedSwap = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<SwapBagAggregatesCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(player.Registration.AccountId),
                new SwapBagAggregatesCommand(
                    player.Character.Id,
                    firstState.Revision,
                    secondBagId,
                    secondBag.Revision,
                    currentFirstContainer.Revision - 1,
                    secondCharacter.Id,
                    secondState.Revision,
                    firstBagId,
                    firstBag.Revision,
                    currentSecondContainer.Revision)),
            CancellationToken.None);
        AssertRejected(failedSwap, ItemTransactionErrorCodes.BagStateChanged);

        Assert.Equal(player.Character.Id, (await LoadItemAsync(context, secondBagId)).EquippedCharacterId);
        Assert.Equal(secondCharacter.Id, (await LoadItemAsync(context, firstBagId)).EquippedCharacterId);
        Assert.Equal(firstBagContainer.ContainerId, (await LoadItemAsync(context, firstChildId)).ContainerId);
        Assert.Equal(secondBagContainer.ContainerId, (await LoadItemAsync(context, secondChildId)).ContainerId);
        Assert.Equal(firstState, await LoadStateAsync(context, player.Character.Id));
        Assert.Equal(secondState, await LoadStateAsync(context, secondCharacter.Id));
    }

    [PostgresIntegrationFact]
    public async Task SwappingToLowerCapacityBagRejectsTheCompleteAggregateAboveHardCap()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        await AddTestBagDefinitionAsync(context, "bag.integration_light", 0);
        var player = await context.RegisterPlayerAsync(
            "lower-bag@example.com",
            "lower_bag_player",
            "Higher Capacity Hero");
        var secondCharacterResult = await context.CharacterService.CreateAsync(
            player.Registration.AccountId,
            new CreateCharacterRequest("Lower Capacity Hero"),
            CancellationToken.None);
        Assert.True(secondCharacterResult.Succeeded, secondCharacterResult.Error?.Message);
        var secondCharacter = secondCharacterResult.Value!;
        var higherBagId = await GrantAndEquipBagAsync(
            context,
            player.Registration.AccountId,
            player.Character.Id);
        var lowerBagId = await GrantAndEquipBagAsync(
            context,
            player.Registration.AccountId,
            secondCharacter.Id,
            "bag.integration_light");
        var firstState = await LoadStateAsync(context, player.Character.Id);
        await GrantAsync(
            context,
            player.Registration.AccountId,
            player.Character.Id,
            "material.iron_ore",
            50,
            firstState.PermanentInventoryContainerId,
            0);

        firstState = await LoadStateAsync(context, player.Character.Id);
        var secondState = await LoadStateAsync(context, secondCharacter.Id);
        Assert.Equal(300, firstState.CarriedWeight);
        Assert.Equal(250, firstState.CarryCapacity);
        Assert.Equal(0, secondState.CarriedWeight);
        Assert.Equal(200, secondState.CarryCapacity);
        var higherBag = await LoadItemAsync(context, higherBagId);
        var lowerBag = await LoadItemAsync(context, lowerBagId);
        var higherContainer = await LoadBagContainerAsync(context, higherBagId);
        var lowerContainer = await LoadBagContainerAsync(context, lowerBagId);

        var swap = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<SwapBagAggregatesCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(player.Registration.AccountId),
                new SwapBagAggregatesCommand(
                    player.Character.Id,
                    firstState.Revision,
                    higherBagId,
                    higherBag.Revision,
                    higherContainer.Revision,
                    secondCharacter.Id,
                    secondState.Revision,
                    lowerBagId,
                    lowerBag.Revision,
                    lowerContainer.Revision)),
            CancellationToken.None);

        AssertRejected(swap, ItemTransactionErrorCodes.CarryWeightLimitExceeded);
        var firstAfter = await LoadStateAsync(context, player.Character.Id);
        var secondAfter = await LoadStateAsync(context, secondCharacter.Id);
        Assert.Equal(firstState, firstAfter);
        Assert.Equal(secondState, secondAfter);
        Assert.Equal(player.Character.Id, (await LoadItemAsync(context, higherBagId)).EquippedCharacterId);
        Assert.Equal(secondCharacter.Id, (await LoadItemAsync(context, lowerBagId)).EquippedCharacterId);
    }

    [PostgresIntegrationFact]
    public Task BagSwapRejectsAProtectedBagWithoutChangingEitherAggregate()
    {
        return AssertPolicyRestrictedBagSwapAsync(BagSwapPolicyScenario.ProtectedBag);
    }

    [PostgresIntegrationFact]
    public Task BagSwapRejectsAnInsuredBagWithoutChangingEitherAggregate()
    {
        return AssertPolicyRestrictedBagSwapAsync(BagSwapPolicyScenario.InsuredBag);
    }

    [PostgresIntegrationFact]
    public Task BagSwapRejectsAProtectedChildWithoutChangingEitherAggregate()
    {
        return AssertPolicyRestrictedBagSwapAsync(BagSwapPolicyScenario.ProtectedChild);
    }

    [PostgresIntegrationFact]
    public Task BagSwapRejectsAnInsuredChildWithoutChangingEitherAggregate()
    {
        return AssertPolicyRestrictedBagSwapAsync(BagSwapPolicyScenario.InsuredChild);
    }

    [PostgresIntegrationFact]
    public async Task AuthorizationAndEffectivePolicyLineageAreValidatedInsideTheKernel()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var owner = await context.RegisterPlayerAsync();
        var other = await context.RegisterPlayerAsync(
            "transaction-other@example.com",
            "transaction_other",
            "Transaction Other");
        var state = await LoadStateAsync(context, owner.Character.Id);
        var unauthorized = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(other.Registration.AccountId),
                new GrantItemCommand(
                    owner.Character.Id,
                    state.Revision,
                    "material.iron_ore",
                    1,
                    state.PermanentInventoryContainerId,
                    0)),
            CancellationToken.None);
        AssertRejected(unauthorized, ItemTransactionErrorCodes.ItemNotOwned);

        var protectedStackId = await GrantAsync(
            context,
            owner.Registration.AccountId,
            owner.Character.Id,
            "ammunition.training_556",
            20,
            state.PermanentInventoryContainerId,
            0);
        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                insert into item_instance_policies (
                    id,
                    item_instance_id,
                    policy_kind,
                    source_kind,
                    source_id)
                values (
                    @PolicyId,
                    @ItemInstanceId,
                    'protected_on_death',
                    'integration_test',
                    'lineage-a');
                """,
                new { PolicyId = Guid.NewGuid(), ItemInstanceId = protectedStackId });
        }

        state = await LoadStateAsync(context, owner.Character.Id);
        var protectedStack = await LoadItemAsync(context, protectedStackId);
        var split = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<SplitItemStackCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(owner.Registration.AccountId),
                new SplitItemStackCommand(
                    owner.Character.Id,
                    state.Revision,
                    protectedStackId,
                    protectedStack.Revision,
                    5,
                    state.PermanentInventoryContainerId,
                    1)),
            CancellationToken.None);
        AssertSucceeded(split);
        var splitId = Assert.Single(
            split.ItemRevisions,
            item => item.ItemInstanceId != protectedStackId).ItemInstanceId;
        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            Assert.Equal(
                2,
                await connection.ExecuteScalarAsync<int>(
                    """
                    select count(*)
                    from item_instance_policies
                    where item_instance_id = any(@ItemIds)
                      and policy_kind = 'protected_on_death'
                      and source_kind = 'integration_test'
                      and source_id = 'lineage-a'
                      and status = 'active';
                    """,
                    new { ItemIds = new[] { protectedStackId, splitId } }));
        }

        var unprotectedStackId = await GrantAsync(
            context,
            owner.Registration.AccountId,
            owner.Character.Id,
            "ammunition.training_556",
            5,
            state.PermanentInventoryContainerId,
            2);
        state = await LoadStateAsync(context, owner.Character.Id);
        protectedStack = await LoadItemAsync(context, protectedStackId);
        var unprotectedStack = await LoadItemAsync(context, unprotectedStackId);
        var incompatibleMerge = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<MergeItemStacksCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(owner.Registration.AccountId),
                new MergeItemStacksCommand(
                    owner.Character.Id,
                    state.Revision,
                    unprotectedStackId,
                    unprotectedStack.Revision,
                    protectedStackId,
                    protectedStack.Revision)),
            CancellationToken.None);
        AssertRejected(incompatibleMerge, ItemTransactionErrorCodes.ItemStackIncompatible);

        var protectedQuestId = await GrantAsync(
            context,
            owner.Registration.AccountId,
            owner.Character.Id,
            "quest_item.signal_transponder",
            1,
            state.PermanentInventoryContainerId,
            3);
        await using var verificationConnection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            "protected_on_death",
            await verificationConnection.ExecuteScalarAsync<string>(
                """
                select policy_kind
                from item_instance_policies
                where item_instance_id = @ItemInstanceId
                  and status = 'active';
                """,
                new { ItemInstanceId = protectedQuestId }));
    }

    [PostgresIntegrationFact]
    public async Task ConcurrentMovesOfTheSameItemCommitOneFinalLocation()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var state = await LoadStateAsync(context, player.Character.Id);
        var itemId = await GrantAsync(
            context,
            player.Registration.AccountId,
            player.Character.Id,
            "material.iron_ore",
            1,
            state.PermanentInventoryContainerId,
            0);
        state = await LoadStateAsync(context, player.Character.Id);
        var item = await LoadItemAsync(context, itemId);
        var actor = ItemTransactionActor.ForAccount(player.Registration.AccountId);
        var firstRequest = new ItemTransactionRequest<RelocateItemCommand>(
            Guid.NewGuid(),
            actor,
            new RelocateItemCommand(
                player.Character.Id,
                state.Revision,
                itemId,
                item.Revision,
                state.PermanentInventoryContainerId,
                1));
        var secondRequest = new ItemTransactionRequest<RelocateItemCommand>(
            Guid.NewGuid(),
            actor,
            new RelocateItemCommand(
                player.Character.Id,
                state.Revision,
                itemId,
                item.Revision,
                state.PermanentInventoryContainerId,
                2));

        var results = await Task.WhenAll(
            context.ItemTransactionService.ExecuteAsync(firstRequest, CancellationToken.None),
            context.ItemTransactionService.ExecuteAsync(secondRequest, CancellationToken.None));

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result =>
            !result.Succeeded
            && result.Error?.Code == ItemTransactionErrorCodes.ItemStateConflict);
        var finalItem = await LoadItemAsync(context, itemId);
        Assert.Contains(finalItem.ContainerSlotIndex, new int?[] { 1, 2 });
        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_instances where id = @ItemId;",
                new { ItemId = itemId }));
    }

    [PostgresIntegrationFact]
    public async Task ConcurrentContainerAndEquipmentTargetsHaveOneWinnerEach()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var firstPlayer = await context.RegisterPlayerAsync();
        var firstState = await LoadStateAsync(context, firstPlayer.Character.Id);
        var firstItemId = await GrantAsync(
            context,
            firstPlayer.Registration.AccountId,
            firstPlayer.Character.Id,
            "material.iron_ore",
            1,
            firstState.PermanentInventoryContainerId,
            0);
        var secondItemId = await GrantAsync(
            context,
            firstPlayer.Registration.AccountId,
            firstPlayer.Character.Id,
            "medical.field_dressing",
            1,
            firstState.PermanentInventoryContainerId,
            1);
        firstState = await LoadStateAsync(context, firstPlayer.Character.Id);
        var firstItem = await LoadItemAsync(context, firstItemId);
        var secondItem = await LoadItemAsync(context, secondItemId);
        var firstActor = ItemTransactionActor.ForAccount(firstPlayer.Registration.AccountId);
        var slotResults = await Task.WhenAll(
            context.ItemTransactionService.ExecuteAsync(
                new ItemTransactionRequest<RelocateItemCommand>(
                    Guid.NewGuid(),
                    firstActor,
                    new RelocateItemCommand(
                        firstPlayer.Character.Id,
                        firstState.Revision,
                        firstItemId,
                        firstItem.Revision,
                        firstState.PermanentInventoryContainerId,
                        2)),
                CancellationToken.None),
            context.ItemTransactionService.ExecuteAsync(
                new ItemTransactionRequest<RelocateItemCommand>(
                    Guid.NewGuid(),
                    firstActor,
                    new RelocateItemCommand(
                        firstPlayer.Character.Id,
                        firstState.Revision,
                        secondItemId,
                        secondItem.Revision,
                        firstState.PermanentInventoryContainerId,
                        2)),
                CancellationToken.None));
        Assert.Single(slotResults, result => result.Succeeded);

        var secondPlayer = await context.RegisterPlayerAsync(
            "equipment@example.com",
            "equipment_player",
            "Equipment Hero");
        var equipmentState = await LoadStateAsync(context, secondPlayer.Character.Id);
        var firstRifleId = await GrantAsync(
            context,
            secondPlayer.Registration.AccountId,
            secondPlayer.Character.Id,
            "weapon.training_rifle",
            1,
            equipmentState.PermanentInventoryContainerId,
            0);
        var secondRifleId = await GrantAsync(
            context,
            secondPlayer.Registration.AccountId,
            secondPlayer.Character.Id,
            "weapon.training_rifle",
            1,
            equipmentState.PermanentInventoryContainerId,
            1);
        equipmentState = await LoadStateAsync(context, secondPlayer.Character.Id);
        var firstRifle = await LoadItemAsync(context, firstRifleId);
        var secondRifle = await LoadItemAsync(context, secondRifleId);
        var equipmentActor = ItemTransactionActor.ForAccount(secondPlayer.Registration.AccountId);
        var equipmentResults = await Task.WhenAll(
            context.ItemTransactionService.ExecuteAsync(
                new ItemTransactionRequest<EquipItemCommand>(
                    Guid.NewGuid(),
                    equipmentActor,
                    new EquipItemCommand(
                        secondPlayer.Character.Id,
                        equipmentState.Revision,
                        firstRifleId,
                        firstRifle.Revision,
                        "primary_weapon")),
                CancellationToken.None),
            context.ItemTransactionService.ExecuteAsync(
                new ItemTransactionRequest<EquipItemCommand>(
                    Guid.NewGuid(),
                    equipmentActor,
                    new EquipItemCommand(
                        secondPlayer.Character.Id,
                        equipmentState.Revision,
                        secondRifleId,
                        secondRifle.Revision,
                        "primary_weapon")),
                CancellationToken.None));
        Assert.Single(equipmentResults, result => result.Succeeded);

        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from item_instances
                where container_id = @ContainerId
                  and container_slot_index = 2;
                """,
                new { ContainerId = firstState.PermanentInventoryContainerId }));
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from item_instances
                where equipped_character_id = @CharacterId
                  and equipment_slot_id = 'primary_weapon';
                """,
                new { CharacterId = secondPlayer.Character.Id }));
    }

    [PostgresIntegrationFact]
    public async Task ConcurrentSplitAndConsumePreserveExpectedTotalQuantity()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var state = await LoadStateAsync(context, player.Character.Id);
        var itemId = await GrantAsync(
            context,
            player.Registration.AccountId,
            player.Character.Id,
            "ammunition.training_556",
            20,
            state.PermanentInventoryContainerId,
            0);
        state = await LoadStateAsync(context, player.Character.Id);
        var item = await LoadItemAsync(context, itemId);
        var actor = ItemTransactionActor.ForAccount(player.Registration.AccountId);
        var splitTask = context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<SplitItemStackCommand>(
                Guid.NewGuid(),
                actor,
                new SplitItemStackCommand(
                    player.Character.Id,
                    state.Revision,
                    itemId,
                    item.Revision,
                    8,
                    state.PermanentInventoryContainerId,
                    1)),
            CancellationToken.None);
        var consumeTask = context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<ConsumeItemQuantityCommand>(
                Guid.NewGuid(),
                actor,
                new ConsumeItemQuantityCommand(
                    player.Character.Id,
                    state.Revision,
                    itemId,
                    item.Revision,
                    7)),
            CancellationToken.None);
        var results = await Task.WhenAll(splitTask, consumeTask);
        Assert.Single(results, result => result.Succeeded);

        await using var connection = await context.DataSource.OpenConnectionAsync();
        var quantity = await connection.ExecuteScalarAsync<int>(
            """
            select coalesce(sum(quantity), 0)::integer
            from item_instances
            where definition_id = 'ammunition.training_556';
            """);
        var itemCount = await connection.ExecuteScalarAsync<int>(
            "select count(*) from item_instances where definition_id = 'ammunition.training_556';");
        if (splitTask.Result.Succeeded)
        {
            Assert.Equal(20, quantity);
            Assert.Equal(2, itemCount);
        }
        else
        {
            Assert.True(consumeTask.Result.Succeeded);
            Assert.Equal(13, quantity);
            Assert.Equal(1, itemCount);
        }
    }

    [PostgresIntegrationFact]
    public async Task OperationReplayReturnsOneResultAndDifferentPayloadConflicts()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var state = await LoadStateAsync(context, player.Character.Id);
        var operationId = Guid.NewGuid();
        var request = new ItemTransactionRequest<GrantItemCommand>(
            operationId,
            ItemTransactionActor.ForAccount(player.Registration.AccountId),
            new GrantItemCommand(
                player.Character.Id,
                state.Revision,
                "material.iron_ore",
                1,
                state.PermanentInventoryContainerId,
                0));

        var replays = await Task.WhenAll(
            context.ItemTransactionService.ExecuteAsync(request, CancellationToken.None),
            context.ItemTransactionService.ExecuteAsync(request, CancellationToken.None));
        Assert.All(replays, AssertSucceeded);
        Assert.Equal(
            Assert.Single(replays[0].ItemRevisions).ItemInstanceId,
            Assert.Single(replays[1].ItemRevisions).ItemInstanceId);

        var conflict = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                operationId,
                request.Actor,
                request.Command with { Quantity = 2 }),
            CancellationToken.None);
        AssertRejected(conflict, ItemTransactionErrorCodes.ItemOperationConflict);

        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_operations where operation_id = @OperationId;",
                new { OperationId = operationId }));
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_operation_changes where operation_id = @OperationId;",
                new { OperationId = operationId }));
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_instances where definition_id = 'material.iron_ore';"));
    }

    [PostgresIntegrationFact]
    public async Task BagAndChildCommandsWaitOnTheSameAggregateRootLock()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var secondCharacterResult = await context.CharacterService.CreateAsync(
            player.Registration.AccountId,
            new CreateCharacterRequest("Lock Partner"),
            CancellationToken.None);
        Assert.True(secondCharacterResult.Succeeded, secondCharacterResult.Error?.Message);
        var secondCharacter = secondCharacterResult.Value!;
        var firstBagId = await GrantAndEquipBagAsync(
            context,
            player.Registration.AccountId,
            player.Character.Id);
        var secondBagId = await GrantAndEquipBagAsync(
            context,
            player.Registration.AccountId,
            secondCharacter.Id);
        var firstBagContainer = await LoadBagContainerAsync(context, firstBagId);
        var childId = await GrantAsync(
            context,
            player.Registration.AccountId,
            player.Character.Id,
            "ammunition.training_556",
            5,
            firstBagContainer.ContainerId,
            6);
        var firstState = await LoadStateAsync(context, player.Character.Id);
        var child = await LoadItemAsync(context, childId);
        var childMoveRequest = new ItemTransactionRequest<RelocateItemCommand>(
            Guid.NewGuid(),
            ItemTransactionActor.ForAccount(player.Registration.AccountId),
            new RelocateItemCommand(
                player.Character.Id,
                firstState.Revision,
                childId,
                child.Revision,
                firstBagContainer.ContainerId,
                0));

        await using (var lockConnection = await context.DataSource.OpenConnectionAsync())
        await using (var lockTransaction = await lockConnection.BeginTransactionAsync())
        {
            await lockConnection.QuerySingleAsync<Guid>(
                "select id from item_instances where id = @BagId for update;",
                new { BagId = firstBagId },
                lockTransaction);
            using var cancellation = new CancellationTokenSource();
            var blockedChild = context.ItemTransactionService.ExecuteAsync(
                childMoveRequest,
                cancellation.Token);
            await AssertBlockedUntilCancelledAsync(blockedChild, cancellation);
            await lockTransaction.RollbackAsync();
        }

        AssertSucceeded(await context.ItemTransactionService.ExecuteAsync(
            childMoveRequest,
            CancellationToken.None));
        Assert.Equal(0, (await LoadItemAsync(context, childId)).ContainerSlotIndex);

        firstState = await LoadStateAsync(context, player.Character.Id);
        var secondState = await LoadStateAsync(context, secondCharacter.Id);
        var firstBag = await LoadItemAsync(context, firstBagId);
        var secondBag = await LoadItemAsync(context, secondBagId);
        firstBagContainer = await LoadBagContainerAsync(context, firstBagId);
        var secondBagContainer = await LoadBagContainerAsync(context, secondBagId);
        var swapRequest = new ItemTransactionRequest<SwapBagAggregatesCommand>(
            Guid.NewGuid(),
            ItemTransactionActor.ForAccount(player.Registration.AccountId),
            new SwapBagAggregatesCommand(
                player.Character.Id,
                firstState.Revision,
                firstBagId,
                firstBag.Revision,
                firstBagContainer.Revision,
                secondCharacter.Id,
                secondState.Revision,
                secondBagId,
                secondBag.Revision,
                secondBagContainer.Revision));

        await using (var lockConnection = await context.DataSource.OpenConnectionAsync())
        await using (var lockTransaction = await lockConnection.BeginTransactionAsync())
        {
            await lockConnection.QuerySingleAsync<Guid>(
                "select id from item_instances where id = @BagId for update;",
                new { BagId = firstBagId },
                lockTransaction);
            using var cancellation = new CancellationTokenSource();
            var blockedSwap = context.ItemTransactionService.ExecuteAsync(
                swapRequest,
                cancellation.Token);
            await AssertBlockedUntilCancelledAsync(blockedSwap, cancellation);
            await lockTransaction.RollbackAsync();
        }

        AssertSucceeded(await context.ItemTransactionService.ExecuteAsync(
            swapRequest,
            CancellationToken.None));
        Assert.Equal(secondCharacter.Id, (await LoadItemAsync(context, firstBagId)).EquippedCharacterId);
        Assert.Equal(firstBagContainer.ContainerId, (await LoadItemAsync(context, childId)).ContainerId);
    }

    private static async Task AssertBlockedUntilCancelledAsync(
        Task<ItemTransactionResult> operation,
        CancellationTokenSource cancellation)
    {
        await Task.Delay(200);
        Assert.False(operation.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
    }

    private static async Task AssertPolicyRestrictedBagSwapAsync(
        BagSwapPolicyScenario scenario)
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var secondCharacterResult = await context.CharacterService.CreateAsync(
            player.Registration.AccountId,
            new CreateCharacterRequest("Policy Bag Partner"),
            CancellationToken.None);
        Assert.True(secondCharacterResult.Succeeded, secondCharacterResult.Error?.Message);
        var secondCharacter = secondCharacterResult.Value!;
        var firstBagId = await GrantAndEquipBagAsync(
            context,
            player.Registration.AccountId,
            player.Character.Id);
        var secondBagId = await GrantAndEquipBagAsync(
            context,
            player.Registration.AccountId,
            secondCharacter.Id);
        var firstBagContainer = await LoadBagContainerAsync(context, firstBagId);
        var policyItemId = scenario is BagSwapPolicyScenario.ProtectedBag
            or BagSwapPolicyScenario.InsuredBag
                ? firstBagId
                : await GrantAsync(
                    context,
                    player.Registration.AccountId,
                    player.Character.Id,
                    "armor.starter_vest",
                    1,
                    firstBagContainer.ContainerId,
                    0);

        var policyState = await LoadStateAsync(context, player.Character.Id);
        var policyItem = await LoadItemAsync(context, policyItemId);
        ItemTransactionResult policyResult;
        if (scenario is BagSwapPolicyScenario.InsuredBag
            or BagSwapPolicyScenario.InsuredChild)
        {
            policyResult = await context.ItemPolicyService.ApplyInsuranceAsync(
                Guid.NewGuid(),
                player.Character.Id,
                policyState.Revision,
                policyItemId,
                policyItem.Revision,
                $"bag-swap-policy-{scenario}",
                CancellationToken.None);
        }
        else
        {
            policyResult = await context.ItemPolicyService.ApplyProtectedOnDeathAsync(
                Guid.NewGuid(),
                player.Character.Id,
                policyState.Revision,
                policyItemId,
                policyItem.Revision,
                ItemPolicySourceKinds.CatalogDefault,
                $"bag-swap-policy-{scenario}",
                CancellationToken.None);
        }

        AssertSucceeded(policyResult);

        var firstStateBefore = await LoadStateAsync(context, player.Character.Id);
        var secondStateBefore = await LoadStateAsync(context, secondCharacter.Id);
        var firstBagBefore = await LoadItemAsync(context, firstBagId);
        var secondBagBefore = await LoadItemAsync(context, secondBagId);
        var policyItemBefore = await LoadItemAsync(context, policyItemId);
        firstBagContainer = await LoadBagContainerAsync(context, firstBagId);
        var secondBagContainer = await LoadBagContainerAsync(context, secondBagId);

        var swap = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<SwapBagAggregatesCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(player.Registration.AccountId),
                new SwapBagAggregatesCommand(
                    player.Character.Id,
                    firstStateBefore.Revision,
                    firstBagId,
                    firstBagBefore.Revision,
                    firstBagContainer.Revision,
                    secondCharacter.Id,
                    secondStateBefore.Revision,
                    secondBagId,
                    secondBagBefore.Revision,
                    secondBagContainer.Revision)),
            CancellationToken.None);

        AssertRejected(swap, ItemTransactionErrorCodes.ItemPolicyRestricted);
        Assert.Equal(firstStateBefore, await LoadStateAsync(context, player.Character.Id));
        Assert.Equal(secondStateBefore, await LoadStateAsync(context, secondCharacter.Id));
        var firstBagAfter = await LoadItemAsync(context, firstBagId);
        var secondBagAfter = await LoadItemAsync(context, secondBagId);
        var policyItemAfter = await LoadItemAsync(context, policyItemId);
        Assert.Equal(firstBagBefore.Revision, firstBagAfter.Revision);
        Assert.Equal(firstBagBefore.EquippedCharacterId, firstBagAfter.EquippedCharacterId);
        Assert.Equal(secondBagBefore.Revision, secondBagAfter.Revision);
        Assert.Equal(secondBagBefore.EquippedCharacterId, secondBagAfter.EquippedCharacterId);
        Assert.Equal(policyItemBefore.Revision, policyItemAfter.Revision);
        Assert.Equal(policyItemBefore.Quantity, policyItemAfter.Quantity);
        Assert.Equal(policyItemBefore.ContainerId, policyItemAfter.ContainerId);
        Assert.Equal(policyItemBefore.EquippedCharacterId, policyItemAfter.EquippedCharacterId);
        Assert.Equal(
            firstBagContainer.Revision,
            (await LoadBagContainerAsync(context, firstBagId)).Revision);
        Assert.Equal(
            secondBagContainer.Revision,
            (await LoadBagContainerAsync(context, secondBagId)).Revision);
    }

    private static async Task<Guid> GrantAsync(
        PostgresIntegrationTestContext context,
        Guid accountId,
        Guid characterId,
        string definitionId,
        int quantity,
        Guid destinationContainerId,
        int? destinationSlotIndex)
    {
        var state = await LoadStateAsync(context, characterId);
        var result = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(accountId),
                new GrantItemCommand(
                    characterId,
                    state.Revision,
                    definitionId,
                    quantity,
                    destinationContainerId,
                    destinationSlotIndex)),
            CancellationToken.None);
        AssertSucceeded(result);
        return Assert.Single(result.ItemRevisions).ItemInstanceId;
    }

    private static async Task<Guid> GrantAndEquipBagAsync(
        PostgresIntegrationTestContext context,
        Guid accountId,
        Guid characterId,
        string definitionId = "bag.field_pack")
    {
        var state = await LoadStateAsync(context, characterId);
        var bagId = await GrantAsync(
            context,
            accountId,
            characterId,
            definitionId,
            1,
            state.PermanentInventoryContainerId,
            0);
        state = await LoadStateAsync(context, characterId);
        var bag = await LoadItemAsync(context, bagId);
        var equip = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<EquipItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(accountId),
                new EquipItemCommand(
                    characterId,
                    state.Revision,
                    bagId,
                    bag.Revision,
                    "bag")),
            CancellationToken.None);
        AssertSucceeded(equip);
        return bagId;
    }

    private static async Task AddTestBagDefinitionAsync(
        PostgresIntegrationTestContext context,
        string definitionId,
        long carryCapacityBonus)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into item_definitions (
                id,
                catalog_id,
                catalog_revision,
                display_name,
                category_id,
                unit_weight,
                maximum_stack_size,
                player_destroyable,
                structural_fingerprint)
            select
                @DefinitionId,
                catalog_id,
                catalog_revision,
                'Integration Test Bag',
                category_id,
                unit_weight,
                maximum_stack_size,
                player_destroyable,
                repeat('c', 64)
            from item_definitions
            where id = 'bag.field_pack';

            insert into item_definition_equipment_slots (definition_id, equipment_slot_id)
            values (@DefinitionId, 'bag');

            insert into item_definition_location_rules (definition_id, location_kind, is_allowed)
            values (@DefinitionId, 'secure_container', false);

            insert into bag_definitions (definition_id, carry_capacity_bonus)
            values (@DefinitionId, @CarryCapacityBonus);

            insert into bag_definition_slots (definition_id, slot_index, slot_kind)
            select @DefinitionId, slot_index, slot_kind
            from bag_definition_slots
            where definition_id = 'bag.field_pack';

            insert into bag_definition_slot_tags (definition_id, slot_index, tag_id)
            select @DefinitionId, slot_index, tag_id
            from bag_definition_slot_tags
            where definition_id = 'bag.field_pack';
            """,
            new { DefinitionId = definitionId, CarryCapacityBonus = carryCapacityBonus });
    }

    private static async Task AddTestSecureTierAsync(
        PostgresIntegrationTestContext context,
        string tierId,
        int slotCapacity)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into secure_container_tiers (
                id,
                catalog_id,
                catalog_revision,
                display_name,
                slot_capacity,
                structural_fingerprint)
            select
                @TierId,
                catalog_id,
                revision,
                'Integration Test Secure Container',
                @SlotCapacity,
                repeat('b', 64)
            from item_catalog_revisions
            where is_current;
            """,
            new { TierId = tierId, SlotCapacity = slotCapacity });
    }

    private static async Task<CharacterItemStateRow> LoadStateAsync(
        PostgresIntegrationTestContext context,
        Guid characterId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<CharacterItemStateRow>(
            """
            select
                character_id as "CharacterId",
                revision as "Revision",
                carried_weight as "CarriedWeight",
                carry_capacity as "CarryCapacity",
                permanent_inventory_container_id as "PermanentInventoryContainerId",
                bank_container_id as "BankContainerId",
                secure_container_id as "SecureContainerId",
                recovery_storage_container_id as "RecoveryStorageContainerId"
            from character_item_states
            where character_id = @CharacterId;
            """,
            new { CharacterId = characterId });
    }

    private static async Task<ItemRow> LoadItemAsync(
        PostgresIntegrationTestContext context,
        Guid itemInstanceId)
    {
        return await TryLoadItemAsync(context, itemInstanceId)
            ?? throw new InvalidOperationException($"Item '{itemInstanceId}' was not found.");
    }

    private static async Task<ItemRow?> TryLoadItemAsync(
        PostgresIntegrationTestContext context,
        Guid itemInstanceId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<ItemRow>(
            """
            select
                id as "ItemInstanceId",
                definition_id as "DefinitionId",
                quantity as "Quantity",
                revision as "Revision",
                container_id as "ContainerId",
                container_slot_index as "ContainerSlotIndex",
                equipped_character_id as "EquippedCharacterId",
                equipment_slot_id as "EquipmentSlotId"
            from item_instances
            where id = @ItemInstanceId;
            """,
            new { ItemInstanceId = itemInstanceId });
    }

    private static async Task<BagContainerRow> LoadBagContainerAsync(
        PostgresIntegrationTestContext context,
        Guid bagItemInstanceId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<BagContainerRow>(
            """
            select
                id as "ContainerId",
                revision as "Revision",
                lifecycle as "Lifecycle"
            from item_containers
            where bound_bag_item_instance_id = @BagItemInstanceId;
            """,
            new { BagItemInstanceId = bagItemInstanceId });
    }

    private static async Task<int> LoadContainerCapacityAsync(
        PostgresIntegrationTestContext context,
        Guid containerId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            "select slot_capacity from item_containers where id = @ContainerId;",
            new { ContainerId = containerId });
    }

    private static void AssertSucceeded(ItemTransactionResult result)
    {
        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Null(result.Error);
    }

    private static void AssertRejected(ItemTransactionResult result, string expectedCode)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.Error?.Code);
    }

    private sealed record CharacterItemStateRow
    {
        public Guid CharacterId { get; init; }

        public long Revision { get; init; }

        public long CarriedWeight { get; init; }

        public long CarryCapacity { get; init; }

        public Guid PermanentInventoryContainerId { get; init; }

        public Guid BankContainerId { get; init; }

        public Guid SecureContainerId { get; init; }

        public Guid RecoveryStorageContainerId { get; init; }
    }

    private sealed class ItemRow
    {
        public Guid ItemInstanceId { get; set; }

        public string DefinitionId { get; set; } = string.Empty;

        public int Quantity { get; set; }

        public long Revision { get; set; }

        public Guid? ContainerId { get; set; }

        public int? ContainerSlotIndex { get; set; }

        public Guid? EquippedCharacterId { get; set; }

        public string? EquipmentSlotId { get; set; }
    }

    private sealed class BagContainerRow
    {
        public Guid ContainerId { get; set; }

        public long Revision { get; set; }

        public string Lifecycle { get; set; } = string.Empty;
    }

    private sealed class DeliveryOrderRow
    {
        public Guid ItemInstanceId { get; set; }

        public int ItemOrder { get; set; }
    }

    private enum BagSwapPolicyScenario
    {
        ProtectedBag,
        InsuredBag,
        ProtectedChild,
        InsuredChild
    }
}
