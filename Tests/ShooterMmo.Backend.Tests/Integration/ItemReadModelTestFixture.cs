using Dapper;
using Npgsql;

namespace ShooterMmo.Backend.Tests.Integration;

internal sealed class ItemReadModelTestFixture(
    NpgsqlDataSource dataSource,
    Guid characterId)
{
    public async Task<Guid> AddContainerItemAsync(
        string containerType,
        int slotIndex,
        string definitionId,
        int quantity = 1,
        params TestItemPolicy[] policies)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var containerId = await connection.QuerySingleAsync<Guid>(
            """
            select id
            from item_containers
            where owner_character_id = @CharacterId
              and container_type = @ContainerType
              and lifecycle = 'active';
            """,
            new { CharacterId = characterId, ContainerType = containerType },
            transaction);
        var itemId = await AddItemToContainerAsync(
            connection,
            transaction,
            containerId,
            slotIndex,
            definitionId,
            quantity,
            policies);
        await connection.ExecuteAsync(
            "update item_containers set revision = revision + 1 where id = @ContainerId;",
            new { ContainerId = containerId },
            transaction);
        await transaction.CommitAsync();
        return itemId;
    }

    public async Task<Guid> EquipItemAsync(
        string equipmentSlotId,
        string definitionId,
        params TestItemPolicy[] policies)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var itemId = Guid.NewGuid();
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
                @DefinitionId,
                1,
                @CharacterId,
                @EquipmentSlotId);
            """,
            new
            {
                ItemId = itemId,
                DefinitionId = definitionId,
                CharacterId = characterId,
                EquipmentSlotId = equipmentSlotId
            },
            transaction);
        await AddPoliciesAsync(connection, transaction, itemId, policies);
        await transaction.CommitAsync();
        return itemId;
    }

    public async Task<TestBagFixture> EquipBagAsync(
        string definitionId = "bag.field_pack",
        params TestItemPolicy[] policies)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var itemId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var slotCapacity = await connection.QuerySingleAsync<int>(
            "select count(*) from bag_definition_slots where definition_id = @DefinitionId;",
            new { DefinitionId = definitionId },
            transaction);

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
                @DefinitionId,
                1,
                @CharacterId,
                'bag');

            insert into item_containers (
                id,
                container_type,
                bound_bag_item_instance_id,
                slot_capacity)
            values (
                @ContainerId,
                'bag_contents',
                @ItemId,
                @SlotCapacity);

            insert into item_container_slots (container_id, slot_index, slot_kind)
            select @ContainerId, slot_index, slot_kind
            from bag_definition_slots
            where definition_id = @DefinitionId
            order by slot_index;

            insert into item_container_slot_tags (container_id, slot_index, tag_id)
            select @ContainerId, slot_index, tag_id
            from bag_definition_slot_tags
            where definition_id = @DefinitionId
            order by slot_index, tag_id;
            """,
            new
            {
                ItemId = itemId,
                DefinitionId = definitionId,
                CharacterId = characterId,
                ContainerId = containerId,
                SlotCapacity = slotCapacity
            },
            transaction);
        await AddPoliciesAsync(connection, transaction, itemId, policies);
        await transaction.CommitAsync();
        return new TestBagFixture(itemId, containerId);
    }

    public async Task<Guid> AddBagContentAsync(
        Guid bagContainerId,
        int slotIndex,
        string definitionId,
        int quantity = 1,
        params TestItemPolicy[] policies)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var itemId = await AddItemToContainerAsync(
            connection,
            transaction,
            bagContainerId,
            slotIndex,
            definitionId,
            quantity,
            policies);
        await connection.ExecuteAsync(
            "update item_containers set revision = revision + 1 where id = @ContainerId;",
            new { ContainerId = bagContainerId },
            transaction);
        await transaction.CommitAsync();
        return itemId;
    }

    public async Task<TestRecoveryDeliveryFixture> AddRecoveryDeliveryAsync(
        string sourceKind,
        string sourceEventId,
        string definitionId,
        int quantity = 1,
        params TestItemPolicy[] policies)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var containerId = await connection.QuerySingleAsync<Guid>(
            """
            select recovery_storage_container_id
            from character_item_states
            where character_id = @CharacterId;
            """,
            new { CharacterId = characterId },
            transaction);
        var slotIndex = await connection.QuerySingleAsync<int>(
            """
            select coalesce(max(slot_index) + 1, 0)
            from item_container_slots
            where container_id = @ContainerId;
            """,
            new { ContainerId = containerId },
            transaction);
        await connection.ExecuteAsync(
            """
            insert into item_container_slots (container_id, slot_index, slot_kind)
            values (@ContainerId, @SlotIndex, 'general');
            """,
            new { ContainerId = containerId, SlotIndex = slotIndex },
            transaction);
        var itemId = await AddItemToContainerAsync(
            connection,
            transaction,
            containerId,
            slotIndex,
            definitionId,
            quantity,
            policies);
        var deliveryId = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            insert into recovery_deliveries (
                id,
                character_id,
                recovery_storage_container_id,
                source_kind,
                source_event_id,
                available_at)
            values (
                @DeliveryId,
                @CharacterId,
                @ContainerId,
                @SourceKind,
                @SourceEventId,
                now());

            insert into recovery_delivery_items (
                recovery_delivery_id,
                item_instance_id,
                item_order)
            values (@DeliveryId, @ItemId, 0);

            update item_containers
            set revision = revision + 1
            where id = @ContainerId;
            """,
            new
            {
                DeliveryId = deliveryId,
                CharacterId = characterId,
                ContainerId = containerId,
                SourceKind = sourceKind,
                SourceEventId = sourceEventId,
                ItemId = itemId
            },
            transaction);
        await transaction.CommitAsync();
        return new TestRecoveryDeliveryFixture(deliveryId, itemId, slotIndex);
    }

    public async Task SetCarryStateAsync(
        long carriedWeight,
        long carryCapacity,
        long revision)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            update character_item_states
            set carried_weight = @CarriedWeight,
                carry_capacity = @CarryCapacity,
                revision = @Revision,
                updated_at = now()
            where character_id = @CharacterId;
            """,
            new
            {
                CharacterId = characterId,
                CarriedWeight = carriedWeight,
                CarryCapacity = carryCapacity,
                Revision = revision
            });
    }

    public async Task AddOperationSentinelAsync(string secretValue)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into item_operations (
                operation_id,
                actor_character_id,
                operation_kind,
                request_hash,
                request_payload)
            values (
                @OperationId,
                @CharacterId,
                'test_fixture',
                repeat('a', 64),
                jsonb_build_object('secret', @SecretValue));
            """,
            new
            {
                OperationId = Guid.NewGuid(),
                CharacterId = characterId,
                SecretValue = secretValue
            });
    }

    private static async Task<Guid> AddItemToContainerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid containerId,
        int slotIndex,
        string definitionId,
        int quantity,
        IReadOnlyCollection<TestItemPolicy> policies)
    {
        var itemId = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (
                @ItemId,
                @DefinitionId,
                @Quantity,
                @ContainerId,
                @SlotIndex);
            """,
            new
            {
                ItemId = itemId,
                DefinitionId = definitionId,
                Quantity = quantity,
                ContainerId = containerId,
                SlotIndex = slotIndex
            },
            transaction);
        await AddPoliciesAsync(connection, transaction, itemId, policies);
        return itemId;
    }

    private static async Task AddPoliciesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        IEnumerable<TestItemPolicy> policies)
    {
        foreach (var policy in policies)
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
                    @ItemId,
                    @PolicyKind,
                    'test_fixture',
                    @SourceId);
                """,
                new
                {
                    PolicyId = Guid.NewGuid(),
                    ItemId = itemId,
                    policy.PolicyKind,
                    policy.SourceId
                },
                transaction);
        }
    }
}

internal sealed record TestItemPolicy(
    string PolicyKind,
    string SourceId = "test-policy-source");

internal sealed record TestBagFixture(
    Guid ItemInstanceId,
    Guid ContainerId);

internal sealed record TestRecoveryDeliveryFixture(
    Guid DeliveryId,
    Guid ItemInstanceId,
    int SlotIndex);
