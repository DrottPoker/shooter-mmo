using Dapper;

namespace AuthService.Items;

public sealed partial class ItemTransactionService
{
    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ExpireRecoveryDeliveryCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.ExpireRecoveryDelivery,
            null,
            ExecuteExpireRecoveryDeliveryAsync,
            cancellationToken);
    }

    private static async Task ExecuteExpireRecoveryDeliveryAsync(
        ItemTransactionContext context,
        ExpireRecoveryDeliveryCommand command,
        CancellationToken cancellationToken)
    {
        context.EnsureSystemAuthority();
        if (command.RecoveryDeliveryId == Guid.Empty)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.RecoveryDeliveryNotFound,
                "A Recovery delivery id is required for expiry cleanup.");
        }

        var preview = await LoadRecoveryDeliveryAsync(
            context,
            command.RecoveryDeliveryId,
            false,
            cancellationToken);
        if (preview is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.RecoveryDeliveryNotFound,
                "The Recovery delivery no longer exists.");
        }

        await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(preview.CharacterId, null)],
            cancellationToken);
        await context.LockMutationScopeAsync(
            preview.Items.Select(item => item.ItemInstanceId),
            [preview.RecoveryStorageContainerId],
            cancellationToken);
        var delivery = await LoadRecoveryDeliveryAsync(
            context,
            command.RecoveryDeliveryId,
            true,
            cancellationToken);
        if (delivery is null
            || delivery.CharacterId != preview.CharacterId
            || delivery.RecoveryStorageContainerId != preview.RecoveryStorageContainerId
            || delivery.ClaimedAt is not null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.RecoveryDeliveryNotFound,
                "The Recovery delivery changed before expiry cleanup acquired custody.");
        }

        if (!delivery.IsExpired)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.RecoveryDeliveryNotExpired,
                "The Recovery delivery has not reached its expiry deadline.");
        }

        foreach (var deliveryItem in delivery.Items.OrderBy(item => item.ItemOrder))
        {
            var item = await context.LoadItemAsync(
                deliveryItem.ItemInstanceId,
                cancellationToken);
            if (item is null
                || item.OwningCharacterId != delivery.CharacterId
                || item.ContainerId != delivery.RecoveryStorageContainerId
                || item.ContainerSlotIndex != deliveryItem.ContainerSlotIndex)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.ItemStateConflict,
                    "Recovery expiry found item custody that no longer matches the delivery.");
            }

            if (await context.BagHasContentsAsync(item, cancellationToken))
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.BagStateChanged,
                    "Recovery expiry cannot destroy a Bag aggregate with child items.");
            }

            var beforeState = await context.CaptureItemStateJsonAsync(
                item.ItemInstanceId,
                cancellationToken);
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                insert into item_destructions (
                    item_instance_id,
                    definition_id,
                    source_operation_id,
                    quantity,
                    reason)
                values (
                    @ItemInstanceId,
                    @DefinitionId,
                    @OperationId,
                    @Quantity,
                    'recovery_expired');

                delete from recovery_delivery_items
                where recovery_delivery_id = @DeliveryId
                  and item_instance_id = @ItemInstanceId;

                delete from item_container_slots
                where container_id = @ContainerId
                  and slot_index = @SlotIndex;

                delete from item_instances
                where id = @ItemInstanceId;
                """,
                new
                {
                    item.ItemInstanceId,
                    item.DefinitionId,
                    OperationId = context.OperationId,
                    item.Quantity,
                    DeliveryId = delivery.DeliveryId,
                    ContainerId = delivery.RecoveryStorageContainerId,
                    SlotIndex = deliveryItem.ContainerSlotIndex
                },
                context.Transaction,
                cancellationToken: cancellationToken));
            context.AddItemAudit(
                "recovery_delivery_expired",
                item.ItemInstanceId,
                beforeState,
                null);
        }

        await context.Connection.ExecuteAsync(new CommandDefinition(
            "delete from recovery_deliveries where id = @DeliveryId;",
            new { DeliveryId = delivery.DeliveryId },
            context.Transaction,
            cancellationToken: cancellationToken));
        context.TouchContainer(delivery.RecoveryStorageContainerId);
        context.TouchCharacter(delivery.CharacterId);
        context.AddMetadataAudit(
            "recovery_delivery_expired",
            new
            {
                delivery.DeliveryId,
                delivery.CharacterId,
                ItemCount = delivery.Items.Count
            },
            null);
    }
}
