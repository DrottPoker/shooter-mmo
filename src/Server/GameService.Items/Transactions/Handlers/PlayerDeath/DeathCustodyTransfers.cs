using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Moves partitioned death items into corpse custody or into Recovery Storage
// deliveries and records their audit changes.
internal static class DeathCustodyTransfers
{
    internal static async Task MoveLootableItemToCorpseAsync(
        ItemTransactionContext context,
        DeathPartitionItem item,
        Guid generalContainerId,
        Guid equipmentContainerId,
        Guid bagContainerId,
        bool useLiveBagAggregate,
        CancellationToken cancellationToken)
    {
        Guid destinationContainerId;
        int destinationSlotIndex;
        if (string.Equals(item.Source.SourceKind, "permanent_inventory", StringComparison.Ordinal))
        {
            destinationContainerId = generalContainerId;
            destinationSlotIndex = item.Source.ContainerSlotIndex
                ?? throw new InvalidOperationException("A permanent inventory item has no slot.");
        }
        else if (string.Equals(item.Source.SourceKind, "equipment", StringComparison.Ordinal))
        {
            destinationContainerId = equipmentContainerId;
            destinationSlotIndex = item.Source.EquipmentSortOrder
                ?? throw new InvalidOperationException("An equipped death item has no stable sort order.");
        }
        else
        {
            destinationContainerId = bagContainerId;
            destinationSlotIndex = item.Source.ContainerSlotIndex
                ?? throw new InvalidOperationException("A Bag child item has no slot.");
            if (useLiveBagAggregate
                && item.Source.ContainerId == bagContainerId)
            {
                await context.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    update item_instances
                    set revision = revision + 1,
                        updated_at = now()
                    where id = @ItemInstanceId;
                    """,
                    new { item.Item.ItemInstanceId },
                    context.Transaction,
                    cancellationToken: cancellationToken));
                await RecordDeathItemAuditAsync(
                    context,
                    item,
                    "death_partitioned_to_corpse_bag",
                    cancellationToken);
                context.TouchContainer(item.Source.ContainerId);
                context.IncludeResultItem(item.Item.ItemInstanceId);
                return;
            }
        }

        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update item_instances
            set container_id = @DestinationContainerId,
                container_slot_index = @DestinationSlotIndex,
                equipped_character_id = null,
                equipment_slot_id = null,
                revision = revision + 1,
                updated_at = now()
            where id = @ItemInstanceId;
            """,
            new
            {
                DestinationContainerId = destinationContainerId,
                DestinationSlotIndex = destinationSlotIndex,
                item.Item.ItemInstanceId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        await RecordDeathItemAuditAsync(
            context,
            item,
            "death_partitioned_to_corpse",
            cancellationToken);
        context.TouchContainer(item.Source.ContainerId);
        context.TouchContainer(destinationContainerId);
        context.IncludeResultItem(item.Item.ItemInstanceId);
    }

    internal static async Task AddDeathRecoveryDeliveriesAsync(
        ItemTransactionContext context,
        Guid characterId,
        Guid recoveryStorageContainerId,
        Guid deathEventId,
        IReadOnlyList<DeathRecoveryGroup> requestedGroups,
        CancellationToken cancellationToken)
    {
        var groups = requestedGroups
            .Where(group => group.Items.Count > 0)
            .ToArray();
        if (groups.Length == 0)
        {
            return;
        }

        var nextSlotIndex = await RecoveryDeliveries.GetNextRecoverySlotIndexAsync(
            context,
            recoveryStorageContainerId,
            cancellationToken);
        var sourceEventId = deathEventId.ToString("D");
        var assignments = new List<DeathRecoveryAssignment>();
        foreach (var group in groups)
        {
            RecoveryDeliveries.ValidateRecoverySource(group.SourceKind, sourceEventId);
            var deliveryId = Guid.NewGuid();
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                insert into recovery_deliveries (
                    id,
                    character_id,
                    recovery_storage_container_id,
                    source_kind,
                    source_event_id)
                values (
                    @DeliveryId,
                    @CharacterId,
                    @RecoveryStorageContainerId,
                    @SourceKind,
                    @SourceEventId);
                """,
                new
                {
                    DeliveryId = deliveryId,
                    CharacterId = characterId,
                    RecoveryStorageContainerId = recoveryStorageContainerId,
                    SourceKind = group.SourceKind,
                    SourceEventId = sourceEventId
                },
                context.Transaction,
                cancellationToken: cancellationToken));
            context.AddRecoveryDelivery(deliveryId);

            var orderedGroupItems = group.Items
                .OrderBy(item => item.Item.ItemInstanceId)
                .ToArray();
            for (var itemOrder = 0; itemOrder < orderedGroupItems.Length; itemOrder++)
            {
                assignments.Add(new DeathRecoveryAssignment(
                    group,
                    orderedGroupItems[itemOrder],
                    deliveryId,
                    itemOrder,
                    nextSlotIndex++));
            }
        }

        foreach (var assignment in assignments
                     .OrderBy(assignment => assignment.Item.IsEquippedBag ? 1 : 0)
                     .ThenBy(assignment => assignment.Item.Item.ItemInstanceId))
        {
            var item = assignment.Item;
            if (assignment.Group.ConsumeInsurance)
            {
                var consumed = await context.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    update item_instance_policies
                    set status = 'consumed',
                        revision = revision + 1,
                        consumed_at = now()
                    where item_instance_id = @ItemInstanceId
                      and policy_kind = 'insured'
                      and status = 'active';
                    """,
                    new { item.Item.ItemInstanceId },
                    context.Transaction,
                    cancellationToken: cancellationToken));
                if (consumed != 1)
                {
                    throw new InvalidOperationException(
                        $"Insured death item '{item.Item.ItemInstanceId}' has no active insurance policy.");
                }
            }

            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                insert into item_container_slots (container_id, slot_index, slot_kind)
                values (@RecoveryStorageContainerId, @SlotIndex, 'general');

                update item_instances
                set container_id = @RecoveryStorageContainerId,
                    container_slot_index = @SlotIndex,
                    equipped_character_id = null,
                    equipment_slot_id = null,
                    revision = revision + 1,
                    updated_at = now()
                where id = @ItemInstanceId;

                insert into recovery_delivery_items (
                    recovery_delivery_id,
                    item_instance_id,
                    item_order)
                values (
                    @DeliveryId,
                    @ItemInstanceId,
                    @ItemOrder);
                """,
                new
                {
                    RecoveryStorageContainerId = recoveryStorageContainerId,
                    assignment.SlotIndex,
                    item.Item.ItemInstanceId,
                    assignment.DeliveryId,
                    assignment.ItemOrder
                },
                context.Transaction,
                cancellationToken: cancellationToken));

            if (item.Definition.IsBag && item.Item.BagContentsContainerId is Guid bagContentsId)
            {
                var remainingChildren = await context.Connection.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        """
                        select count(*)
                        from item_instances
                        where container_id = @ContainerId;
                        """,
                        new { ContainerId = bagContentsId },
                        context.Transaction,
                        cancellationToken: cancellationToken));
                if (remainingChildren != 0)
                {
                    throw new InvalidOperationException(
                        $"Recovered Bag '{item.Item.ItemInstanceId}' still has child items after death partition.");
                }

                await context.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    update item_containers
                    set lifecycle = 'closed',
                        updated_at = now()
                    where id = @ContainerId;
                    """,
                    new { ContainerId = bagContentsId },
                    context.Transaction,
                    cancellationToken: cancellationToken));
                context.TouchContainer(bagContentsId);
            }

            await RecordDeathItemAuditAsync(
                context,
                item,
                assignment.Group.ConsumeInsurance
                    ? "death_recovered_insured"
                    : "death_recovered_protected",
                cancellationToken);
            context.TouchContainer(item.Source.ContainerId);
            context.TouchContainer(recoveryStorageContainerId);
            context.IncludeResultItem(item.Item.ItemInstanceId);
        }
    }

    private static async Task RecordDeathItemAuditAsync(
        ItemTransactionContext context,
        DeathPartitionItem item,
        string changeKind,
        CancellationToken cancellationToken)
    {
        var afterState = await context.CaptureItemStateJsonAsync(
            item.Item.ItemInstanceId,
            cancellationToken);
        context.AddItemAudit(
            changeKind,
            item.Item.ItemInstanceId,
            item.BeforeState,
            afterState);
    }
}
