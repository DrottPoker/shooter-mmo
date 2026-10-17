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

    public async Task<int> SelectDestinationSlotAsync(
        LockedContainer container,
        ResolvedItemDefinition definition,
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
                row.OccupantItemInstanceId
            })
            .Select(group => new DestinationSlot(
                group.Key.SlotIndex,
                group.Key.SlotKind,
                group.Where(row => row.TagId is not null).Select(row => row.TagId!).ToArray(),
                group.Key.EquipmentSlotId,
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
            slot.OccupantItemInstanceId is null && IsCompatible(slot));
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

    public async Task EnsureEquipmentDestinationAsync(
        Guid characterId,
        string equipmentSlotId,
        ResolvedItemDefinition definition,
        bool bagHasContents,
        CancellationToken cancellationToken)
    {
        var slot = await Connection.QuerySingleOrDefaultAsync<EquipmentDestinationRow>(
            new CommandDefinition(
                """
                select
                    equipment.id as "EquipmentSlotId",
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

        if (slot.OccupantItemInstanceId is not null)
        {
            Reject(
                ItemTransactionErrorCodes.EquipmentSlotOccupied,
                "The requested equipment slot is occupied.");
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

        public string? TagId { get; set; }

        public Guid? OccupantItemInstanceId { get; set; }
    }

    private sealed record DestinationSlot(
        int SlotIndex,
        string SlotKind,
        string[] AcceptedTags,
        string? EquipmentSlotId,
        Guid? OccupantItemInstanceId);

    private sealed class EquipmentDestinationRow
    {
        public string EquipmentSlotId { get; set; } = string.Empty;

        public Guid? OccupantItemInstanceId { get; set; }
    }
}
