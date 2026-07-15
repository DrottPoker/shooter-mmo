using AuthService.Auth;
using AuthService.Characters;
using AuthService.Items;
using Dapper;
using Npgsql;
using ShooterMmo.WorldData.Items;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class ItemPersistenceIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task CatalogMirrorIsCompleteTransactionalAndIdempotent()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var catalog = await ItemCatalogRuntimeLoader.LoadAsync(
            context.CatalogSource.RuntimeCatalogPath,
            CancellationToken.None);

        await using var connection = await context.DataSource.OpenConnectionAsync();

        Assert.Equal(
            catalog.Revision,
            await connection.ExecuteScalarAsync<string>(
                "select revision from item_catalog_revisions where is_current;"));
        Assert.Equal(
            catalog.Categories.Length,
            await connection.ExecuteScalarAsync<int>("select count(*) from item_categories;"));
        Assert.Equal(
            catalog.Tags.Length,
            await connection.ExecuteScalarAsync<int>("select count(*) from item_tags;"));
        Assert.Equal(
            catalog.EquipmentSlots.Length,
            await connection.ExecuteScalarAsync<int>("select count(*) from equipment_slots;"));
        Assert.Equal(
            catalog.Definitions.Length,
            await connection.ExecuteScalarAsync<int>("select count(*) from item_definitions;"));
        Assert.Equal(
            catalog.Definitions.Sum(definition => definition.Tags.Length),
            await connection.ExecuteScalarAsync<int>("select count(*) from item_definition_tags;"));
        Assert.Equal(
            catalog.Definitions.Sum(definition => definition.EquipmentSlots.Length),
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_definition_equipment_slots;"));
        Assert.Equal(
            catalog.Definitions.Length,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_definition_location_rules;"));
        Assert.Equal(
            catalog.Definitions.Sum(definition => definition.DefaultPolicies.Length),
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_definition_default_policies;"));
        Assert.Equal(
            catalog.Definitions.Count(definition => definition.Bag is not null),
            await connection.ExecuteScalarAsync<int>("select count(*) from bag_definitions;"));
        Assert.Equal(
            catalog.Definitions.Sum(definition => definition.Bag?.Slots.Length ?? 0),
            await connection.ExecuteScalarAsync<int>("select count(*) from bag_definition_slots;"));
        Assert.Equal(
            catalog.Definitions.Sum(definition =>
                definition.Bag?.Slots.Sum(slot => slot.AcceptedTags.Length) ?? 0),
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from bag_definition_slot_tags;"));
        Assert.Equal(
            catalog.SecureContainerTiers.Length,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from secure_container_tiers;"));

        await connection.ExecuteAsync(
            """
            delete from item_definition_tags
            where definition_id = 'material.iron_ore'
              and tag_id = 'material';
            """);
        await context.ItemCatalogSeeder.SeedAsync(CancellationToken.None);

        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_catalog_revisions;"));
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_catalog_revisions where is_current;"));
        Assert.Equal(
            catalog.Definitions.Sum(definition => definition.Tags.Length),
            await connection.ExecuteScalarAsync<int>("select count(*) from item_definition_tags;"));
    }

    [PostgresIntegrationFact]
    public async Task StartupBackfillCreatesCompleteItemStateAndIsIdempotent()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var accountId = Guid.NewGuid();
        var characterId = Guid.NewGuid();

        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                insert into accounts (
                    id,
                    email,
                    normalized_email,
                    username,
                    normalized_username,
                    password_hash)
                values (
                    @AccountId,
                    'legacy@example.com',
                    'LEGACY@EXAMPLE.COM',
                    'legacy_player',
                    'LEGACY_PLAYER',
                    'legacy-hash');

                insert into characters (id, account_id, name, normalized_name)
                values (@CharacterId, @AccountId, 'Legacy Hero', 'LEGACY HERO');
                """,
                new { AccountId = accountId, CharacterId = characterId });
        }

        await context.DatabaseInitializer.InitializeAsync(CancellationToken.None);
        var first = await LoadBootstrapSnapshotAsync(context, characterId);

        Assert.Equal(0, first.Revision);
        Assert.Equal(0, first.CarriedWeight);
        Assert.Equal(200, first.BaseCarryCapacity);
        Assert.Equal(200, first.CarryCapacity);
        Assert.Equal(20, first.PermanentInventoryCapacity);
        Assert.Equal(40, first.BankCapacity);
        Assert.Equal(4, first.SecureContainerCapacity);
        Assert.Null(first.RecoveryStorageCapacity);
        Assert.Equal(20, first.PermanentInventorySlots);
        Assert.Equal(40, first.BankSlots);
        Assert.Equal(4, first.SecureContainerSlots);
        Assert.Equal(0, first.RecoveryStorageSlots);
        Assert.Equal("secure_container.base", first.SecureContainerTierId);

        await context.DatabaseInitializer.InitializeAsync(CancellationToken.None);
        var second = await LoadBootstrapSnapshotAsync(context, characterId);

        Assert.Equal(first, second);

        await using var verificationConnection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            4,
            await verificationConnection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from item_containers
                where owner_character_id = @CharacterId
                  and lifecycle = 'active'
                  and container_type in (
                      'permanent_inventory',
                      'bank',
                      'secure_container',
                      'recovery_storage');
                """,
                new { CharacterId = characterId }));
        Assert.Equal(
            0,
            await verificationConnection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from (
                    select owner_character_id, container_type
                    from item_containers
                    where owner_character_id is not null
                      and lifecycle = 'active'
                    group by owner_character_id, container_type
                    having count(*) <> 1
                ) duplicates;
                """));
        Assert.Equal(
            0,
            await verificationConnection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from characters character
                left join character_item_states state on state.character_id = character.id
                where character.deleted_at is null
                  and state.character_id is null;
                """));
    }

    [PostgresIntegrationFact]
    public async Task CharacterCreationCommitsCharacterAndCompleteItemStateTogether()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var snapshot = await LoadBootstrapSnapshotAsync(context, player.Character.Id);

        Assert.Equal(200, snapshot.BaseCarryCapacity);
        Assert.Equal(200, snapshot.CarryCapacity);
        Assert.Equal(20, snapshot.PermanentInventorySlots);
        Assert.Equal(40, snapshot.BankSlots);
        Assert.Equal(4, snapshot.SecureContainerSlots);
        Assert.Equal(0, snapshot.RecoveryStorageSlots);
    }

    [PostgresIntegrationFact]
    public async Task CharacterCreationRollsBackWhenItemBootstrapCannotComplete()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var registration = await context.AccountService.RegisterAsync(
            new RegisterAccountRequest(
                "rollback@example.com",
                "rollback_player",
                "TestPass123!"),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Error?.Message);

        await context.ExecuteAsync(
            "update item_catalog_revisions set is_current = false where is_current;");

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            context.CharacterService.CreateAsync(
                registration.Value!.AccountId,
                new CreateCharacterRequest("Rollback Hero"),
                CancellationToken.None));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(
            0,
            await context.ExecuteScalarIntAsync(
                "select count(*) from characters where name = 'Rollback Hero';"));
    }

    [PostgresIntegrationFact]
    public async Task DuplicateEquipmentSlotContainerSlotBagBindingAndOperationIdAreRejected()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var inventoryId = await connection.ExecuteScalarAsync<Guid>(
            """
            select permanent_inventory_container_id
            from character_item_states
            where character_id = @CharacterId;
            """,
            new { CharacterId = player.Character.Id });

        await connection.ExecuteAsync(
            """
            insert into item_instances (
                id,
                definition_id,
                quantity,
                equipped_character_id,
                equipment_slot_id)
            values (
                @ItemId,
                'weapon.training_rifle',
                1,
                @CharacterId,
                'primary_weapon');
            """,
            new { ItemId = Guid.NewGuid(), CharacterId = player.Character.Id });
        var duplicateEquipment = await Assert.ThrowsAsync<PostgresException>(() =>
            connection.ExecuteAsync(
                """
                insert into item_instances (
                    id,
                    definition_id,
                    quantity,
                    equipped_character_id,
                    equipment_slot_id)
                values (
                    @ItemId,
                    'weapon.training_rifle',
                    1,
                    @CharacterId,
                    'primary_weapon');
                """,
                new { ItemId = Guid.NewGuid(), CharacterId = player.Character.Id }));
        AssertConstraint(duplicateEquipment, "ux_item_instances_equipment_slot");

        await connection.ExecuteAsync(
            """
            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (@ItemId, 'material.iron_ore', 1, @ContainerId, 0);
            """,
            new { ItemId = Guid.NewGuid(), ContainerId = inventoryId });
        var duplicateContainerSlot = await Assert.ThrowsAsync<PostgresException>(() =>
            connection.ExecuteAsync(
                """
                insert into item_instances (
                    id,
                    definition_id,
                    quantity,
                    container_id,
                    container_slot_index)
                values (@ItemId, 'medical.field_dressing', 1, @ContainerId, 0);
                """,
                new { ItemId = Guid.NewGuid(), ContainerId = inventoryId }));
        AssertConstraint(duplicateContainerSlot, "ux_item_instances_container_slot");

        var bagItemId = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (@ItemId, 'bag.field_pack', 1, @ContainerId, 1);
            """,
            new { ItemId = bagItemId, ContainerId = inventoryId });
        await connection.ExecuteAsync(
            """
            insert into item_containers (
                id,
                container_type,
                bound_bag_item_instance_id,
                slot_capacity)
            values (@ContainerId, 'bag_contents', @BagItemId, 7);
            """,
            new { ContainerId = Guid.NewGuid(), BagItemId = bagItemId });
        var duplicateBagBinding = await Assert.ThrowsAsync<PostgresException>(() =>
            connection.ExecuteAsync(
                """
                insert into item_containers (
                    id,
                    container_type,
                    bound_bag_item_instance_id,
                    slot_capacity)
                values (@ContainerId, 'bag_contents', @BagItemId, 7);
                """,
                new { ContainerId = Guid.NewGuid(), BagItemId = bagItemId }));
        AssertConstraint(duplicateBagBinding, "ux_item_containers_bound_bag");

        var operationId = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            insert into item_operations (
                operation_id,
                actor_account_id,
                actor_character_id,
                operation_kind,
                request_hash,
                request_payload)
            values (
                @OperationId,
                @AccountId,
                @CharacterId,
                'test',
                @RequestHash,
                '{}'::jsonb);
            """,
            new
            {
                OperationId = operationId,
                AccountId = player.Registration.AccountId,
                CharacterId = player.Character.Id,
                RequestHash = new string('0', 64)
            });
        var duplicateOperation = await Assert.ThrowsAsync<PostgresException>(() =>
            connection.ExecuteAsync(
                """
                insert into item_operations (
                    operation_id,
                    operation_kind,
                    request_hash,
                    request_payload)
                values (@OperationId, 'test', @RequestHash, '{}'::jsonb);
                """,
                new
                {
                    OperationId = operationId,
                    RequestHash = new string('1', 64)
                }));
        AssertConstraint(duplicateOperation, "item_operations_pkey");
    }

    [PostgresIntegrationFact]
    public async Task DeferrableOccupancyConstraintsSupportAtomicSlotAndEquipmentSwaps()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var inventoryId = await connection.ExecuteScalarAsync<Guid>(
            """
            select permanent_inventory_container_id
            from character_item_states
            where character_id = @CharacterId;
            """,
            new { CharacterId = player.Character.Id });
        var firstContainerItemId = Guid.NewGuid();
        var secondContainerItemId = Guid.NewGuid();
        var firstEquipmentItemId = Guid.NewGuid();
        var secondEquipmentItemId = Guid.NewGuid();

        await connection.ExecuteAsync(
            """
            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values
                (@FirstContainerItemId, 'material.iron_ore', 1, @ContainerId, 0),
                (@SecondContainerItemId, 'medical.field_dressing', 1, @ContainerId, 1);

            insert into item_instances (
                id,
                definition_id,
                quantity,
                equipped_character_id,
                equipment_slot_id)
            values
                (@FirstEquipmentItemId, 'weapon.training_rifle', 1, @CharacterId, 'primary_weapon'),
                (@SecondEquipmentItemId, 'weapon.training_rifle', 1, @CharacterId, 'secondary_weapon');
            """,
            new
            {
                FirstContainerItemId = firstContainerItemId,
                SecondContainerItemId = secondContainerItemId,
                FirstEquipmentItemId = firstEquipmentItemId,
                SecondEquipmentItemId = secondEquipmentItemId,
                ContainerId = inventoryId,
                CharacterId = player.Character.Id
            });

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync(
                """
                set constraints
                    ux_item_instances_container_slot,
                    ux_item_instances_equipment_slot
                deferred;
                """,
                transaction: transaction);
            await connection.ExecuteAsync(
                """
                update item_instances
                set container_slot_index = 1
                where id = @FirstItemId;

                update item_instances
                set container_slot_index = 0
                where id = @SecondItemId;

                update item_instances
                set equipment_slot_id = 'secondary_weapon'
                where id = @FirstEquipmentItemId;

                update item_instances
                set equipment_slot_id = 'primary_weapon'
                where id = @SecondEquipmentItemId;
                """,
                new
                {
                    FirstItemId = firstContainerItemId,
                    SecondItemId = secondContainerItemId,
                    FirstEquipmentItemId = firstEquipmentItemId,
                    SecondEquipmentItemId = secondEquipmentItemId
                },
                transaction);
            await transaction.CommitAsync();
        }

        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select container_slot_index from item_instances where id = @ItemId;",
                new { ItemId = firstContainerItemId }));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select container_slot_index from item_instances where id = @ItemId;",
                new { ItemId = secondContainerItemId }));
        Assert.Equal(
            "secondary_weapon",
            await connection.ExecuteScalarAsync<string>(
                "select equipment_slot_id from item_instances where id = @ItemId;",
                new { ItemId = firstEquipmentItemId }));
        Assert.Equal(
            "primary_weapon",
            await connection.ExecuteScalarAsync<string>(
                "select equipment_slot_id from item_instances where id = @ItemId;",
                new { ItemId = secondEquipmentItemId }));
    }

    [PostgresIntegrationFact]
    public async Task InvalidLocationUnionAndNegativeRevisionsAreRejected()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var inventoryId = await connection.ExecuteScalarAsync<Guid>(
            """
            select permanent_inventory_container_id
            from character_item_states
            where character_id = @CharacterId;
            """,
            new { CharacterId = player.Character.Id });

        var missingLocation = await Assert.ThrowsAsync<PostgresException>(() =>
            connection.ExecuteAsync(
                """
                insert into item_instances (id, definition_id, quantity)
                values (@ItemId, 'material.iron_ore', 1);
                """,
                new { ItemId = Guid.NewGuid() }));
        AssertCheckConstraint(missingLocation, "ck_item_instances_location_union");

        var duplicateLocation = await Assert.ThrowsAsync<PostgresException>(() =>
            connection.ExecuteAsync(
                """
                insert into item_instances (
                    id,
                    definition_id,
                    quantity,
                    container_id,
                    container_slot_index,
                    equipped_character_id,
                    equipment_slot_id)
                values (
                    @ItemId,
                    'weapon.training_rifle',
                    1,
                    @ContainerId,
                    0,
                    @CharacterId,
                    'primary_weapon');
                """,
                new
                {
                    ItemId = Guid.NewGuid(),
                    ContainerId = inventoryId,
                    CharacterId = player.Character.Id
                }));
        AssertCheckConstraint(duplicateLocation, "ck_item_instances_location_union");

        var negativeItemRevision = await Assert.ThrowsAsync<PostgresException>(() =>
            connection.ExecuteAsync(
                """
                insert into item_instances (
                    id,
                    definition_id,
                    quantity,
                    revision,
                    container_id,
                    container_slot_index)
                values (@ItemId, 'material.iron_ore', 1, -1, @ContainerId, 0);
                """,
                new { ItemId = Guid.NewGuid(), ContainerId = inventoryId }));
        AssertCheckConstraint(negativeItemRevision, "ck_item_instances_revision");

        var negativeStateRevision = await Assert.ThrowsAsync<PostgresException>(() =>
            connection.ExecuteAsync(
                """
                update character_item_states
                set revision = -1
                where character_id = @CharacterId;
                """,
                new { CharacterId = player.Character.Id }));
        AssertCheckConstraint(negativeStateRevision, "ck_character_item_states_revision");
    }

    [PostgresIntegrationFact]
    public async Task SchemaRepresentsCharacterBagRecoveryEquipmentAndCorpseCustody()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var containers = await connection.QuerySingleAsync<CharacterContainerIds>(
            """
            select
                permanent_inventory_container_id as "PermanentInventoryId",
                recovery_storage_container_id as "RecoveryStorageId"
            from character_item_states
            where character_id = @CharacterId;
            """,
            new { CharacterId = player.Character.Id });

        var bagItemId = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (@BagItemId, 'bag.field_pack', 1, @InventoryId, 0);
            """,
            new { BagItemId = bagItemId, InventoryId = containers.PermanentInventoryId });
        var bagContainerId = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            insert into item_containers (
                id,
                container_type,
                bound_bag_item_instance_id,
                slot_capacity)
            values (@ContainerId, 'bag_contents', @BagItemId, 7);

            insert into item_container_slots (container_id, slot_index, slot_kind)
            values (@ContainerId, 0, 'general');

            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (@ChildItemId, 'medical.field_dressing', 1, @ContainerId, 0);
            """,
            new
            {
                ContainerId = bagContainerId,
                BagItemId = bagItemId,
                ChildItemId = Guid.NewGuid()
            });

        var recoveryItemId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            insert into item_container_slots (container_id, slot_index, slot_kind)
            values (@RecoveryContainerId, 0, 'general');

            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (@RecoveryItemId, 'quest_item.signal_transponder', 1, @RecoveryContainerId, 0);

            insert into recovery_deliveries (
                id,
                character_id,
                recovery_storage_container_id,
                source_kind,
                source_event_id)
            values (
                @DeliveryId,
                @CharacterId,
                @RecoveryContainerId,
                'system_restoration',
                'representation-test');

            insert into recovery_delivery_items (
                recovery_delivery_id,
                item_instance_id,
                item_order)
            values (@DeliveryId, @RecoveryItemId, 0);
            """,
            new
            {
                RecoveryContainerId = containers.RecoveryStorageId,
                RecoveryItemId = recoveryItemId,
                DeliveryId = deliveryId,
                CharacterId = player.Character.Id
            });

        await connection.ExecuteAsync(
            """
            insert into item_instances (
                id,
                definition_id,
                quantity,
                equipped_character_id,
                equipment_slot_id)
            values (@ItemId, 'tool.starter_pickaxe', 1, @CharacterId, 'tool');
            """,
            new { ItemId = Guid.NewGuid(), CharacterId = player.Character.Id });

        var corpseContainerId = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            insert into item_containers (id, container_type, slot_capacity)
            values (@ContainerId, 'corpse_inventory', 1);

            insert into item_container_slots (container_id, slot_index, slot_kind)
            values (@ContainerId, 0, 'general');

            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (@ItemId, 'material.iron_ore', 1, @ContainerId, 0);
            """,
            new { ContainerId = corpseContainerId, ItemId = Guid.NewGuid() });

        Assert.Equal(
            5,
            await connection.ExecuteScalarAsync<int>("select count(*) from item_instances;"));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from item_instances
                where not (
                    (container_id is not null
                     and container_slot_index is not null
                     and equipped_character_id is null
                     and equipment_slot_id is null)
                    or
                    (container_id is null
                     and container_slot_index is null
                     and equipped_character_id is not null
                     and equipment_slot_id is not null));
                """));
    }

    [PostgresIntegrationFact]
    public async Task CharacterAndAccountDeletionFollowExplicitForeignKeyRules()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var containers = await connection.QuerySingleAsync<CharacterContainerIds>(
            """
            select
                permanent_inventory_container_id as "PermanentInventoryId",
                recovery_storage_container_id as "RecoveryStorageId"
            from character_item_states
            where character_id = @CharacterId;
            """,
            new { CharacterId = player.Character.Id });
        var itemId = Guid.NewGuid();
        var recoveryItemId = Guid.NewGuid();
        var bagItemId = Guid.NewGuid();
        var bagContainerId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        await connection.ExecuteAsync(
            """
            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (@ItemId, 'material.iron_ore', 1, @InventoryId, 0);

            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (@BagItemId, 'bag.field_pack', 1, @InventoryId, 1);

            insert into item_containers (
                id,
                container_type,
                bound_bag_item_instance_id,
                slot_capacity)
            values (@BagContainerId, 'bag_contents', @BagItemId, 7);

            insert into item_container_slots (container_id, slot_index, slot_kind)
            values (@BagContainerId, 0, 'general');

            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (@BagChildItemId, 'ammunition.training_556', 1, @BagContainerId, 0);

            insert into item_instances (
                id,
                definition_id,
                quantity,
                equipped_character_id,
                equipment_slot_id)
            values (@EquippedItemId, 'tool.starter_pickaxe', 1, @CharacterId, 'tool');

            insert into item_container_slots (container_id, slot_index, slot_kind)
            values (@RecoveryId, 0, 'general');

            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (@RecoveryItemId, 'medical.field_dressing', 1, @RecoveryId, 0);

            insert into recovery_deliveries (
                id,
                character_id,
                recovery_storage_container_id,
                source_kind,
                source_event_id)
            values (
                @DeliveryId,
                @CharacterId,
                @RecoveryId,
                'system_restoration',
                'delete-test');

            insert into recovery_delivery_items (
                recovery_delivery_id,
                item_instance_id,
                item_order)
            values (@DeliveryId, @RecoveryItemId, 0);

            insert into item_operations (
                operation_id,
                actor_account_id,
                actor_character_id,
                operation_kind,
                request_hash,
                request_payload)
            values (
                @OperationId,
                @AccountId,
                @CharacterId,
                'delete_test',
                @RequestHash,
                '{}'::jsonb);

            insert into item_operation_changes (
                operation_id,
                change_index,
                item_instance_id,
                change_kind,
                before_state)
            values (
                @OperationId,
                0,
                @ItemId,
                'delete_test',
                '{}'::jsonb);
            """,
            new
            {
                ItemId = itemId,
                RecoveryItemId = recoveryItemId,
                BagItemId = bagItemId,
                BagContainerId = bagContainerId,
                BagChildItemId = Guid.NewGuid(),
                EquippedItemId = Guid.NewGuid(),
                InventoryId = containers.PermanentInventoryId,
                RecoveryId = containers.RecoveryStorageId,
                DeliveryId = deliveryId,
                CharacterId = player.Character.Id,
                AccountId = player.Registration.AccountId,
                OperationId = operationId,
                RequestHash = new string('a', 64)
            });

        await connection.ExecuteAsync(
            "delete from characters where id = @CharacterId;",
            new { CharacterId = player.Character.Id });

        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from character_item_states where character_id = @CharacterId;",
                new { CharacterId = player.Character.Id }));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_containers;"));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_instances;"));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from recovery_deliveries where id = @DeliveryId;",
                new { DeliveryId = deliveryId }));
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from account_secure_container_entitlements
                where account_id = @AccountId;
                """,
                new { AccountId = player.Registration.AccountId }));

        var afterCharacterDelete = await connection.QuerySingleAsync<OperationActorRow>(
            """
            select
                actor_account_id as "AccountId",
                actor_character_id as "CharacterId"
            from item_operations
            where operation_id = @OperationId;
            """,
            new { OperationId = operationId });
        Assert.Equal(player.Registration.AccountId, afterCharacterDelete.AccountId);
        Assert.Null(afterCharacterDelete.CharacterId);
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_operation_changes where operation_id = @OperationId;",
                new { OperationId = operationId }));
        Assert.Null(await connection.ExecuteScalarAsync<Guid?>(
            """
            select item_instance_id
            from item_operation_changes
            where operation_id = @OperationId;
            """,
            new { OperationId = operationId }));

        await connection.ExecuteAsync(
            "delete from accounts where id = @AccountId;",
            new { AccountId = player.Registration.AccountId });

        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from account_secure_container_entitlements
                where account_id = @AccountId;
                """,
                new { AccountId = player.Registration.AccountId }));
        var afterAccountDelete = await connection.QuerySingleAsync<OperationActorRow>(
            """
            select
                actor_account_id as "AccountId",
                actor_character_id as "CharacterId"
            from item_operations
            where operation_id = @OperationId;
            """,
            new { OperationId = operationId });
        Assert.Null(afterAccountDelete.AccountId);
        Assert.Null(afterAccountDelete.CharacterId);
    }

    [PostgresIntegrationFact]
    public async Task StructuralCatalogChangeWithLiveDataFailsWithoutChangingCurrentMirror()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var catalog = await ItemCatalogRuntimeLoader.LoadAsync(
            context.CatalogSource.RuntimeCatalogPath,
            CancellationToken.None);
        var sourceDefinition = catalog.Definitions.Single(
            definition => definition.Id == "material.iron_ore");
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var inventoryId = await connection.ExecuteScalarAsync<Guid>(
            """
            select permanent_inventory_container_id
            from character_item_states
            where character_id = @CharacterId;
            """,
            new { CharacterId = player.Character.Id });
        await connection.ExecuteAsync(
            """
            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (@ItemId, @DefinitionId, 1, @ContainerId, 0);
            """,
            new
            {
                ItemId = Guid.NewGuid(),
                DefinitionId = sourceDefinition.Id,
                ContainerId = inventoryId
            });

        var displayOnlyCatalog = CompileCatalogVariant(
            catalog,
            sourceDefinition.Id,
            sourceDefinition.DisplayName + " Display Test",
            sourceDefinition.UnitWeight);
        var displayOnlyDefinition = displayOnlyCatalog.Definitions.Single(
            definition => definition.Id == sourceDefinition.Id);
        Assert.Equal(
            sourceDefinition.StructuralFingerprint,
            displayOnlyDefinition.StructuralFingerprint);

        await context.ItemCatalogSeeder.SeedAsync(displayOnlyCatalog, CancellationToken.None);
        Assert.Equal(
            displayOnlyCatalog.Revision,
            await connection.ExecuteScalarAsync<string>(
                "select revision from item_catalog_revisions where is_current;"));

        var structuralCatalog = CompileCatalogVariant(
            displayOnlyCatalog,
            sourceDefinition.Id,
            displayOnlyDefinition.DisplayName,
            sourceDefinition.UnitWeight + 1);
        var exception = await Assert.ThrowsAsync<ItemCatalogCompatibilityException>(() =>
            context.ItemCatalogSeeder.SeedAsync(structuralCatalog, CancellationToken.None));

        Assert.Contains(sourceDefinition.Id, exception.Message, StringComparison.Ordinal);
        Assert.Contains("explicit data migration", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            displayOnlyCatalog.Revision,
            await connection.ExecuteScalarAsync<string>(
                "select revision from item_catalog_revisions where is_current;"));
        Assert.Equal(
            sourceDefinition.UnitWeight,
            await connection.ExecuteScalarAsync<long>(
                "select unit_weight from item_definitions where id = @DefinitionId;",
                new { DefinitionId = sourceDefinition.Id }));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from item_catalog_revisions
                where revision = @Revision;
                """,
                new { structuralCatalog.Revision }));
    }

    private static async Task<BootstrapSnapshot> LoadBootstrapSnapshotAsync(
        PostgresIntegrationTestContext context,
        Guid characterId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<BootstrapSnapshot>(
            """
            select
                state.revision as "Revision",
                state.carried_weight as "CarriedWeight",
                state.base_carry_capacity as "BaseCarryCapacity",
                state.carry_capacity as "CarryCapacity",
                inventory.id as "PermanentInventoryId",
                bank.id as "BankId",
                secure.id as "SecureContainerId",
                recovery.id as "RecoveryStorageId",
                inventory.slot_capacity as "PermanentInventoryCapacity",
                bank.slot_capacity as "BankCapacity",
                secure.slot_capacity as "SecureContainerCapacity",
                recovery.slot_capacity as "RecoveryStorageCapacity",
                (select count(*) from item_container_slots where container_id = inventory.id)
                    as "PermanentInventorySlots",
                (select count(*) from item_container_slots where container_id = bank.id)
                    as "BankSlots",
                (select count(*) from item_container_slots where container_id = secure.id)
                    as "SecureContainerSlots",
                (select count(*) from item_container_slots where container_id = recovery.id)
                    as "RecoveryStorageSlots",
                entitlement.tier_id as "SecureContainerTierId"
            from character_item_states state
            join characters character on character.id = state.character_id
            join account_secure_container_entitlements entitlement
              on entitlement.account_id = character.account_id
            join item_containers inventory
              on inventory.id = state.permanent_inventory_container_id
            join item_containers bank on bank.id = state.bank_container_id
            join item_containers secure on secure.id = state.secure_container_id
            join item_containers recovery on recovery.id = state.recovery_storage_container_id
            where state.character_id = @CharacterId;
            """,
            new { CharacterId = characterId });
    }

    private static ItemCatalogRuntimeDocument CompileCatalogVariant(
        ItemCatalogRuntimeDocument source,
        string changedDefinitionId,
        string displayName,
        long unitWeight)
    {
        var authoring = new ItemCatalogAuthoringDocument
        {
            FormatVersion = source.FormatVersion,
            CatalogId = source.CatalogId,
            BaseSecureContainerTierId = source.BaseSecureContainerTierId,
            Categories = source.Categories.Select(CloneIdentity).ToArray(),
            Tags = source.Tags.Select(CloneIdentity).ToArray(),
            EquipmentSlots = source.EquipmentSlots.Select(CloneIdentity).ToArray(),
            Definitions = source.Definitions.Select(definition =>
            {
                var isChanged = definition.Id == changedDefinitionId;
                return new ItemDefinitionAuthoringEntry
                {
                    Id = definition.Id,
                    DisplayName = isChanged ? displayName : definition.DisplayName,
                    Category = definition.Category,
                    Tags = definition.Tags.ToArray(),
                    UnitWeight = isChanged ? unitWeight : definition.UnitWeight,
                    MaximumStackSize = definition.MaximumStackSize,
                    EquipmentSlots = definition.EquipmentSlots.ToArray(),
                    PlayerDestroyable = definition.PlayerDestroyable,
                    LocationEligibility = new ItemLocationEligibilityAuthoringEntry
                    {
                        SecureContainer = definition.LocationEligibility.SecureContainer
                    },
                    DefaultPolicies = definition.DefaultPolicies.ToArray(),
                    Bag = definition.Bag is null
                        ? null
                        : new BagDefinitionAuthoringEntry
                        {
                            CarryCapacityBonus = definition.Bag.CarryCapacityBonus,
                            Slots = definition.Bag.Slots.Select(slot =>
                                new BagSlotAuthoringEntry
                                {
                                    Index = slot.Index,
                                    Kind = slot.Kind,
                                    AcceptedTags = slot.AcceptedTags.ToArray()
                                }).ToArray()
                        }
                };
            }).ToArray(),
            SecureContainerTiers = source.SecureContainerTiers.Select(tier =>
                new SecureContainerTierAuthoringEntry
                {
                    Id = tier.Id,
                    DisplayName = tier.DisplayName,
                    SlotCapacity = tier.SlotCapacity
                }).ToArray()
        };

        return ItemCatalogCompiler.Compile(authoring);
    }

    private static ItemCatalogIdentityEntry CloneIdentity(ItemCatalogIdentityEntry source)
    {
        return new ItemCatalogIdentityEntry
        {
            Id = source.Id,
            DisplayName = source.DisplayName
        };
    }

    private static void AssertConstraint(PostgresException exception, string constraintName)
    {
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal(constraintName, exception.ConstraintName);
    }

    private static void AssertCheckConstraint(PostgresException exception, string constraintName)
    {
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(constraintName, exception.ConstraintName);
    }

    private sealed record BootstrapSnapshot
    {
        public long Revision { get; init; }

        public long CarriedWeight { get; init; }

        public long BaseCarryCapacity { get; init; }

        public long CarryCapacity { get; init; }

        public Guid PermanentInventoryId { get; init; }

        public Guid BankId { get; init; }

        public Guid SecureContainerId { get; init; }

        public Guid RecoveryStorageId { get; init; }

        public int PermanentInventoryCapacity { get; init; }

        public int BankCapacity { get; init; }

        public int SecureContainerCapacity { get; init; }

        public int? RecoveryStorageCapacity { get; init; }

        public long PermanentInventorySlots { get; init; }

        public long BankSlots { get; init; }

        public long SecureContainerSlots { get; init; }

        public long RecoveryStorageSlots { get; init; }

        public string SecureContainerTierId { get; init; } = string.Empty;
    }

    private sealed record CharacterContainerIds(
        Guid PermanentInventoryId,
        Guid RecoveryStorageId);

    private sealed record OperationActorRow(Guid? AccountId, Guid? CharacterId);
}
