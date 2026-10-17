using Dapper;
using Mmorpg.WorldData.Items;
using Npgsql;
using static Mmorpg.GameService.Items.ItemTransactionRejection;

namespace Mmorpg.GameService.Items.Transactions;

// Slot and equipment placement rules: which container slot or equipment slot accepts
// an item, including Secure Container eligibility, Bag nesting, and Recovery Storage.
internal sealed class ItemSlotPlacement(ItemTransactionSession session)
{
    private NpgsqlConnection Connection => session.Connection;

    private NpgsqlTransaction Transaction => session.Transaction;

    // Selects the slot that the item occupies afterwards. The quantity is the stack size in that
    // slot after the operation, which a corpse equipment slot limits.
    public async Task<int> SelectDestinationSlotAsync(
        LockedContainer container,
        ResolvedItemDefinition definition,
        int quantity,
        int? requestedSlotIndex,
        bool bagHasContents,
        Guid? movingBagItemId,
        bool allowRecoveryStorage,
        CancellationToken cancellationToken,
        Guid? permittedOccupantItemInstanceId = null)
    {
        if (string.Equals(container.ContainerType, "recovery_storage", StringComparison.Ordinal))
        {
            if (!allowRecoveryStorage)
            {
                Reject(
                    ItemTransactionErrorCodes.RecoveryAccessRequired,
                    "Items can enter Recovery Storage only through a system delivery.");
            }

            Reject(
                ItemTransactionErrorCodes.ItemSlotIncompatible,
                "Recovery Storage delivery slots are allocated by the transaction kernel.");
        }

        if (string.Equals(container.ContainerType, "secure_container", StringComparison.Ordinal)
            && !SecureContainerRules.IsEligible(definition.RuntimeDefinition))
        {
            Reject(
                ItemTransactionErrorCodes.SecureContainerItemForbidden,
                "The item definition is not eligible for Secure Container custody.");
        }

        var rows = (await Connection.QueryAsync<ContainerSlotQueryRow>(new CommandDefinition(
            """
            select
                slot.slot_index as "SlotIndex",
                slot.slot_kind as "SlotKind",
                equipment_slot.id as "EquipmentSlotId",
                equipment_slot.maximum_quantity as "EquipmentSlotMaximumQuantity",
                tag.tag_id as "TagId",
                occupant.id as "OccupantItemInstanceId"
            from item_container_slots slot
            left join equipment_slots equipment_slot
              on @ContainerType = 'corpse_equipment'
             and equipment_slot.id = slot.equipment_slot_id
            left join item_container_slot_tags tag
              on tag.container_id = slot.container_id
             and tag.slot_index = slot.slot_index
            left join item_instances occupant
              on occupant.container_id = slot.container_id
             and occupant.container_slot_index = slot.slot_index
            where slot.container_id = @ContainerId
            order by slot.slot_index, tag.tag_id;
            """,
            new { container.ContainerId, container.ContainerType },
            Transaction,
            cancellationToken: cancellationToken))).ToArray();

        var slots = rows
            .GroupBy(row => new
            {
                row.SlotIndex,
                row.SlotKind,
                row.EquipmentSlotId,
                row.EquipmentSlotMaximumQuantity,
                row.OccupantItemInstanceId
            })
            .Select(group => new DestinationSlot(
                group.Key.SlotIndex,
                group.Key.SlotKind,
                group.Where(row => row.TagId is not null).Select(row => row.TagId!).ToArray(),
                group.Key.EquipmentSlotId,
                group.Key.EquipmentSlotMaximumQuantity,
                group.Key.OccupantItemInstanceId))
            .OrderBy(slot => slot.SlotIndex)
            .ToArray();

        bool IsCompatible(DestinationSlot slot)
        {
            if (string.Equals(
                    container.ContainerType,
                    "corpse_equipment",
                    StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(slot.EquipmentSlotId)
                    || !ItemEquipmentRules.IsCompatible(
                        definition.RuntimeDefinition,
                        slot.EquipmentSlotId)))
            {
                return false;
            }

            if (definition.IsBag)
            {
                var destinationKind = ResolveBagDestinationKind(container.ContainerType, slot.SlotKind);
                var wouldCreateCycle = movingBagItemId is not null
                    && container.BoundBagItemInstanceId == movingBagItemId;
                return BagLocationRules.CanPlace(
                    bagHasContents,
                    destinationKind,
                    wouldCreateCycle);
            }

            return ItemSlotRules.Accepts(
                definition.RuntimeDefinition,
                new BagSlotDefinition
                {
                    Index = slot.SlotIndex,
                    Kind = slot.SlotKind,
                    AcceptedTags = slot.AcceptedTags
                });
        }

        // A corpse equipment slot holds at most its equipment slot's quantity, so an
        // ammunition stack keeps the limit it had while equipped.
        bool CanHold(DestinationSlot slot)
        {
            return slot.EquipmentSlotMaximumQuantity is not int maximumQuantity
                || ItemEquipmentRules.CanHoldQuantity(
                    definition.RuntimeDefinition,
                    new ItemEquipmentSlotEntry
                    {
                        Id = slot.EquipmentSlotId!,
                        MaximumQuantity = maximumQuantity
                    },
                    quantity);
        }

        if (requestedSlotIndex is not null)
        {
            var requested = slots.SingleOrDefault(slot => slot.SlotIndex == requestedSlotIndex.Value);
            if (requested is null || !IsCompatible(requested))
            {
                Reject(
                    definition.IsBag && bagHasContents
                        ? ItemTransactionErrorCodes.BagNotEmpty
                        : ItemTransactionErrorCodes.ItemSlotIncompatible,
                    "The requested destination slot is incompatible with the item.");
            }

            if (!CanHold(requested))
            {
                Reject(
                    ItemTransactionErrorCodes.ItemStackLimitExceeded,
                    "The requested destination slot cannot hold the item quantity.");
            }

            if (requested.OccupantItemInstanceId is not null
                && requested.OccupantItemInstanceId != permittedOccupantItemInstanceId)
            {
                Reject(
                    ItemTransactionErrorCodes.ItemSlotOccupied,
                    "The requested destination slot is occupied.");
            }

            return requested.SlotIndex;
        }

        var selected = slots.FirstOrDefault(slot =>
            slot.OccupantItemInstanceId is null && IsCompatible(slot) && CanHold(slot));
        if (selected is null)
        {
            if (definition.IsBag && bagHasContents)
            {
                Reject(
                    ItemTransactionErrorCodes.BagNotEmpty,
                    "A non-empty Bag cannot enter an ordinary item slot.");
            }

            Reject(
                ItemTransactionErrorCodes.ItemSlotOccupied,
                "No compatible empty destination slot is available.");
        }

        return selected.SlotIndex;
    }

    // Checks that the item can enter the empty equipment slot with its whole quantity and that
    // no equipped item blocks the slot or is blocked by the item.
    public async Task EnsureEquipmentDestinationAsync(
        Guid characterId,
        string equipmentSlotId,
        ResolvedItemDefinition definition,
        int quantity,
        bool bagHasContents,
        CancellationToken cancellationToken)
    {
        var slot = await LoadEquipmentSlotAsync(characterId, equipmentSlotId, cancellationToken);
        if (slot is null
            || !ItemEquipmentRules.IsCompatible(definition.RuntimeDefinition, equipmentSlotId)
            || (definition.IsBag
                && !BagLocationRules.CanPlace(
                    bagHasContents,
                    BagDestinationKind.CharacterBagEquipmentSlot,
                    false)))
        {
            Reject(
                ItemTransactionErrorCodes.EquipmentSlotIncompatible,
                "The equipment slot is incompatible with the item definition.");
        }

        EnsureEquipmentQuantity(slot, definition, quantity);
        if (slot.OccupantItemInstanceId is not null)
        {
            Reject(
                ItemTransactionErrorCodes.EquipmentSlotOccupied,
                "The requested equipment slot is occupied.");
        }

        var blockedSlotsByOccupiedSlot = (await Connection.QueryAsync<EquippedBlockedSlotRow>(
                new CommandDefinition(
                    """
                    select
                        item.equipment_slot_id as "EquipmentSlotId",
                        blocked.equipment_slot_id as "BlockedEquipmentSlotId"
                    from item_instances item
                    left join item_definition_blocked_equipment_slots blocked
                      on blocked.definition_id = item.definition_id
                    where item.equipped_character_id = @CharacterId
                    order by item.equipment_slot_id, blocked.equipment_slot_id;
                    """,
                    new { CharacterId = characterId },
                    Transaction,
                    cancellationToken: cancellationToken)))
            .GroupBy(row => row.EquipmentSlotId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Where(row => row.BlockedEquipmentSlotId is not null)
                    .Select(row => row.BlockedEquipmentSlotId!)
                    .ToArray(),
                StringComparer.Ordinal);
        var blockingSlotId = ItemEquipmentRules.FindBlockingSlot(
            definition.RuntimeDefinition,
            equipmentSlotId,
            blockedSlotsByOccupiedSlot);
        if (blockingSlotId is not null)
        {
            Reject(
                ItemTransactionErrorCodes.EquipmentSlotBlocked,
                $"The item in equipment slot '{blockingSlotId}' blocks this equipment change.");
        }
    }

    // Checks that an equipped stack can grow to the quantity, such as when arrows top up the
    // equipped ammunition stack.
    public async Task EnsureEquippedStackQuantityAsync(
        Guid characterId,
        string equipmentSlotId,
        ResolvedItemDefinition definition,
        int quantity,
        CancellationToken cancellationToken)
    {
        var slot = await LoadEquipmentSlotAsync(characterId, equipmentSlotId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Equipped item references unknown equipment slot '{equipmentSlotId}'.");
        EnsureEquipmentQuantity(slot, definition, quantity);
    }

    private Task<EquipmentDestinationRow?> LoadEquipmentSlotAsync(
        Guid characterId,
        string equipmentSlotId,
        CancellationToken cancellationToken)
    {
        return Connection.QuerySingleOrDefaultAsync<EquipmentDestinationRow>(
            new CommandDefinition(
                """
                select
                    equipment.id as "EquipmentSlotId",
                    equipment.maximum_quantity as "MaximumQuantity",
                    occupant.id as "OccupantItemInstanceId"
                from equipment_slots equipment
                left join item_instances occupant
                  on occupant.equipped_character_id = @CharacterId
                 and occupant.equipment_slot_id = equipment.id
                where equipment.id = @EquipmentSlotId;
                """,
                new { CharacterId = characterId, EquipmentSlotId = equipmentSlotId },
                Transaction,
                cancellationToken: cancellationToken));
    }

    private static void EnsureEquipmentQuantity(
        EquipmentDestinationRow slot,
        ResolvedItemDefinition definition,
        int quantity)
    {
        var entry = new ItemEquipmentSlotEntry
        {
            Id = slot.EquipmentSlotId,
            MaximumQuantity = slot.MaximumQuantity
        };
        if (!ItemEquipmentRules.CanHoldQuantity(definition.RuntimeDefinition, entry, quantity))
        {
            Reject(
                ItemTransactionErrorCodes.ItemStackLimitExceeded,
                $"Equipment slot '{slot.EquipmentSlotId}' holds at most "
                + $"{ItemEquipmentRules.MaximumQuantity(definition.RuntimeDefinition, entry)} of the item.");
        }
    }

    private static BagDestinationKind ResolveBagDestinationKind(
        string containerType,
        string slotKind)
    {
        if (string.Equals(containerType, "permanent_inventory", StringComparison.Ordinal)
            && string.Equals(slotKind, BagSlotKindIds.General, StringComparison.Ordinal))
        {
            return BagDestinationKind.PermanentInventoryGeneralSlot;
        }

        if (string.Equals(containerType, "bank", StringComparison.Ordinal)
            && string.Equals(slotKind, BagSlotKindIds.General, StringComparison.Ordinal))
        {
            return BagDestinationKind.BankGeneralSlot;
        }

        if (string.Equals(containerType, "bag_contents", StringComparison.Ordinal))
        {
            return string.Equals(slotKind, BagSlotKindIds.General, StringComparison.Ordinal)
                ? BagDestinationKind.BagGeneralSlot
                : BagDestinationKind.BagSpecializedSlot;
        }

        if (string.Equals(containerType, "recovery_storage", StringComparison.Ordinal))
        {
            return BagDestinationKind.RecoveryStorageSlot;
        }

        if (string.Equals(containerType, "corpse_inventory", StringComparison.Ordinal)
            || string.Equals(containerType, "corpse_equipment", StringComparison.Ordinal)
            || string.Equals(containerType, "corpse_bag_contents", StringComparison.Ordinal))
        {
            return BagDestinationKind.CorpseStorageSlot;
        }

        return BagDestinationKind.SecureContainerSlot;
    }

    private sealed class ContainerSlotQueryRow
    {
        public int SlotIndex { get; set; }

        public string SlotKind { get; set; } = string.Empty;

        public string? EquipmentSlotId { get; set; }

        public int? EquipmentSlotMaximumQuantity { get; set; }

        public string? TagId { get; set; }

        public Guid? OccupantItemInstanceId { get; set; }
    }

    private sealed record DestinationSlot(
        int SlotIndex,
        string SlotKind,
        string[] AcceptedTags,
        string? EquipmentSlotId,
        int? EquipmentSlotMaximumQuantity,
        Guid? OccupantItemInstanceId);

    private sealed class EquipmentDestinationRow
    {
        public string EquipmentSlotId { get; set; } = string.Empty;

        public int MaximumQuantity { get; set; }

        public Guid? OccupantItemInstanceId { get; set; }
    }

    private sealed class EquippedBlockedSlotRow
    {
        public string EquipmentSlotId { get; set; } = string.Empty;

        public string? BlockedEquipmentSlotId { get; set; }
    }
}
