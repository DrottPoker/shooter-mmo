using Dapper;
using Mmorpg.WorldData.Items;
using Npgsql;
using static Mmorpg.GameService.Items.ItemTransactionRejection;

namespace Mmorpg.GameService.Items.Transactions;

// Reads items, containers, definitions, and policies inside one item transaction,
// with the ownership, revision, and live container access checks of each load.
internal sealed class ItemStateReader(
    ItemTransactionSession session,
    ItemActorAuthority authority)
{
    private NpgsqlConnection Connection => session.Connection;

    private NpgsqlTransaction Transaction => session.Transaction;

    private ItemActorAuthority Authority => authority;

    public async Task<LockedItem> LoadOwnedItemAsync(
        Guid itemInstanceId,
        Guid characterId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var item = await LoadItemAsync(itemInstanceId, cancellationToken);
        if (item is null)
        {
            Reject(ItemTransactionErrorCodes.ItemNotFound, "The item instance was not found.");
        }

        if (item.OwningCharacterId != characterId)
        {
            Reject(ItemTransactionErrorCodes.ItemNotOwned, "The character does not own the item instance.");
        }

        if (item.Revision != expectedRevision)
        {
            Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                $"Item '{itemInstanceId}' changed before the operation committed.");
        }

        Authority.EnsureLiveContainerAccess(item.SourceContainerType);

        return item;
    }

    public Task<LockedItem?> LoadItemAsync(
        Guid itemInstanceId,
        CancellationToken cancellationToken)
    {
        return Connection.QuerySingleOrDefaultAsync<LockedItem>(new CommandDefinition(
            $"""
            select
                item.id as "ItemInstanceId",
                item.definition_id as "DefinitionId",
                item.quantity as "Quantity",
                item.revision as "Revision",
                item.container_id as "ContainerId",
                item.container_slot_index as "ContainerSlotIndex",
                item.equipped_character_id as "EquippedCharacterId",
                item.equipment_slot_id as "EquipmentSlotId",
                container.container_type as "SourceContainerType",
                container.lifecycle as "SourceContainerLifecycle",
                container.bound_bag_item_instance_id as "SourceBagItemInstanceId",
                {ItemOwnershipSql.OwningCharacterId} as "OwningCharacterId",
                (bag.definition_id is not null) as "IsBag",
                bag.carry_capacity_bonus as "BagCarryCapacityBonus",
                bag_container.id as "BagContentsContainerId",
                bag_container.lifecycle as "BagContentsLifecycle",
                bag_container.revision as "BagContentsRevision"
            from item_instances item
            {ItemOwnershipSql.Joins}
            left join bag_definitions bag on bag.definition_id = item.definition_id
            left join item_containers bag_container
              on bag_container.bound_bag_item_instance_id = item.id
             and bag_container.container_type = 'bag_contents'
            where item.id = @ItemInstanceId;
            """,
            new { ItemInstanceId = itemInstanceId },
            Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<LockedContainer> LoadOwnedContainerAsync(
        Guid containerId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        var container = await LoadKernelPlacementContainerAsync(
            containerId,
            characterId,
            cancellationToken);
        Authority.EnsureLiveContainerAccess(container.ContainerType);

        return container;
    }

    // Loads a container that the kernel itself places items into, such as Recovery Storage
    // for displaced items. It is not a player-chosen location, so no service access applies.
    public async Task<LockedContainer> LoadKernelPlacementContainerAsync(
        Guid containerId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        var container = await LoadContainerAsync(containerId, cancellationToken);

        if (container.OwningCharacterId != characterId)
        {
            Reject(ItemTransactionErrorCodes.ItemNotOwned, "The character does not own the item container.");
        }

        return container;
    }

    public async Task<LockedContainer> LoadContainerAsync(
        Guid containerId,
        CancellationToken cancellationToken)
    {
        var container = await Connection.QuerySingleOrDefaultAsync<LockedContainer>(
            new CommandDefinition(
                """
                select
                    container.id as "ContainerId",
                    container.container_type as "ContainerType",
                    container.owner_character_id as "DirectOwnerCharacterId",
                    container.bound_bag_item_instance_id as "BoundBagItemInstanceId",
                    coalesce(
                        container.owner_character_id,
                        bound_bag.equipped_character_id) as "OwningCharacterId",
                    container.slot_capacity as "SlotCapacity",
                    container.revision as "Revision",
                    container.lifecycle as "Lifecycle"
                from item_containers container
                left join item_instances bound_bag
                  on bound_bag.id = container.bound_bag_item_instance_id
                where container.id = @ContainerId;
                """,
                new { ContainerId = containerId },
                Transaction,
                cancellationToken: cancellationToken));

        if (container is null || !string.Equals(container.Lifecycle, "active", StringComparison.Ordinal))
        {
            Reject(ItemTransactionErrorCodes.ItemNotFound, "The item container was not found.");
        }

        return container;
    }

    public async Task<ResolvedItemDefinition> LoadDefinitionAsync(
        string definitionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definitionId))
        {
            Reject(ItemTransactionErrorCodes.ItemNotFound, "An item definition id is required.");
        }

        using var grid = await Connection.QueryMultipleAsync(new CommandDefinition(
            """
            select
                definition.id as "Id",
                definition.display_name as "DisplayName",
                definition.category_id as "Category",
                definition.unit_weight as "UnitWeight",
                definition.maximum_stack_size as "MaximumStackSize",
                definition.player_destroyable as "PlayerDestroyable",
                definition.required_character_level as "RequiredCharacterLevel",
                definition.structural_fingerprint as "StructuralFingerprint",
                coalesce(location.is_allowed, false) as "SecureContainerEligible",
                bag.carry_capacity_bonus as "BagCarryCapacityBonus"
            from item_definitions definition
            left join item_definition_location_rules location
              on location.definition_id = definition.id
             and location.location_kind = 'secure_container'
            left join bag_definitions bag on bag.definition_id = definition.id
            where definition.id = @DefinitionId
              and definition.is_active;

            select tag_id
            from item_definition_tags
            where definition_id = @DefinitionId
            order by tag_id;

            select equipment_slot_id
            from item_definition_equipment_slots
            where definition_id = @DefinitionId
            order by equipment_slot_id;

            select equipment_slot_id
            from item_definition_blocked_equipment_slots
            where definition_id = @DefinitionId
            order by equipment_slot_id;

            select policy_kind
            from item_definition_default_policies
            where definition_id = @DefinitionId
            order by policy_kind;
            """,
            new { DefinitionId = definitionId },
            Transaction,
            cancellationToken: cancellationToken));

        var row = await grid.ReadSingleOrDefaultAsync<DefinitionRow>();
        if (row is null)
        {
            Reject(ItemTransactionErrorCodes.ItemNotFound, "The item definition was not found.");
        }

        var tags = (await grid.ReadAsync<string>()).ToArray();
        var equipmentSlots = (await grid.ReadAsync<string>()).ToArray();
        var blockedEquipmentSlots = (await grid.ReadAsync<string>()).ToArray();
        var defaultPolicies = (await grid.ReadAsync<string>()).ToArray();
        var definition = new ItemDefinition
        {
            Id = row.Id,
            DisplayName = row.DisplayName,
            Category = row.Category,
            Tags = tags,
            UnitWeight = row.UnitWeight,
            MaximumStackSize = row.MaximumStackSize,
            EquipmentSlots = equipmentSlots,
            BlockedEquipmentSlots = blockedEquipmentSlots,
            PlayerDestroyable = row.PlayerDestroyable,
            LocationEligibility = new ItemLocationEligibility
            {
                SecureContainer = row.SecureContainerEligible
            },
            DefaultPolicies = defaultPolicies,
            RequiredCharacterLevel = row.RequiredCharacterLevel,
            StructuralFingerprint = row.StructuralFingerprint
        };

        return new ResolvedItemDefinition(
            definition,
            row.BagCarryCapacityBonus is not null);
    }

    public Task<bool> BagHasContentsAsync(
        LockedItem item,
        CancellationToken cancellationToken)
    {
        if (!item.IsBag || item.BagContentsContainerId is null)
        {
            return Task.FromResult(false);
        }

        return Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists (
                select 1
                from item_instances
                where container_id = @ContainerId);
            """,
            new { ContainerId = item.BagContentsContainerId.Value },
            Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Guid>> LoadBagAggregateItemIdsAsync(
        Guid bagItemInstanceId,
        CancellationToken cancellationToken)
    {
        return (await Connection.QueryAsync<Guid>(new CommandDefinition(
            """
            select item.id
            from item_instances item
            where item.id = @BagItemInstanceId
               or item.container_id in (
                    select container.id
                    from item_containers container
                    where container.bound_bag_item_instance_id = @BagItemInstanceId
                      and container.container_type = 'bag_contents')
            order by item.id;
            """,
            new { BagItemInstanceId = bagItemInstanceId },
            Transaction,
            cancellationToken: cancellationToken))).ToArray();
    }

    public Task<IReadOnlyList<LockedItemPolicy>> LoadPoliciesAsync(
        Guid itemInstanceId,
        CancellationToken cancellationToken)
    {
        return LoadPoliciesCoreAsync(itemInstanceId, cancellationToken);
    }

    private async Task<IReadOnlyList<LockedItemPolicy>> LoadPoliciesCoreAsync(
        Guid itemInstanceId,
        CancellationToken cancellationToken)
    {
        return (await Connection.QueryAsync<LockedItemPolicy>(new CommandDefinition(
            """
            select
                id as "PolicyId",
                policy_kind as "PolicyKind",
                status as "Status",
                source_kind as "SourceKind",
                source_id as "SourceId",
                revision as "Revision"
            from item_instance_policies
            where item_instance_id = @ItemInstanceId
            order by policy_kind, source_kind, source_id, id;
            """,
            new { ItemInstanceId = itemInstanceId },
            Transaction,
            cancellationToken: cancellationToken))).ToArray();
    }

    private sealed class DefinitionRow
    {
        public string Id { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public long UnitWeight { get; set; }

        public int MaximumStackSize { get; set; }

        public bool PlayerDestroyable { get; set; }

        public int RequiredCharacterLevel { get; set; }

        public string StructuralFingerprint { get; set; } = string.Empty;

        public bool SecureContainerEligible { get; set; }

        public long? BagCarryCapacityBonus { get; set; }
    }
}
