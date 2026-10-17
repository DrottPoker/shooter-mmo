using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Moves a whole or partial stack between slots of the same corpse.
internal sealed class CorpseInternalMoveCommandHandler
    : IItemCommandHandler<MoveCorpseItemCommand>,
    IItemCommandHandler<MoveCorpsePartialStackCommand>
{
    public Task ExecuteAsync(
        ItemTransactionContext context,
        MoveCorpseItemCommand command,
        CancellationToken cancellationToken)
    {
        return ExecuteMoveCorpseStackAsync(
            context,
            new CorpseItemTransferCommand(
                command.CharacterId,
                command.CorpseId,
                command.ItemInstanceId,
                command.ExpectedItemRevision,
                null,
                command.DestinationContainerId,
                command.ExpectedDestinationContainerRevision,
                command.DestinationSlotIndex,
                command.TargetItemInstanceId,
                command.ExpectedTargetItemRevision),
            cancellationToken);
    }

    public Task ExecuteAsync(
        ItemTransactionContext context,
        MoveCorpsePartialStackCommand command,
        CancellationToken cancellationToken)
    {
        return ExecuteMoveCorpseStackAsync(
            context,
            new CorpseItemTransferCommand(
                command.CharacterId,
                command.CorpseId,
                command.ItemInstanceId,
                command.ExpectedItemRevision,
                command.Quantity,
                command.DestinationContainerId,
                command.ExpectedDestinationContainerRevision,
                command.DestinationSlotIndex,
                command.TargetItemInstanceId,
                command.ExpectedTargetItemRevision),
            cancellationToken);
    }

    private static async Task ExecuteMoveCorpseStackAsync(
        ItemTransactionContext context,
        CorpseItemTransferCommand command,
        CancellationToken cancellationToken)
    {
        CorpseItemTransfers.ValidateCorpseItemTransferCommand(command);
        await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var corpse = await CorpseCustody.LockOpenCorpseAsync(context, command.CorpseId, cancellationToken);

        var itemIds = command.TargetItemInstanceId is null
            ? new[] { command.ItemInstanceId }
            : new[] { command.ItemInstanceId, command.TargetItemInstanceId.Value };
        await context.Locks.LockMutationScopeAsync(
            itemIds,
            [command.DestinationContainerId],
            cancellationToken);

        var source = await context.State.LoadItemAsync(command.ItemInstanceId, cancellationToken);
        if (source is null
            || source.Revision != command.ExpectedItemRevision
            || source.ContainerId is null
            || source.ContainerSlotIndex is null
            || !await CorpseCustody.IsCorpseSectionContainerAsync(
                context,
                command.CorpseId,
                source.ContainerId.Value,
                cancellationToken))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The corpse source item moved before the internal transfer committed.");
        }

        var sourceContainer = await context.State.LoadContainerAsync(
            source.ContainerId.Value,
            cancellationToken);
        var sourceBagHasContents = await context.State.BagHasContentsAsync(source, cancellationToken);
        if (source.IsBag && sourceBagHasContents)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "A non-empty corpse Bag cannot use an ordinary internal slot move.");
        }

        if (source.IsBag
            && await CorpseCustody.OwnsCorpseBagSectionAsync(
                context,
                command.CorpseId,
                source.ItemInstanceId,
                cancellationToken))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "The corpse Bag owns the corpse Bag section and cannot leave its equipment slot.");
        }

        var definition = await context.State.LoadDefinitionAsync(
            source.DefinitionId,
            cancellationToken);
        var sourcePolicies = await context.State.LoadPoliciesAsync(
            source.ItemInstanceId,
            cancellationToken);
        var sourceCapabilities = ItemPolicyRules.Evaluate(
            definition.RuntimeDefinition,
            ItemPolicyStates.ToPolicyStates(sourcePolicies));
        var isPartial = command.Quantity is not null;
        var movedQuantity = isPartial ? command.Quantity!.Value : source.Quantity;
        if (isPartial
            && (movedQuantity <= 0
                || movedQuantity >= source.Quantity
                || definition.RuntimeDefinition.MaximumStackSize <= 1
                || !sourceCapabilities.CanStack))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemQuantityChanged,
                "The requested internal partial-stack quantity is no longer valid.");
        }

        var destination = await context.State.LoadContainerAsync(
            command.DestinationContainerId,
            cancellationToken);
        if (!await CorpseCustody.IsCorpseSectionContainerAsync(
                context,
                command.CorpseId,
                destination.ContainerId,
                cancellationToken))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemSlotIncompatible,
                "The requested internal destination is not part of this corpse.");
        }

        if (destination.Revision != command.ExpectedDestinationContainerRevision)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The corpse destination changed before the internal transfer committed.");
        }

        LockedItem? target = null;
        IReadOnlyList<LockedItemPolicy>? targetPolicies = null;
        var swapItems = false;
        if (command.TargetItemInstanceId is not null)
        {
            target = await context.State.LoadItemAsync(
                command.TargetItemInstanceId.Value,
                cancellationToken);
            if (target is null
                || target.Revision != command.ExpectedTargetItemRevision
                || target.ContainerId != destination.ContainerId
                || target.ContainerSlotIndex != command.DestinationSlotIndex
                || !await CorpseCustody.IsCorpseSectionContainerAsync(
                    context,
                    command.CorpseId,
                    target.ContainerId.Value,
                    cancellationToken))
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.ItemStateConflict,
                    "The corpse target moved before the internal transfer committed.");
            }

            var targetBagHasContents = await context.State.BagHasContentsAsync(
                target,
                cancellationToken);
            if (target.IsBag && targetBagHasContents)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.BagStateChanged,
                    "A non-empty corpse Bag cannot use an ordinary internal slot swap.");
            }

            if (target.IsBag
                && await CorpseCustody.OwnsCorpseBagSectionAsync(
                    context,
                    command.CorpseId,
                    target.ItemInstanceId,
                    cancellationToken))
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.BagStateChanged,
                    "The corpse Bag owns the corpse Bag section and cannot leave its equipment slot.");
            }

            targetPolicies = await context.State.LoadPoliciesAsync(
                target.ItemInstanceId,
                cancellationToken);
            var stackDisposition = CorpseItemTransfers.GetCorpseStackDisposition(
                source,
                sourcePolicies,
                sourceCapabilities,
                target,
                targetPolicies,
                definition,
                movedQuantity);
            await context.Placement.SelectDestinationSlotAsync(
                destination,
                definition,
                stackDisposition == CorpseStackDisposition.Merge
                    ? target.Quantity + movedQuantity
                    : movedQuantity,
                command.DestinationSlotIndex,
                sourceBagHasContents,
                source.IsBag ? source.ItemInstanceId : null,
                false,
                cancellationToken,
                target.ItemInstanceId);
            if (stackDisposition != CorpseStackDisposition.Merge)
            {
                if (isPartial)
                {
                    CorpseItemTransfers.RejectCorpseStackMerge(stackDisposition);
                }

                var targetDefinition = await context.State.LoadDefinitionAsync(
                    target.DefinitionId,
                    cancellationToken);
                await context.Placement.SelectDestinationSlotAsync(
                    sourceContainer,
                    targetDefinition,
                    target.Quantity,
                    source.ContainerSlotIndex,
                    targetBagHasContents,
                    target.IsBag ? target.ItemInstanceId : null,
                    false,
                    cancellationToken,
                    source.ItemInstanceId);
                swapItems = true;
            }
        }
        else
        {
            await context.Placement.SelectDestinationSlotAsync(
                destination,
                definition,
                movedQuantity,
                command.DestinationSlotIndex,
                sourceBagHasContents,
                source.IsBag ? source.ItemInstanceId : null,
                false,
                cancellationToken);
        }

        if (target is not null && swapItems)
        {
            await CorpseCustodyWrites.SwapCorpseContainerItemsAsync(
                context,
                source,
                target,
                "corpse_item_internal_swap_source",
                "corpse_item_internal_swap_target",
                cancellationToken);
        }
        else if (target is not null)
        {
            await CorpseCustodyWrites.MergeCorpseQuantityAsync(
                context,
                source,
                target,
                movedQuantity,
                isPartial,
                "corpse_stack_internal_source",
                "corpse_stack_internal_merged",
                cancellationToken);
        }
        else if (isPartial)
        {
            await CorpseCustodyWrites.SplitCorpseQuantityAsync(
                context,
                source,
                sourcePolicies,
                movedQuantity,
                destination,
                command.DestinationSlotIndex,
                "corpse_stack_internal_source",
                "corpse_stack_internal_created",
                cancellationToken);
        }
        else
        {
            await CorpseCustodyWrites.MoveCorpseItemAsync(
                context,
                source,
                destination,
                command.DestinationSlotIndex,
                "corpse_item_repositioned",
                cancellationToken);
        }

        await CorpseCustody.AdvanceCorpseRevisionAsync(
            context,
            corpse,
            "corpse_internal_transfer_revision_advanced",
            cancellationToken);
        context.Journal.TouchContainer(sourceContainer.ContainerId);
        context.Journal.TouchContainer(destination.ContainerId);
    }
}
