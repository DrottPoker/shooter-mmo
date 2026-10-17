using Dapper;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Moves an item to another container slot or swaps two container items.
internal sealed class ItemRelocationCommandHandler
    : IItemCommandHandler<RelocateItemCommand>,
    IItemCommandHandler<SwapContainerItemsCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        RelocateItemCommand command,
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
        if (item.ContainerId is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemSlotIncompatible,
                "Relocate accepts only an item currently assigned to a container slot.");
        }

        if (string.Equals(item.SourceContainerType, "recovery_storage", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.RecoveryAccessRequired,
                "Recovery Storage items must be withdrawn through a delivery claim.");
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

        var afterState = await context.Journal.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit("item_relocated", item.ItemInstanceId, beforeState, afterState);
        context.Journal.TouchContainer(item.ContainerId);
        context.Journal.TouchContainer(destination.ContainerId);
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.IncludeResultItem(item.ItemInstanceId);
    }

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        SwapContainerItemsCommand command,
        CancellationToken cancellationToken)
    {
        if (command.FirstItemInstanceId == command.SecondItemInstanceId)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemSlotIncompatible,
                "An item cannot swap its container slot with itself.");
        }

        await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            [command.FirstItemInstanceId, command.SecondItemInstanceId],
            [],
            cancellationToken);
        var firstItem = await context.State.LoadOwnedItemAsync(
            command.FirstItemInstanceId,
            command.CharacterId,
            command.ExpectedFirstItemRevision,
            cancellationToken);
        var secondItem = await context.State.LoadOwnedItemAsync(
            command.SecondItemInstanceId,
            command.CharacterId,
            command.ExpectedSecondItemRevision,
            cancellationToken);
        if (firstItem.ContainerId is null
            || firstItem.ContainerSlotIndex is null
            || secondItem.ContainerId is null
            || secondItem.ContainerSlotIndex is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemSlotIncompatible,
                "Ordinary item swaps require two container-assigned items.");
        }

        if (string.Equals(
                firstItem.SourceContainerType,
                "recovery_storage",
                StringComparison.Ordinal)
            || string.Equals(
                secondItem.SourceContainerType,
                "recovery_storage",
                StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.RecoveryAccessRequired,
                "Recovery Storage items must be withdrawn through a delivery claim.");
        }

        var firstContainer = await context.State.LoadOwnedContainerAsync(
            firstItem.ContainerId.Value,
            command.CharacterId,
            cancellationToken);
        var secondContainer = await context.State.LoadOwnedContainerAsync(
            secondItem.ContainerId.Value,
            command.CharacterId,
            cancellationToken);
        var firstDefinition = await context.State.LoadDefinitionAsync(
            firstItem.DefinitionId,
            cancellationToken);
        var secondDefinition = await context.State.LoadDefinitionAsync(
            secondItem.DefinitionId,
            cancellationToken);
        var firstBagHasContents = await context.State.BagHasContentsAsync(
            firstItem,
            cancellationToken);
        var secondBagHasContents = await context.State.BagHasContentsAsync(
            secondItem,
            cancellationToken);

        await context.Placement.SelectDestinationSlotAsync(
            secondContainer,
            firstDefinition,
            firstItem.Quantity,
            secondItem.ContainerSlotIndex.Value,
            firstBagHasContents,
            firstItem.IsBag ? firstItem.ItemInstanceId : null,
            false,
            cancellationToken,
            secondItem.ItemInstanceId);
        await context.Placement.SelectDestinationSlotAsync(
            firstContainer,
            secondDefinition,
            secondItem.Quantity,
            firstItem.ContainerSlotIndex.Value,
            secondBagHasContents,
            secondItem.IsBag ? secondItem.ItemInstanceId : null,
            false,
            cancellationToken,
            firstItem.ItemInstanceId);

        var firstBeforeState = await context.Journal.CaptureItemStateJsonAsync(
            firstItem.ItemInstanceId,
            cancellationToken);
        var secondBeforeState = await context.Journal.CaptureItemStateJsonAsync(
            secondItem.ItemInstanceId,
            cancellationToken);

        await context.Connection.ExecuteAsync(new CommandDefinition(
            "set constraints ux_item_instances_container_slot deferred;",
            transaction: context.Transaction,
            cancellationToken: cancellationToken));
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update item_instances
            set container_id = case
                    when id = @FirstItemInstanceId then @SecondContainerId
                    else @FirstContainerId
                end,
                container_slot_index = case
                    when id = @FirstItemInstanceId then @SecondContainerSlotIndex
                    else @FirstContainerSlotIndex
                end,
                revision = revision + 1,
                updated_at = now()
            where id = any(@ItemInstanceIds);
            """,
            new
            {
                command.FirstItemInstanceId,
                command.SecondItemInstanceId,
                ItemInstanceIds = new[]
                {
                    command.FirstItemInstanceId,
                    command.SecondItemInstanceId
                },
                FirstContainerId = firstContainer.ContainerId,
                FirstContainerSlotIndex = firstItem.ContainerSlotIndex.Value,
                SecondContainerId = secondContainer.ContainerId,
                SecondContainerSlotIndex = secondItem.ContainerSlotIndex.Value
            },
            context.Transaction,
            cancellationToken: cancellationToken));

        var firstAfterState = await context.Journal.CaptureItemStateJsonAsync(
            firstItem.ItemInstanceId,
            cancellationToken);
        var secondAfterState = await context.Journal.CaptureItemStateJsonAsync(
            secondItem.ItemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit(
            "container_items_swapped",
            firstItem.ItemInstanceId,
            firstBeforeState,
            firstAfterState);
        context.Journal.AddItemAudit(
            "container_items_swapped",
            secondItem.ItemInstanceId,
            secondBeforeState,
            secondAfterState);
        context.Journal.TouchContainer(firstContainer.ContainerId);
        context.Journal.TouchContainer(secondContainer.ContainerId);
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.IncludeResultItem(firstItem.ItemInstanceId);
        context.Journal.IncludeResultItem(secondItem.ItemInstanceId);
    }
}
