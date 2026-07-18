using System.Data;
using System.Numerics;
using AuthService.Http;
using Dapper;
using Npgsql;
using ShooterMmo.WorldData.Items;

namespace AuthService.Items;

public sealed class ItemQueryService(NpgsqlDataSource dataSource)
{
    private const int RatioBasisPoints = 10_000;

    public async Task<ServiceResult<CharacterInventorySnapshotResponse>> GetCharacterInventoryAsync(
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "set transaction read only;",
                transaction: transaction,
                cancellationToken: cancellationToken));

            var state = await connection.QuerySingleOrDefaultAsync<CharacterStateRow>(
                new CommandDefinition(
                    """
                    select
                        character.id as "CharacterId",
                        revision.revision as "CatalogRevision",
                        state.revision as "ItemStateRevision",
                        state.carried_weight as "CarriedWeight",
                        state.carry_capacity as "CarryCapacity",
                        state.permanent_inventory_container_id as "PermanentInventoryContainerId",
                        state.bank_container_id as "BankContainerId",
                        state.secure_container_id as "SecureContainerId",
                        state.recovery_storage_container_id as "RecoveryStorageContainerId",
                        entitlement.tier_id as "SecureContainerTierId",
                        entitlement.revision as "SecureContainerEntitlementRevision"
                    from characters character
                    join character_item_states state on state.character_id = character.id
                    join item_system_settings settings on settings.id = 'character_default'
                    join item_catalog_revisions revision
                      on revision.catalog_id = settings.catalog_id
                     and revision.is_current
                    left join account_secure_container_entitlements entitlement
                      on entitlement.account_id = character.account_id
                    where character.id = @CharacterId
                      and character.account_id = @AccountId
                      and character.deleted_at is null;
                    """,
                    new { AccountId = accountId, CharacterId = characterId },
                    transaction,
                    cancellationToken: cancellationToken));

            if (state is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return ServiceResult<CharacterInventorySnapshotResponse>.NotFound(
                    "character_inventory_not_found",
                    "Character inventory was not found.");
            }

            if (string.IsNullOrWhiteSpace(state.SecureContainerTierId))
            {
                throw new InvalidOperationException(
                    "Character item state has no Secure Container entitlement.");
            }

            var permanentInventory = await LoadContainerAsync(
                connection,
                transaction,
                state.PermanentInventoryContainerId,
                "permanent_inventory",
                cancellationToken);
            var bank = await LoadContainerAsync(
                connection,
                transaction,
                state.BankContainerId,
                "bank",
                cancellationToken);
            var secureContainer = await LoadContainerAsync(
                connection,
                transaction,
                state.SecureContainerId,
                "secure_container",
                cancellationToken);
            var recoveryContainer = await LoadContainerAsync(
                connection,
                transaction,
                state.RecoveryStorageContainerId,
                "recovery_storage",
                cancellationToken);
            var equipment = await LoadEquipmentAsync(
                connection,
                transaction,
                characterId,
                cancellationToken);
            var equippedBag = await LoadEquippedBagAsync(
                connection,
                transaction,
                equipment,
                cancellationToken);
            var recoveryStorage = await LoadRecoveryStorageAsync(
                connection,
                transaction,
                characterId,
                recoveryContainer,
                cancellationToken);

            var response = new CharacterInventorySnapshotResponse(
                state.CharacterId,
                state.CatalogRevision,
                state.ItemStateRevision,
                permanentInventory,
                equipment,
                equippedBag,
                bank,
                new SecureContainerSnapshotResponse(
                    state.SecureContainerTierId,
                    state.SecureContainerEntitlementRevision,
                    secureContainer),
                recoveryStorage,
                state.CarriedWeight,
                state.CarryCapacity,
                CalculateLoadRatioBasisPoints(state.CarriedWeight, state.CarryCapacity),
                state.CarriedWeight <= state.CarryCapacity,
                EncumbranceRules.CalculateMovementMultiplierBasisPoints(
                    state.CarriedWeight,
                    state.CarryCapacity));

            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<CharacterInventorySnapshotResponse>.Ok(response);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ServiceResult<CharacterBankSnapshotResponse>> GetCharacterBankAsync(
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "set transaction read only;",
                transaction: transaction,
                cancellationToken: cancellationToken));
            var state = await LoadOwnedCharacterStateAsync(
                connection,
                transaction,
                accountId,
                characterId,
                cancellationToken);
            if (state is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return ServiceResult<CharacterBankSnapshotResponse>.NotFound(
                    "character_bank_not_found",
                    "Character bank was not found.");
            }

            var bank = await LoadContainerAsync(
                connection,
                transaction,
                state.BankContainerId,
                "bank",
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<CharacterBankSnapshotResponse>.Ok(
                new CharacterBankSnapshotResponse(
                    characterId,
                    state.CatalogRevision,
                    state.ItemStateRevision,
                    bank));
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ServiceResult<CharacterRecoverySnapshotResponse>> GetCharacterRecoveryAsync(
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "set transaction read only;",
                transaction: transaction,
                cancellationToken: cancellationToken));
            var state = await LoadOwnedCharacterStateAsync(
                connection,
                transaction,
                accountId,
                characterId,
                cancellationToken);
            if (state is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return ServiceResult<CharacterRecoverySnapshotResponse>.NotFound(
                    "character_recovery_not_found",
                    "Character Recovery Storage was not found.");
            }

            var recoveryContainer = await LoadContainerAsync(
                connection,
                transaction,
                state.RecoveryStorageContainerId,
                "recovery_storage",
                cancellationToken);
            var recovery = await LoadRecoveryStorageAsync(
                connection,
                transaction,
                characterId,
                recoveryContainer,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<CharacterRecoverySnapshotResponse>.Ok(
                new CharacterRecoverySnapshotResponse(
                    characterId,
                    state.CatalogRevision,
                    state.ItemStateRevision,
                    recovery));
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static Task<CharacterStateRow?> LoadOwnedCharacterStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        return connection.QuerySingleOrDefaultAsync<CharacterStateRow>(new CommandDefinition(
            """
            select
                character.id as "CharacterId",
                revision.revision as "CatalogRevision",
                state.revision as "ItemStateRevision",
                state.carried_weight as "CarriedWeight",
                state.carry_capacity as "CarryCapacity",
                state.permanent_inventory_container_id as "PermanentInventoryContainerId",
                state.bank_container_id as "BankContainerId",
                state.secure_container_id as "SecureContainerId",
                state.recovery_storage_container_id as "RecoveryStorageContainerId",
                entitlement.tier_id as "SecureContainerTierId",
                entitlement.revision as "SecureContainerEntitlementRevision"
            from characters character
            join character_item_states state on state.character_id = character.id
            join item_system_settings settings on settings.id = 'character_default'
            join item_catalog_revisions revision
              on revision.catalog_id = settings.catalog_id
             and revision.is_current
            left join account_secure_container_entitlements entitlement
              on entitlement.account_id = character.account_id
            where character.id = @CharacterId
              and character.account_id = @AccountId
              and character.deleted_at is null;
            """,
            new { AccountId = accountId, CharacterId = characterId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<ItemContainerSnapshotResponse> LoadContainerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid containerId,
        string expectedContainerType,
        CancellationToken cancellationToken)
    {
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            """
            select
                id as "ContainerId",
                container_type as "ContainerType",
                revision as "Revision",
                slot_capacity as "SlotCapacity"
            from item_containers
            where id = @ContainerId
              and container_type = @ExpectedContainerType
              and lifecycle = 'active';

            select slot_index as "SlotIndex", slot_kind as "SlotKind"
            from item_container_slots
            where container_id = @ContainerId
            order by slot_index;

            select slot_index as "SlotIndex", tag_id as "TagId"
            from item_container_slot_tags
            where container_id = @ContainerId
            order by slot_index, tag_id;

            select
                id as "ItemInstanceId",
                definition_id as "DefinitionId",
                quantity as "Quantity",
                revision as "Revision",
                container_slot_index as "ContainerSlotIndex"
            from item_instances
            where container_id = @ContainerId
            order by container_slot_index, id;

            select
                policy.item_instance_id as "ItemInstanceId",
                policy.policy_kind as "PolicyKind",
                policy.status as "Status",
                policy.source_kind as "SourceKind"
            from item_instance_policies policy
            join item_instances item on item.id = policy.item_instance_id
            where item.container_id = @ContainerId
              and policy.status = 'active'
            order by policy.item_instance_id, policy.policy_kind;
            """,
            new { ContainerId = containerId, ExpectedContainerType = expectedContainerType },
            transaction,
            cancellationToken: cancellationToken));

        var container = await grid.ReadSingleOrDefaultAsync<ContainerRow>()
            ?? throw new InvalidOperationException(
                $"Required {expectedContainerType} container '{containerId}' was not found.");
        var slots = (await grid.ReadAsync<ContainerSlotRow>()).ToArray();
        var slotTags = (await grid.ReadAsync<ContainerSlotTagRow>()).ToArray();
        var items = (await grid.ReadAsync<ContainerItemRow>()).ToArray();
        var policies = (await grid.ReadAsync<PolicyRow>()).ToArray();

        var tagsBySlot = slotTags.ToLookup(row => row.SlotIndex, row => row.TagId);
        var policiesByItem = policies.ToLookup(row => row.ItemInstanceId);
        var itemsBySlot = items.ToDictionary(
            row => row.ContainerSlotIndex,
            row => CreateItemResponse(row, policiesByItem[row.ItemInstanceId]));

        return new ItemContainerSnapshotResponse(
            container.ContainerId,
            container.ContainerType,
            container.Revision,
            container.SlotCapacity,
            slots.Select(slot => new ItemSlotSnapshotResponse(
                slot.SlotIndex,
                slot.SlotKind,
                tagsBySlot[slot.SlotIndex].ToArray(),
                itemsBySlot.GetValueOrDefault(slot.SlotIndex))).ToArray());
    }

    private static async Task<IReadOnlyList<EquipmentSlotSnapshotResponse>> LoadEquipmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            """
            select id as "EquipmentSlotId", sort_order as "SortOrder"
            from equipment_slots
            order by sort_order, id;

            select
                id as "ItemInstanceId",
                definition_id as "DefinitionId",
                quantity as "Quantity",
                revision as "Revision",
                equipment_slot_id as "EquipmentSlotId"
            from item_instances
            where equipped_character_id = @CharacterId
            order by equipment_slot_id, id;

            select
                policy.item_instance_id as "ItemInstanceId",
                policy.policy_kind as "PolicyKind",
                policy.status as "Status",
                policy.source_kind as "SourceKind"
            from item_instance_policies policy
            join item_instances item on item.id = policy.item_instance_id
            where item.equipped_character_id = @CharacterId
              and policy.status = 'active'
            order by policy.item_instance_id, policy.policy_kind;
            """,
            new { CharacterId = characterId },
            transaction,
            cancellationToken: cancellationToken));

        var slots = (await grid.ReadAsync<EquipmentSlotRow>()).ToArray();
        var items = (await grid.ReadAsync<EquipmentItemRow>()).ToArray();
        var policies = (await grid.ReadAsync<PolicyRow>()).ToArray();
        var policiesByItem = policies.ToLookup(row => row.ItemInstanceId);
        var itemsBySlot = items.ToDictionary(
            row => row.EquipmentSlotId,
            row => CreateItemResponse(row, policiesByItem[row.ItemInstanceId]),
            StringComparer.Ordinal);

        return slots.Select(slot => new EquipmentSlotSnapshotResponse(
            slot.EquipmentSlotId,
            slot.SortOrder,
            itemsBySlot.GetValueOrDefault(slot.EquipmentSlotId))).ToArray();
    }

    private static async Task<EquippedBagSnapshotResponse?> LoadEquippedBagAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<EquipmentSlotSnapshotResponse> equipment,
        CancellationToken cancellationToken)
    {
        var bagItem = equipment.Single(slot => string.Equals(
                slot.EquipmentSlotId,
                ItemEquipmentSlotIds.Bag,
                StringComparison.Ordinal))
            .Item;
        if (bagItem is null)
        {
            return null;
        }

        var bagContainerId = await connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                select container.id
                from item_containers container
                join bag_definitions bag on bag.definition_id = @DefinitionId
                where container.bound_bag_item_instance_id = @ItemInstanceId
                  and container.container_type = 'bag_contents'
                  and container.lifecycle = 'active';
                """,
                new
                {
                    bagItem.DefinitionId,
                    bagItem.ItemInstanceId
                },
                transaction,
                cancellationToken: cancellationToken));

        if (bagContainerId is null)
        {
            throw new InvalidOperationException(
                $"Equipped Bag item '{bagItem.ItemInstanceId}' has no active contents container.");
        }

        var contents = await LoadContainerAsync(
            connection,
            transaction,
            bagContainerId.Value,
            "bag_contents",
            cancellationToken);
        return new EquippedBagSnapshotResponse(bagItem, contents);
    }

    private static async Task<RecoveryStorageSnapshotResponse> LoadRecoveryStorageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid characterId,
        ItemContainerSnapshotResponse recoveryContainer,
        CancellationToken cancellationToken)
    {
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            """
            select
                id as "DeliveryId",
                revision as "Revision",
                source_kind as "SourceKind",
                created_at as "CreatedAt",
                available_at as "AvailableAt",
                expires_at as "ExpiresAt",
                claimed_at as "ClaimedAt"
            from recovery_deliveries
            where character_id = @CharacterId
              and recovery_storage_container_id = @ContainerId
            order by created_at, id;

            select
                link.recovery_delivery_id as "DeliveryId",
                link.item_instance_id as "ItemInstanceId",
                link.item_order as "ItemOrder",
                item.container_id as "ContainerId",
                item.container_slot_index as "ContainerSlotIndex"
            from recovery_delivery_items link
            join recovery_deliveries delivery on delivery.id = link.recovery_delivery_id
            join item_instances item on item.id = link.item_instance_id
            where delivery.character_id = @CharacterId
              and delivery.recovery_storage_container_id = @ContainerId
            order by link.recovery_delivery_id, link.item_order;
            """,
            new
            {
                CharacterId = characterId,
                ContainerId = recoveryContainer.ContainerId
            },
            transaction,
            cancellationToken: cancellationToken));

        var deliveries = (await grid.ReadAsync<RecoveryDeliveryRow>()).ToArray();
        var deliveryItems = (await grid.ReadAsync<RecoveryDeliveryItemRow>()).ToArray();
        var recoveryItems = recoveryContainer.Slots
            .Where(slot => slot.Item is not null)
            .Select(slot => slot.Item!)
            .ToDictionary(item => item.ItemInstanceId);

        foreach (var deliveryItem in deliveryItems)
        {
            if (deliveryItem.ContainerId != recoveryContainer.ContainerId
                || deliveryItem.ContainerSlotIndex is null
                || !recoveryItems.ContainsKey(deliveryItem.ItemInstanceId))
            {
                throw new InvalidOperationException(
                    $"Recovery delivery item '{deliveryItem.ItemInstanceId}' has invalid custody.");
            }
        }

        var linkedItemIds = deliveryItems
            .Select(item => item.ItemInstanceId)
            .ToHashSet();
        if (recoveryItems.Keys.Any(itemId => !linkedItemIds.Contains(itemId)))
        {
            throw new InvalidOperationException(
                "Recovery Storage contains an item without a delivery record.");
        }

        var itemsByDelivery = deliveryItems.ToLookup(row => row.DeliveryId);
        return new RecoveryStorageSnapshotResponse(
            recoveryContainer.ContainerId,
            recoveryContainer.Revision,
            deliveries.Select(delivery => new RecoveryDeliverySnapshotResponse(
                delivery.DeliveryId,
                delivery.Revision,
                delivery.SourceKind,
                delivery.CreatedAt,
                delivery.AvailableAt,
                delivery.ExpiresAt,
                delivery.ClaimedAt,
                itemsByDelivery[delivery.DeliveryId]
                    .Select(item => new RecoveryDeliveryItemSnapshotResponse(
                        item.ItemOrder,
                        item.ContainerSlotIndex!.Value,
                        recoveryItems[item.ItemInstanceId]))
                    .ToArray())).ToArray());
    }

    private static ItemInstanceSnapshotResponse CreateItemResponse(
        ItemRow row,
        IEnumerable<PolicyRow> policies)
    {
        return new ItemInstanceSnapshotResponse(
            row.ItemInstanceId,
            row.DefinitionId,
            row.Quantity,
            row.Revision,
            policies.Select(policy => new ItemPolicySummaryResponse(
                policy.PolicyKind,
                policy.Status,
                ToProtectionSource(policy.SourceKind))).ToArray());
    }

    private static string ToProtectionSource(string sourceKind)
    {
        return sourceKind switch
        {
            ItemPolicySourceKinds.InsuranceService => "insurance_npc",
            ItemPolicySourceKinds.QuestGrant => "quest_grant",
            ItemPolicySourceKinds.CatalogDefault => "catalog_default",
            _ => "system"
        };
    }

    private static int CalculateLoadRatioBasisPoints(long carriedWeight, long capacity)
    {
        if (carriedWeight < 0 || capacity <= 0)
        {
            throw new InvalidOperationException("Character carry state is invalid.");
        }

        var scaled = ((BigInteger)carriedWeight * RatioBasisPoints) / capacity;
        return scaled > int.MaxValue ? int.MaxValue : (int)scaled;
    }

    private sealed class CharacterStateRow
    {
        public Guid CharacterId { get; set; }

        public string CatalogRevision { get; set; } = string.Empty;

        public long ItemStateRevision { get; set; }

        public long CarriedWeight { get; set; }

        public long CarryCapacity { get; set; }

        public Guid PermanentInventoryContainerId { get; set; }

        public Guid BankContainerId { get; set; }

        public Guid SecureContainerId { get; set; }

        public Guid RecoveryStorageContainerId { get; set; }

        public string? SecureContainerTierId { get; set; }

        public long SecureContainerEntitlementRevision { get; set; }
    }

    private sealed class ContainerRow
    {
        public Guid ContainerId { get; set; }

        public string ContainerType { get; set; } = string.Empty;

        public long Revision { get; set; }

        public int? SlotCapacity { get; set; }
    }

    private sealed class ContainerSlotRow
    {
        public int SlotIndex { get; set; }

        public string SlotKind { get; set; } = string.Empty;
    }

    private sealed class ContainerSlotTagRow
    {
        public int SlotIndex { get; set; }

        public string TagId { get; set; } = string.Empty;
    }

    private abstract class ItemRow
    {
        public Guid ItemInstanceId { get; set; }

        public string DefinitionId { get; set; } = string.Empty;

        public int Quantity { get; set; }

        public long Revision { get; set; }
    }

    private sealed class ContainerItemRow : ItemRow
    {
        public int ContainerSlotIndex { get; set; }
    }

    private sealed class EquipmentItemRow : ItemRow
    {
        public string EquipmentSlotId { get; set; } = string.Empty;
    }

    private sealed class PolicyRow
    {
        public Guid ItemInstanceId { get; set; }

        public string PolicyKind { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string SourceKind { get; set; } = string.Empty;
    }

    private sealed class EquipmentSlotRow
    {
        public string EquipmentSlotId { get; set; } = string.Empty;

        public int SortOrder { get; set; }
    }

    private sealed class RecoveryDeliveryRow
    {
        public Guid DeliveryId { get; set; }

        public long Revision { get; set; }

        public string SourceKind { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        public DateTime? AvailableAt { get; set; }

        public DateTime? ExpiresAt { get; set; }

        public DateTime? ClaimedAt { get; set; }
    }

    private sealed class RecoveryDeliveryItemRow
    {
        public Guid DeliveryId { get; set; }

        public Guid ItemInstanceId { get; set; }

        public int ItemOrder { get; set; }

        public Guid ContainerId { get; set; }

        public int? ContainerSlotIndex { get; set; }
    }
}
