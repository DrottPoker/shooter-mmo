using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Adds items to Recovery Storage as one delivery and claims a delivery back into
// permanent inventory or bank.
internal sealed class RecoveryDeliveryCommandHandler
    : IItemCommandHandler<AddRecoveryDeliveryCommand>,
    IItemCommandHandler<ClaimRecoveryDeliveryCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        AddRecoveryDeliveryCommand command,
        CancellationToken cancellationToken)
    {
        context.Authority.EnsureSystemAuthority();
        RecoveryDeliveries.ValidateRecoverySource(command.SourceKind, command.SourceEventId);
        RecoveryDeliveries.ValidateRecoveryTimeline(command.AvailableAt, command.ExpiresAt);
        var expectations = RecoveryDeliveries.ValidateDistinctItemExpectations(command.Items);
        var states = await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        var state = states[command.CharacterId];
        await context.Locks.LockMutationScopeAsync(
            expectations.Keys,
            [state.RecoveryStorageContainerId],
            cancellationToken);
        var recovery = await context.State.LoadKernelPlacementContainerAsync(
            state.RecoveryStorageContainerId,
            command.CharacterId,
            cancellationToken);
        if (!string.Equals(recovery.ContainerType, "recovery_storage", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Character '{command.CharacterId}' has an invalid Recovery Storage container.");
        }

        var duplicateSource = await context.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists (
                select 1
                from recovery_deliveries
                where character_id = @CharacterId
                  and source_kind = @SourceKind
                  and source_event_id = @SourceEventId);
            """,
            new
            {
                command.CharacterId,
                command.SourceKind,
                command.SourceEventId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        if (duplicateSource)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemOperationConflict,
                "The Recovery Storage source event was already applied.");
        }

        var items = new List<LockedItem>();
        foreach (var expectation in command.Items)
        {
            var item = await context.State.LoadOwnedItemAsync(
                expectation.ItemInstanceId,
                command.CharacterId,
                expectation.Revision,
                cancellationToken);
            if (item.ContainerId == recovery.ContainerId)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.ItemStateConflict,
                    "The item is already in Recovery Storage.");
            }

            if (await context.State.BagHasContentsAsync(item, cancellationToken))
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.BagNotEmpty,
                    "A non-empty Bag cannot enter an ordinary Recovery Storage slot.");
            }

            items.Add(item);
        }

        var deliveryId = Guid.NewGuid();
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into recovery_deliveries (
                id,
                character_id,
                recovery_storage_container_id,
                source_kind,
                source_event_id,
                available_at,
                expires_at)
            values (
                @DeliveryId,
                @CharacterId,
                @RecoveryStorageContainerId,
                @SourceKind,
                @SourceEventId,
                case
                    when cast(@AvailableAt as timestamptz) is null then null
                    else greatest(cast(@AvailableAt as timestamptz), now())
                end,
                case
                    when cast(@ExpiresAt as timestamptz) is null then null
                    else greatest(cast(@ExpiresAt as timestamptz), now())
                end);
            """,
            new
            {
                DeliveryId = deliveryId,
                command.CharacterId,
                RecoveryStorageContainerId = recovery.ContainerId,
                command.SourceKind,
                command.SourceEventId,
                command.AvailableAt,
                command.ExpiresAt
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        var nextSlotIndex = await RecoveryDeliveries.GetNextRecoverySlotIndexAsync(
            context,
            recovery.ContainerId,
            cancellationToken);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var beforeState = await context.Journal.CaptureItemStateJsonAsync(
                item.ItemInstanceId,
                cancellationToken);
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
                    RecoveryStorageContainerId = recovery.ContainerId,
                    SlotIndex = nextSlotIndex + index,
                    item.ItemInstanceId,
                    DeliveryId = deliveryId,
                    ItemOrder = index
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
            context.Journal.AddItemAudit(
                "recovery_delivery_added",
                item.ItemInstanceId,
                beforeState,
                afterState);
            context.Journal.TouchContainer(item.ContainerId);
            context.Journal.IncludeResultItem(item.ItemInstanceId);
        }

        context.Journal.TouchContainer(recovery.ContainerId);
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.AddRecoveryDelivery(deliveryId);
    }

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        ClaimRecoveryDeliveryCommand command,
        CancellationToken cancellationToken)
    {
        var expectedItems = RecoveryDeliveries.ValidateDistinctItemExpectations(command.Items);
        var states = await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        var state = states[command.CharacterId];
        var preview = await RecoveryDeliveries.LoadRecoveryDeliveryAsync(
            context,
            command.RecoveryDeliveryId,
            false,
            cancellationToken);
        if (preview is null || preview.CharacterId != command.CharacterId)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.RecoveryDeliveryNotFound,
                "The Recovery Storage delivery was not found.");
        }

        await context.Locks.LockMutationScopeAsync(
            preview.Items.Select(item => item.ItemInstanceId),
            [state.RecoveryStorageContainerId, command.DestinationContainerId],
            cancellationToken);
        var delivery = await RecoveryDeliveries.LoadRecoveryDeliveryAsync(
            context,
            command.RecoveryDeliveryId,
            true,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Recovery delivery '{command.RecoveryDeliveryId}' disappeared after locking.");
        if (delivery.CharacterId != command.CharacterId
            || delivery.RecoveryStorageContainerId != state.RecoveryStorageContainerId
            || delivery.ClaimedAt is not null
            || !delivery.IsAvailable
            || delivery.IsExpired)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.RecoveryDeliveryNotFound,
                "The Recovery Storage delivery is not currently claimable.");
        }

        if (delivery.Revision != command.ExpectedRecoveryDeliveryRevision)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The Recovery Storage delivery changed before it could be claimed.");
        }

        var actualItemIds = delivery.Items
            .Select(item => item.ItemInstanceId)
            .Order()
            .ToArray();
        if (actualItemIds.Length == 0
            || !actualItemIds.SequenceEqual(expectedItems.Keys.Order()))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The Recovery Storage delivery item set changed before it could be claimed.");
        }

        var destination = await context.State.LoadOwnedContainerAsync(
            command.DestinationContainerId,
            command.CharacterId,
            cancellationToken);
        if (!string.Equals(destination.ContainerType, "permanent_inventory", StringComparison.Ordinal)
            && !string.Equals(destination.ContainerType, "bank", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemSlotIncompatible,
                "Recovery Storage deliveries can be claimed only to permanent inventory or bank.");
        }

        foreach (var deliveryItem in delivery.Items.OrderBy(item => item.ItemOrder))
        {
            var item = await context.State.LoadOwnedItemAsync(
                deliveryItem.ItemInstanceId,
                command.CharacterId,
                expectedItems[deliveryItem.ItemInstanceId],
                cancellationToken);
            if (item.ContainerId != state.RecoveryStorageContainerId
                || item.ContainerSlotIndex != deliveryItem.ContainerSlotIndex)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.ItemStateConflict,
                    "A Recovery Storage item changed custody before it could be claimed.");
            }

            var definition = await context.State.LoadDefinitionAsync(item.DefinitionId, cancellationToken);
            var bagHasContents = await context.State.BagHasContentsAsync(item, cancellationToken);
            var destinationSlotIndex = await context.Placement.SelectDestinationSlotAsync(
                destination,
                definition,
                null,
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
                    revision = revision + 1,
                    updated_at = now()
                where id = @ItemInstanceId;

                delete from recovery_delivery_items
                where recovery_delivery_id = @DeliveryId
                  and item_instance_id = @ItemInstanceId;

                delete from item_container_slots
                where container_id = @RecoveryStorageContainerId
                  and slot_index = @RecoverySlotIndex;
                """,
                new
                {
                    DestinationContainerId = destination.ContainerId,
                    DestinationSlotIndex = destinationSlotIndex,
                    item.ItemInstanceId,
                    DeliveryId = delivery.DeliveryId,
                    RecoveryStorageContainerId = state.RecoveryStorageContainerId,
                    RecoverySlotIndex = deliveryItem.ContainerSlotIndex
                },
                context.Transaction,
                cancellationToken: cancellationToken));
            var afterState = await context.Journal.CaptureItemStateJsonAsync(
                item.ItemInstanceId,
                cancellationToken);
            context.Journal.AddItemAudit(
                "recovery_delivery_claimed",
                item.ItemInstanceId,
                beforeState,
                afterState);
            context.Journal.IncludeResultItem(item.ItemInstanceId);
        }

        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update recovery_deliveries
            set claimed_at = now(),
                revision = revision + 1
            where id = @DeliveryId;
            """,
            new { DeliveryId = delivery.DeliveryId },
            context.Transaction,
            cancellationToken: cancellationToken));
        context.Journal.TouchContainer(state.RecoveryStorageContainerId);
        context.Journal.TouchContainer(destination.ContainerId);
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.AddRecoveryDelivery(delivery.DeliveryId);
    }
}
