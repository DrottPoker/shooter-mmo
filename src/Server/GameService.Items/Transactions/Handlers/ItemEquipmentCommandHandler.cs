using Dapper;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Equips a whole item or stack from a container, or part of a stack into an empty slot, and
// unequips an item into a container slot. Equipping a whole item onto an occupied slot swaps: the
// equipped item moves into the slot the new item came from, in one transaction. The expected
// character revision guarantees the swap replaces the occupant the player saw.
internal sealed class ItemEquipmentCommandHandler
    : IItemCommandHandler<EquipItemCommand>,
    IItemCommandHandler<UnequipItemCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        EquipItemCommand command,
        CancellationToken cancellationToken)
    {
        await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        var occupantId = await context.State.LoadEquippedItemIdAsync(
            command.CharacterId,
            command.EquipmentSlotId,
            cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            occupantId is null ? [command.ItemInstanceId] : [command.ItemInstanceId, occupantId.Value],
            [],
            cancellationToken);
        var item = await context.State.LoadOwnedItemAsync(
            command.ItemInstanceId,
            command.CharacterId,
            command.ExpectedItemRevision,
            cancellationToken);
        if (item.ContainerId is null
            || item.ContainerSlotIndex is null
            || string.Equals(item.SourceContainerType, "recovery_storage", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.EquipmentSlotIncompatible,
                "The item is not in a container that can equip directly.");
        }

        var definition = await context.State.LoadDefinitionAsync(item.DefinitionId, cancellationToken);
        var quantity = command.Quantity ?? item.Quantity;
        if (quantity <= 0 || quantity > item.Quantity)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemQuantityChanged,
                "An equip quantity must be positive and at most the stack quantity.");
        }

        if (quantity < item.Quantity)
        {
            await EquipPartAsync(context, command, item, definition, quantity, occupantId, cancellationToken);
            return;
        }

        var bagHasContents = await context.State.BagHasContentsAsync(item, cancellationToken);
        await context.Placement.EnsureEquipmentDestinationAsync(
            command.CharacterId,
            command.EquipmentSlotId,
            definition,
            item.Quantity,
            bagHasContents,
            cancellationToken,
            occupantId);
        var occupant = occupantId is null
            ? null
            : await LoadSwapOccupantAsync(context, command, item, definition, occupantId.Value, cancellationToken);
        var beforeState = await context.Journal.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        var occupantBeforeState = occupant is null
            ? null
            : await context.Journal.CaptureItemStateJsonAsync(occupant.ItemInstanceId, cancellationToken);

        if (occupant is null)
        {
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
                    command.CharacterId,
                    command.EquipmentSlotId,
                    item.ItemInstanceId
                },
                context.Transaction,
                cancellationToken: cancellationToken));
        }
        else
        {
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                set constraints ux_item_instances_equipment_slot deferred;
                set constraints ux_item_instances_container_slot deferred;

                update item_instances
                set container_id = case id
                        when @ItemInstanceId then null
                        when @OccupantItemInstanceId then @SourceContainerId
                    end,
                    container_slot_index = case id
                        when @ItemInstanceId then null
                        when @OccupantItemInstanceId then @SourceSlotIndex
                    end,
                    equipped_character_id = case id
                        when @ItemInstanceId then @CharacterId
                        when @OccupantItemInstanceId then null
                    end,
                    equipment_slot_id = case id
                        when @ItemInstanceId then @EquipmentSlotId
                        when @OccupantItemInstanceId then null
                    end,
                    revision = revision + 1,
                    updated_at = now()
                where id in (@ItemInstanceId, @OccupantItemInstanceId);
                """,
                new
                {
                    item.ItemInstanceId,
                    OccupantItemInstanceId = occupant.ItemInstanceId,
                    SourceContainerId = item.ContainerId!.Value,
                    SourceSlotIndex = item.ContainerSlotIndex!.Value,
                    command.CharacterId,
                    command.EquipmentSlotId
                },
                context.Transaction,
                cancellationToken: cancellationToken));
            await SetBagContentsLifecycleAsync(context, occupant, "closed", cancellationToken);
        }

        await SetBagContentsLifecycleAsync(context, item, "active", cancellationToken);

        var afterState = await context.Journal.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit("item_equipped", item.ItemInstanceId, beforeState, afterState);
        if (occupant is not null)
        {
            var occupantAfterState = await context.Journal.CaptureItemStateJsonAsync(
                occupant.ItemInstanceId,
                cancellationToken);
            context.Journal.AddItemAudit(
                "item_unequipped",
                occupant.ItemInstanceId,
                occupantBeforeState!,
                occupantAfterState);
            context.Journal.IncludeResultItem(occupant.ItemInstanceId);
        }

        context.Journal.TouchContainer(item.ContainerId);
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.IncludeResultItem(item.ItemInstanceId);
    }

    // Equips part of a stack, such as some arrows, as a new stack in an empty slot; the rest
    // stays where it was. A part never swaps, and topping up an equipped stack is a merge.
    private static async Task EquipPartAsync(
        ItemTransactionContext context,
        EquipItemCommand command,
        LockedItem source,
        ResolvedItemDefinition definition,
        int quantity,
        Guid? occupantId,
        CancellationToken cancellationToken)
    {
        if (definition.IsBag || definition.RuntimeDefinition.MaximumStackSize <= 1)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStackIncompatible,
                "The item definition is not stackable.");
        }

        if (occupantId is not null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.EquipmentSlotOccupied,
                "Part of a stack equips only into an empty slot; it tops up an equipped stack through a merge.");
        }

        await context.Placement.EnsureEquipmentDestinationAsync(
            command.CharacterId,
            command.EquipmentSlotId,
            definition,
            quantity,
            false,
            cancellationToken);
        var policies = await context.State.LoadPoliciesAsync(source.ItemInstanceId, cancellationToken);
        var beforeSource = await context.Journal.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var equippedItemInstanceId = Guid.NewGuid();
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
                equipped_character_id,
                equipment_slot_id)
            values (
                @EquippedItemInstanceId,
                @DefinitionId,
                @Quantity,
                @CharacterId,
                @EquipmentSlotId);
            """,
            new
            {
                Quantity = quantity,
                SourceItemInstanceId = source.ItemInstanceId,
                EquippedItemInstanceId = equippedItemInstanceId,
                source.DefinitionId,
                command.CharacterId,
                command.EquipmentSlotId
            },
            context.Transaction,
            cancellationToken: cancellationToken));

        foreach (var policy in policies.Where(policy =>
                     string.Equals(policy.Status, ItemPolicyRules.ActiveStatus, StringComparison.Ordinal)))
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
                    ItemInstanceId = equippedItemInstanceId,
                    policy.PolicyKind,
                    policy.SourceKind,
                    policy.SourceId
                },
                context.Transaction,
                cancellationToken: cancellationToken));
        }

        context.Journal.AddItemAudit(
            "stack_split_source",
            source.ItemInstanceId,
            beforeSource,
            await context.Journal.CaptureItemStateJsonAsync(source.ItemInstanceId, cancellationToken));
        context.Journal.AddItemAudit(
            "item_equipped",
            equippedItemInstanceId,
            null,
            await context.Journal.CaptureItemStateJsonAsync(equippedItemInstanceId, cancellationToken));
        context.Journal.TouchContainer(source.ContainerId);
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.IncludeResultItem(source.ItemInstanceId);
        context.Journal.IncludeResultItem(equippedItemInstanceId);
    }

    // The equipped item that a swap moves into the new item's source slot. The slot must accept
    // it as an unequip into that slot would, apart from the new item that leaves it. A stack of
    // the equipped definition tops up the equipped stack through a merge and never swaps.
    private static async Task<LockedItem> LoadSwapOccupantAsync(
        ItemTransactionContext context,
        EquipItemCommand command,
        LockedItem item,
        ResolvedItemDefinition definition,
        Guid occupantId,
        CancellationToken cancellationToken)
    {
        var occupant = await context.State.LoadItemAsync(occupantId, cancellationToken);
        if (occupant is null
            || occupant.EquippedCharacterId != command.CharacterId
            || !string.Equals(occupant.EquipmentSlotId, command.EquipmentSlotId, StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The equipped item changed before the swap committed.");
        }

        if (string.Equals(occupant.DefinitionId, item.DefinitionId, StringComparison.Ordinal)
            && definition.RuntimeDefinition.MaximumStackSize > 1)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.EquipmentSlotOccupied,
                "A stack of the equipped definition tops up the equipped stack through a merge.");
        }

        var source = await context.State.LoadOwnedContainerAsync(
            item.ContainerId!.Value,
            command.CharacterId,
            cancellationToken);
        var occupantDefinition = await context.State.LoadDefinitionAsync(
            occupant.DefinitionId,
            cancellationToken);
        await context.Placement.SelectDestinationSlotAsync(
            source,
            occupantDefinition,
            occupant.Quantity,
            item.ContainerSlotIndex,
            await context.State.BagHasContentsAsync(occupant, cancellationToken),
            occupant.IsBag ? occupant.ItemInstanceId : null,
            false,
            cancellationToken,
            item.ItemInstanceId);
        return occupant;
    }

    // An equipped Bag's contents are active; a Bag in ordinary storage keeps them closed.
    private static async Task SetBagContentsLifecycleAsync(
        ItemTransactionContext context,
        LockedItem item,
        string lifecycle,
        CancellationToken cancellationToken)
    {
        if (!item.IsBag)
        {
            return;
        }

        if (item.BagContentsContainerId is null)
        {
            throw new InvalidOperationException(
                $"Bag item '{item.ItemInstanceId}' has no contents container.");
        }

        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update item_containers
            set lifecycle = @Lifecycle,
                updated_at = now()
            where id = @ContainerId;
            """,
            new { ContainerId = item.BagContentsContainerId.Value, Lifecycle = lifecycle },
            context.Transaction,
            cancellationToken: cancellationToken));
        context.Journal.TouchContainer(item.BagContentsContainerId);
    }

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        UnequipItemCommand command,
        CancellationToken cancellationToken)
    {
        await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            [command.ItemInstanceId],
            [command.DestinationContainerId],
            cancellationToken);
        var item = await context.State.LoadOwnedItemAsync(
            command.ItemInstanceId,
            command.CharacterId,
            command.ExpectedItemRevision,
            cancellationToken);
        if (item.EquippedCharacterId != command.CharacterId)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.EquipmentSlotIncompatible,
                "The item is not equipped by the requested character.");
        }

        var destination = await context.State.LoadOwnedContainerAsync(
            command.DestinationContainerId,
            command.CharacterId,
            cancellationToken);
        var definition = await context.State.LoadDefinitionAsync(item.DefinitionId, cancellationToken);
        var bagHasContents = await context.State.BagHasContentsAsync(item, cancellationToken);
        var slotIndex = await context.Placement.SelectDestinationSlotAsync(
            destination,
            definition,
            item.Quantity,
            command.DestinationSlotIndex,
            bagHasContents,
            item.IsBag ? item.ItemInstanceId : null,
            false,
            cancellationToken);
        var beforeState = await context.Journal.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
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
                DestinationSlotIndex = slotIndex,
                item.ItemInstanceId
            },
            context.Transaction,
            cancellationToken: cancellationToken));

        if (item.IsBag && item.BagContentsContainerId is not null)
        {
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                update item_containers
                set lifecycle = 'closed',
                    updated_at = now()
                where id = @ContainerId;
                """,
                new { ContainerId = item.BagContentsContainerId.Value },
                context.Transaction,
                cancellationToken: cancellationToken));
            context.Journal.TouchContainer(item.BagContentsContainerId);
        }

        var afterState = await context.Journal.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit("item_unequipped", item.ItemInstanceId, beforeState, afterState);
        context.Journal.TouchContainer(destination.ContainerId);
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.IncludeResultItem(item.ItemInstanceId);
    }
}
