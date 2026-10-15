using Dapper;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Equips an item from a container and unequips it into a container slot.
internal sealed class ItemEquipmentCommandHandler
    : IItemCommandHandler<EquipItemCommand>,
    IItemCommandHandler<UnequipItemCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        EquipItemCommand command,
        CancellationToken cancellationToken)
    {
        await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.LockMutationScopeAsync(
            [command.ItemInstanceId],
            [],
            cancellationToken);
        var item = await context.LoadOwnedItemAsync(
            command.ItemInstanceId,
            command.CharacterId,
            command.ExpectedItemRevision,
            cancellationToken);
        if (item.ContainerId is null
            || string.Equals(item.SourceContainerType, "recovery_storage", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.EquipmentSlotIncompatible,
                "The item is not in a container that can equip directly.");
        }

        if (item.Quantity != 1)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.EquipmentSlotIncompatible,
                "Equipped items must have quantity one.");
        }

        var definition = await context.LoadDefinitionAsync(item.DefinitionId, cancellationToken);
        var bagHasContents = await context.BagHasContentsAsync(item, cancellationToken);
        await context.EnsureEquipmentDestinationAsync(
            command.CharacterId,
            command.EquipmentSlotId,
            definition,
            bagHasContents,
            cancellationToken);
        var beforeState = await context.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
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
                command.CharacterId,
                command.EquipmentSlotId,
                item.ItemInstanceId
            },
            context.Transaction,
            cancellationToken: cancellationToken));

        if (item.IsBag)
        {
            if (item.BagContentsContainerId is null)
            {
                throw new InvalidOperationException(
                    $"Bag item '{item.ItemInstanceId}' has no contents container.");
            }

            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                update item_containers
                set lifecycle = 'active',
                    updated_at = now()
                where id = @ContainerId;
                """,
                new { ContainerId = item.BagContentsContainerId.Value },
                context.Transaction,
                cancellationToken: cancellationToken));
            context.TouchContainer(item.BagContentsContainerId);
        }

        var afterState = await context.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        context.AddItemAudit("item_equipped", item.ItemInstanceId, beforeState, afterState);
        context.TouchContainer(item.ContainerId);
        context.TouchCharacter(command.CharacterId);
        context.IncludeResultItem(item.ItemInstanceId);
    }

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        UnequipItemCommand command,
        CancellationToken cancellationToken)
    {
        await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.LockMutationScopeAsync(
            [command.ItemInstanceId],
            [command.DestinationContainerId],
            cancellationToken);
        var item = await context.LoadOwnedItemAsync(
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

        var destination = await context.LoadOwnedContainerAsync(
            command.DestinationContainerId,
            command.CharacterId,
            cancellationToken);
        var definition = await context.LoadDefinitionAsync(item.DefinitionId, cancellationToken);
        var bagHasContents = await context.BagHasContentsAsync(item, cancellationToken);
        var slotIndex = await context.SelectDestinationSlotAsync(
            destination,
            definition,
            command.DestinationSlotIndex,
            bagHasContents,
            item.IsBag ? item.ItemInstanceId : null,
            false,
            cancellationToken);
        var beforeState = await context.CaptureItemStateJsonAsync(
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
            context.TouchContainer(item.BagContentsContainerId);
        }

        var afterState = await context.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        context.AddItemAudit("item_unequipped", item.ItemInstanceId, beforeState, afterState);
        context.TouchContainer(destination.ContainerId);
        context.TouchCharacter(command.CharacterId);
        context.IncludeResultItem(item.ItemInstanceId);
    }
}
