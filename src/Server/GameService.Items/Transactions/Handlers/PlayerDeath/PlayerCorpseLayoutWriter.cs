using System.Text.Json;
using Dapper;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Writes the player corpse layout: the corpse section containers and the snapshots
// of insured equipment and a recovered Bag.
internal static class PlayerCorpseLayoutWriter
{
    internal static async Task CreateCorpseSectionContainersAsync(
        ItemTransactionContext context,
        Guid corpseId,
        Guid sourceInventoryContainerId,
        Guid generalContainerId,
        Guid equipmentContainerId,
        Guid bagContainerId,
        bool useLiveBagAggregate,
        DeathPartitionItem? equippedBag,
        CancellationToken cancellationToken)
    {
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into item_containers (
                id,
                container_type,
                slot_capacity,
                lifecycle)
            select
                @GeneralContainerId,
                'corpse_inventory',
                source.slot_capacity,
                'active'
            from item_containers source
            where source.id = @SourceInventoryContainerId;

            insert into item_container_slots (container_id, slot_index, slot_kind)
            select @GeneralContainerId, slot_index, slot_kind
            from item_container_slots
            where container_id = @SourceInventoryContainerId
            order by slot_index;

            insert into item_container_slot_tags (container_id, slot_index, tag_id)
            select @GeneralContainerId, slot_index, tag_id
            from item_container_slot_tags
            where container_id = @SourceInventoryContainerId
            order by slot_index, tag_id;

            insert into item_containers (
                id,
                container_type,
                slot_capacity,
                lifecycle)
            select
                @EquipmentContainerId,
                'corpse_equipment',
                count(*),
                'active'
            from equipment_slots
            where is_active;

            -- Each corpse equipment slot names its equipment slot, in display order.
            insert into item_container_slots (
                container_id,
                slot_index,
                slot_kind,
                equipment_slot_id)
            select
                @EquipmentContainerId,
                (row_number() over (order by display_order, id) - 1)::integer,
                'general',
                id
            from equipment_slots
            where is_active;
            """,
            new
            {
                GeneralContainerId = generalContainerId,
                SourceInventoryContainerId = sourceInventoryContainerId,
                EquipmentContainerId = equipmentContainerId
            },
            context.Transaction,
            cancellationToken: cancellationToken));

        if (!useLiveBagAggregate)
        {
            if (equippedBag?.Item.BagContentsContainerId is Guid sourceBagContainerId)
            {
                await context.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into item_containers (
                        id,
                        container_type,
                        slot_capacity,
                        lifecycle)
                    select
                        @BagContainerId,
                        'corpse_bag_contents',
                        slot_capacity,
                        'active'
                    from item_containers
                    where id = @SourceBagContainerId;

                    insert into item_container_slots (container_id, slot_index, slot_kind)
                    select @BagContainerId, slot_index, slot_kind
                    from item_container_slots
                    where container_id = @SourceBagContainerId
                    order by slot_index;

                    insert into item_container_slot_tags (container_id, slot_index, tag_id)
                    select @BagContainerId, slot_index, tag_id
                    from item_container_slot_tags
                    where container_id = @SourceBagContainerId
                    order by slot_index, tag_id;
                    """,
                    new { BagContainerId = bagContainerId, SourceBagContainerId = sourceBagContainerId },
                    context.Transaction,
                    cancellationToken: cancellationToken));
            }
            else
            {
                await context.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into item_containers (
                        id,
                        container_type,
                        slot_capacity,
                        lifecycle)
                    values (@BagContainerId, 'corpse_bag_contents', 0, 'active');
                    """,
                    new { BagContainerId = bagContainerId },
                    context.Transaction,
                    cancellationToken: cancellationToken));
            }
        }

        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into corpse_sections (corpse_id, section_kind, container_id)
            values
                (@CorpseId, 'general_inventory', @GeneralContainerId),
                (@CorpseId, 'equipment', @EquipmentContainerId),
                (@CorpseId, 'bag', @BagContainerId);
            """,
            new
            {
                CorpseId = corpseId,
                GeneralContainerId = generalContainerId,
                EquipmentContainerId = equipmentContainerId,
                BagContainerId = bagContainerId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    internal static async Task CreateCorpseSnapshotsAsync(
        ItemTransactionContext context,
        Guid corpseId,
        IReadOnlyList<DeathPartitionItem> items,
        DeathPartitionItem? equippedBag,
        CancellationToken cancellationToken)
    {
        var sortOrder = 0;
        foreach (var item in items
                     .Where(item =>
                         item.Disposition == ItemDeathDisposition.InsuredRecovery
                         && string.Equals(item.Source.SourceKind, "equipment", StringComparison.Ordinal)
                         && !item.IsEquippedBag)
                     .OrderBy(item => item.Source.EquipmentCorpseSlotIndex)
                     .ThenBy(item => item.Item.ItemInstanceId))
        {
            await InsertCorpseSnapshotAsync(
                context,
                corpseId,
                "insured_equipment",
                sortOrder++,
                item.Item.DefinitionId,
                item.Source.EquipmentSlotId,
                ItemPolicyIds.Insured,
                new
                {
                    definitionId = item.Item.DefinitionId,
                    equipmentSlotId = item.Source.EquipmentSlotId,
                    insured = true
                },
                cancellationToken);
        }

        if (equippedBag is not null
            && equippedBag.Disposition is ItemDeathDisposition.ProtectedRecovery
                or ItemDeathDisposition.InsuredRecovery)
        {
            var layoutRows = (await context.Connection.QueryAsync<BagSnapshotSlotRow>(
                new CommandDefinition(
                    """
                    select
                        slot.slot_index as "SlotIndex",
                        slot.slot_kind as "SlotKind",
                        tag.tag_id as "TagId"
                    from bag_definition_slots slot
                    left join bag_definition_slot_tags tag
                      on tag.definition_id = slot.definition_id
                     and tag.slot_index = slot.slot_index
                    where slot.definition_id = @DefinitionId
                    order by slot.slot_index, tag.tag_id;
                    """,
                    new { equippedBag.Item.DefinitionId },
                    context.Transaction,
                    cancellationToken: cancellationToken))).ToArray();
            var slots = layoutRows
                .GroupBy(row => new { row.SlotIndex, row.SlotKind })
                .Select(group => new
                {
                    index = group.Key.SlotIndex,
                    kind = group.Key.SlotKind,
                    acceptedTags = group
                        .Where(row => row.TagId is not null)
                        .Select(row => row.TagId!)
                        .Order(StringComparer.Ordinal)
                        .ToArray()
                })
                .OrderBy(slot => slot.index)
                .ToArray();
            var protectedBag = equippedBag.Disposition == ItemDeathDisposition.ProtectedRecovery;
            await InsertCorpseSnapshotAsync(
                context,
                corpseId,
                protectedBag ? "protected_bag" : "insured_bag",
                sortOrder,
                equippedBag.Item.DefinitionId,
                "bag",
                protectedBag ? ItemPolicyIds.ProtectedOnDeath : ItemPolicyIds.Insured,
                new
                {
                    definitionId = equippedBag.Item.DefinitionId,
                    policyKind = protectedBag
                        ? ItemPolicyIds.ProtectedOnDeath
                        : ItemPolicyIds.Insured,
                    slots
                },
                cancellationToken);
        }
    }

    private static Task InsertCorpseSnapshotAsync(
        ItemTransactionContext context,
        Guid corpseId,
        string snapshotKind,
        int sortOrder,
        string? definitionId,
        string? equipmentSlotId,
        string? policyKind,
        object presentationPayload,
        CancellationToken cancellationToken)
    {
        return context.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into corpse_snapshots (
                id,
                corpse_id,
                snapshot_kind,
                sort_order,
                definition_id,
                equipment_slot_id,
                policy_kind,
                presentation_payload)
            values (
                @SnapshotId,
                @CorpseId,
                @SnapshotKind,
                @SortOrder,
                @DefinitionId,
                @EquipmentSlotId,
                @PolicyKind,
                cast(@PresentationPayload as jsonb));
            """,
            new
            {
                SnapshotId = Guid.NewGuid(),
                CorpseId = corpseId,
                SnapshotKind = snapshotKind,
                SortOrder = sortOrder,
                DefinitionId = definitionId,
                EquipmentSlotId = equipmentSlotId,
                PolicyKind = policyKind,
                PresentationPayload = JsonSerializer.Serialize(
                    presentationPayload,
                    ItemOperationJson.Options)
            },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    private sealed class BagSnapshotSlotRow
    {
        public int SlotIndex { get; set; }

        public string SlotKind { get; set; } = string.Empty;

        public string? TagId { get; set; }
    }
}
