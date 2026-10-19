using Dapper;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Item writes for corpse transfers: move, split, merge, and swap, each with its
// audit change kinds.
internal static class CorpseCustodyWrites
{
    public static async Task MoveCorpseItemAsync(
        ItemTransactionContext context,
        LockedItem source,
        LockedContainer destination,
        int destinationSlotIndex,
        CancellationToken cancellationToken)
    {
        await MoveCorpseItemAsync(
            context,
            source,
            destination,
            destinationSlotIndex,
            "corpse_item_looted",
            cancellationToken);
    }

    public static async Task MoveCorpseItemAsync(
        ItemTransactionContext context,
        LockedItem source,
        LockedContainer destination,
        int destinationSlotIndex,
        string auditKind,
        CancellationToken cancellationToken)
    {
        var before = await context.Journal.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
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
                DestinationContainerId = destination.ContainerId,
                DestinationSlotIndex = destinationSlotIndex,
                source.ItemInstanceId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        var after = await context.Journal.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit(
            auditKind,
            source.ItemInstanceId,
            before,
            after);
        context.Journal.IncludeResultItem(source.ItemInstanceId);
    }

    public static async Task SplitCorpseQuantityAsync(
        ItemTransactionContext context,
        LockedItem source,
        IReadOnlyList<LockedItemPolicy> policies,
        int quantity,
        LockedContainer destination,
        int destinationSlotIndex,
        CancellationToken cancellationToken)
    {
        await SplitCorpseQuantityAsync(
            context,
            source,
            policies,
            quantity,
            destination,
            destinationSlotIndex,
            "corpse_stack_loot_source",
            "corpse_stack_loot_created",
            cancellationToken);
    }

    public static async Task SplitCorpseQuantityAsync(
        ItemTransactionContext context,
        LockedItem source,
        IReadOnlyList<LockedItemPolicy> policies,
        int quantity,
        LockedContainer destination,
        int destinationSlotIndex,
        string sourceAuditKind,
        string createdAuditKind,
        CancellationToken cancellationToken)
    {
        var sourceBefore = await context.Journal.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var createdItemId = Guid.NewGuid();
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update item_instances
            set quantity = quantity - @Quantity,
                revision = revision + 1,
                updated_at = now()
            where id = @SourceItemInstanceId;

            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (
                @CreatedItemId,
                @DefinitionId,
                @Quantity,
                @DestinationContainerId,
                @DestinationSlotIndex);
            """,
            new
            {
                Quantity = quantity,
                SourceItemInstanceId = source.ItemInstanceId,
                CreatedItemId = createdItemId,
                source.DefinitionId,
                DestinationContainerId = destination.ContainerId,
                DestinationSlotIndex = destinationSlotIndex
            },
            context.Transaction,
            cancellationToken: cancellationToken));

        foreach (var policy in policies.Where(policy => string.Equals(
                     policy.Status,
                     ItemPolicyRules.ActiveStatus,
                     StringComparison.Ordinal)))
        {
            await context.Connection.ExecuteAsync(new CommandDefinition(
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
                    @PolicyKind,
                    @SourceKind,
                    @SourceId);
                """,
                new
                {
                    PolicyId = Guid.NewGuid(),
                    ItemInstanceId = createdItemId,
                    policy.PolicyKind,
                    policy.SourceKind,
                    policy.SourceId
                },
                context.Transaction,
                cancellationToken: cancellationToken));
        }

        var sourceAfter = await context.Journal.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var createdAfter = await context.Journal.CaptureItemStateJsonAsync(
            createdItemId,
            cancellationToken);
        context.Journal.AddItemAudit(
            sourceAuditKind,
            source.ItemInstanceId,
            sourceBefore,
            sourceAfter);
        context.Journal.AddItemAudit(
            createdAuditKind,
            createdItemId,
            null,
            createdAfter);
        context.Journal.IncludeResultItem(source.ItemInstanceId);
        context.Journal.IncludeResultItem(createdItemId);
    }

    public static async Task MergeCorpseQuantityAsync(
        ItemTransactionContext context,
        LockedItem source,
        LockedItem target,
        int quantity,
        bool isPartial,
        CancellationToken cancellationToken)
    {
        await MergeCorpseQuantityAsync(
            context,
            source,
            target,
            quantity,
            isPartial,
            "corpse_stack_loot_source",
            "corpse_stack_loot_merged",
            cancellationToken);
    }

    public static async Task MergeCorpseQuantityAsync(
        ItemTransactionContext context,
        LockedItem source,
        LockedItem target,
        int quantity,
        bool isPartial,
        string sourceAuditKind,
        string targetAuditKind,
        CancellationToken cancellationToken)
    {
        var sourceBefore = await context.Journal.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var targetBefore = await context.Journal.CaptureItemStateJsonAsync(
            target.ItemInstanceId,
            cancellationToken);
        if (isPartial)
        {
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                update item_instances
                set quantity = quantity - @Quantity,
                    revision = revision + 1,
                    updated_at = now()
                where id = @SourceItemInstanceId;

                update item_instances
                set quantity = quantity + @Quantity,
                    revision = revision + 1,
                    updated_at = now()
                where id = @TargetItemInstanceId;
                """,
                new
                {
                    Quantity = quantity,
                    SourceItemInstanceId = source.ItemInstanceId,
                    TargetItemInstanceId = target.ItemInstanceId
                },
                context.Transaction,
                cancellationToken: cancellationToken));
        }
        else
        {
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                update item_instances
                set quantity = quantity + @Quantity,
                    revision = revision + 1,
                    updated_at = now()
                where id = @TargetItemInstanceId;

                delete from item_instances
                where id = @SourceItemInstanceId;
                """,
                new
                {
                    Quantity = quantity,
                    SourceItemInstanceId = source.ItemInstanceId,
                    TargetItemInstanceId = target.ItemInstanceId
                },
                context.Transaction,
                cancellationToken: cancellationToken));
        }

        var sourceAfter = isPartial
            ? await context.Journal.CaptureItemStateJsonAsync(source.ItemInstanceId, cancellationToken)
            : null;
        var targetAfter = await context.Journal.CaptureItemStateJsonAsync(
            target.ItemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit(
            isPartial ? sourceAuditKind : sourceAuditKind + "_removed",
            source.ItemInstanceId,
            sourceBefore,
            sourceAfter);
        context.Journal.AddItemAudit(
            targetAuditKind,
            target.ItemInstanceId,
            targetBefore,
            targetAfter);
        if (isPartial)
        {
            context.Journal.IncludeResultItem(source.ItemInstanceId);
        }

        context.Journal.IncludeResultItem(target.ItemInstanceId);
    }

    public static async Task EquipCorpseItemAsync(
        ItemTransactionContext context,
        LockedItem source,
        Guid characterId,
        string equipmentSlotId,
        CancellationToken cancellationToken)
    {
        var before = await context.Journal.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update item_instances
            set container_id = null,
                container_slot_index = null,
                equipped_character_id = @CharacterId,
                equipment_slot_id = @EquipmentSlotId,
                revision = revision + 1,
                updated_at = now()
            where id = @ItemInstanceId;
            """,
            new
            {
                CharacterId = characterId,
                EquipmentSlotId = equipmentSlotId,
                source.ItemInstanceId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        var after = await context.Journal.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit(
            "corpse_item_looted_to_equipment",
            source.ItemInstanceId,
            before,
            after);
        context.Journal.IncludeResultItem(source.ItemInstanceId);
    }

    // The corpse item becomes equipped and the equipped item takes its corpse slot.
    public static async Task SwapCorpseItemWithEquipmentAsync(
        ItemTransactionContext context,
        LockedItem source,
        LockedItem equipped,
        Guid characterId,
        string equipmentSlotId,
        CancellationToken cancellationToken)
    {
        var sourceBefore = await context.Journal.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var equippedBefore = await context.Journal.CaptureItemStateJsonAsync(
            equipped.ItemInstanceId,
            cancellationToken);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            set constraints ux_item_instances_equipment_slot deferred;
            set constraints ux_item_instances_container_slot deferred;

            update item_instances
            set container_id = case id
                    when @SourceItemInstanceId then null
                    when @EquippedItemInstanceId then @SourceContainerId
                end,
                container_slot_index = case id
                    when @SourceItemInstanceId then null
                    when @EquippedItemInstanceId then @SourceSlotIndex
                end,
                equipped_character_id = case id
                    when @SourceItemInstanceId then @CharacterId
                    when @EquippedItemInstanceId then null
                end,
                equipment_slot_id = case id
                    when @SourceItemInstanceId then @EquipmentSlotId
                    when @EquippedItemInstanceId then null
                end,
                revision = revision + 1,
                updated_at = now()
            where id in (@SourceItemInstanceId, @EquippedItemInstanceId);
            """,
            new
            {
                SourceItemInstanceId = source.ItemInstanceId,
                EquippedItemInstanceId = equipped.ItemInstanceId,
                SourceContainerId = source.ContainerId!.Value,
                SourceSlotIndex = source.ContainerSlotIndex!.Value,
                CharacterId = characterId,
                EquipmentSlotId = equipmentSlotId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        var sourceAfter = await context.Journal.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var equippedAfter = await context.Journal.CaptureItemStateJsonAsync(
            equipped.ItemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit(
            "corpse_item_swapped_to_equipment",
            source.ItemInstanceId,
            sourceBefore,
            sourceAfter);
        context.Journal.AddItemAudit(
            "equipped_item_swapped_to_corpse",
            equipped.ItemInstanceId,
            equippedBefore,
            equippedAfter);
        context.Journal.IncludeResultItem(source.ItemInstanceId);
        context.Journal.IncludeResultItem(equipped.ItemInstanceId);
    }

    public static async Task SwapCorpseContainerItemsAsync(
        ItemTransactionContext context,
        LockedItem source,
        LockedItem target,
        string sourceAuditKind,
        string targetAuditKind,
        CancellationToken cancellationToken)
    {
        if (source.ContainerId is null
            || source.ContainerSlotIndex is null
            || target.ContainerId is null
            || target.ContainerSlotIndex is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "Both swapped items must remain in their expected container slots.");
        }

        var sourceBefore = await context.Journal.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var targetBefore = await context.Journal.CaptureItemStateJsonAsync(
            target.ItemInstanceId,
            cancellationToken);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            set constraints ux_item_instances_container_slot deferred;

            update item_instances
            set container_id = case id
                    when @SourceItemInstanceId then @TargetContainerId
                    when @TargetItemInstanceId then @SourceContainerId
                end,
                container_slot_index = case id
                    when @SourceItemInstanceId then @TargetSlotIndex
                    when @TargetItemInstanceId then @SourceSlotIndex
                end,
                equipped_character_id = null,
                equipment_slot_id = null,
                revision = revision + 1,
                updated_at = now()
            where id in (@SourceItemInstanceId, @TargetItemInstanceId);
            """,
            new
            {
                SourceItemInstanceId = source.ItemInstanceId,
                TargetItemInstanceId = target.ItemInstanceId,
                SourceContainerId = source.ContainerId.Value,
                SourceSlotIndex = source.ContainerSlotIndex.Value,
                TargetContainerId = target.ContainerId.Value,
                TargetSlotIndex = target.ContainerSlotIndex.Value
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        var sourceAfter = await context.Journal.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var targetAfter = await context.Journal.CaptureItemStateJsonAsync(
            target.ItemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit(
            sourceAuditKind,
            source.ItemInstanceId,
            sourceBefore,
            sourceAfter);
        context.Journal.AddItemAudit(
            targetAuditKind,
            target.ItemInstanceId,
            targetBefore,
            targetAfter);
        context.Journal.IncludeResultItem(source.ItemInstanceId);
        context.Journal.IncludeResultItem(target.ItemInstanceId);
    }
}
