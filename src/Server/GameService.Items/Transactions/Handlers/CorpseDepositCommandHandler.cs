using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Deposits a whole or partial carried stack into a corpse section, merging or
// swapping with a corpse target stack.
internal sealed class CorpseDepositCommandHandler
    : IItemCommandHandler<DepositCorpseItemCommand>,
    IItemCommandHandler<DepositCorpsePartialStackCommand>
{
    public Task ExecuteAsync(
        ItemTransactionContext context,
        DepositCorpseItemCommand command,
        CancellationToken cancellationToken)
    {
        return ExecuteDepositCorpseStackAsync(
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
        DepositCorpsePartialStackCommand command,
        CancellationToken cancellationToken)
    {
        return ExecuteDepositCorpseStackAsync(
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

    private static async Task ExecuteDepositCorpseStackAsync(
        ItemTransactionContext context,
        CorpseItemTransferCommand command,
        CancellationToken cancellationToken)
    {
        CorpseItemTransfers.ValidateCorpseItemTransferCommand(command);
        await context.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var corpse = await CorpseCustody.LockOpenCorpseAsync(context, command.CorpseId, cancellationToken);

        var itemIds = command.TargetItemInstanceId is null
            ? new[] { command.ItemInstanceId }
            : new[] { command.ItemInstanceId, command.TargetItemInstanceId.Value };
        await context.LockMutationScopeAsync(
            itemIds,
            [command.DestinationContainerId],
            cancellationToken);

        var source = await context.LoadOwnedItemAsync(
            command.ItemInstanceId,
            command.CharacterId,
            command.ExpectedItemRevision,
            cancellationToken);
        if (source.ContainerId is null || source.ContainerSlotIndex is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemSlotIncompatible,
                "Only items in carried container slots can enter corpse custody.");
        }

        var sourceContainer = await context.LoadOwnedContainerAsync(
            source.ContainerId.Value,
            command.CharacterId,
            cancellationToken);
        CorpseItemTransfers.EnsureCarriedCorpseTransferSource(sourceContainer);

        var definition = await context.LoadDefinitionAsync(
            source.DefinitionId,
            cancellationToken);
        var sourcePolicies = await context.LoadPoliciesAsync(
            source.ItemInstanceId,
            cancellationToken);
        var sourceCapabilities = ItemPolicyRules.Evaluate(
            definition.RuntimeDefinition,
            ItemPolicyStates.ToPolicyStates(sourcePolicies));
        if (!sourceCapabilities.CanChangeOwningCharacter)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "An active item policy prevents this carried item from entering corpse custody.");
        }

        var sourceBagHasContents = await context.BagHasContentsAsync(source, cancellationToken);
        if (source.IsBag && sourceBagHasContents)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagNotEmpty,
                "A non-empty Bag moves into corpse custody only through an atomic Bag aggregate swap.");
        }

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
                "The requested partial-stack quantity is no longer valid.");
        }

        var destination = await context.LoadContainerAsync(
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
                "The requested destination is not part of this corpse.");
        }

        if (destination.Revision != command.ExpectedDestinationContainerRevision)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The corpse destination changed before the operation committed.");
        }

        LockedItem? target = null;
        IReadOnlyList<LockedItemPolicy>? targetPolicies = null;
        var swapItems = false;
        if (command.TargetItemInstanceId is not null)
        {
            target = await context.LoadItemAsync(
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
                    "The corpse target moved before the deposit committed.");
            }

            if (target.IsBag)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.BagStateChanged,
                    "Corpse Bags move only through an atomic Bag aggregate swap.");
            }

            await context.SelectDestinationSlotAsync(
                destination,
                definition,
                command.DestinationSlotIndex,
                sourceBagHasContents,
                source.IsBag ? source.ItemInstanceId : null,
                false,
                cancellationToken,
                target.ItemInstanceId);
            targetPolicies = await context.LoadPoliciesAsync(
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
            if (stackDisposition != CorpseStackDisposition.Merge)
            {
                if (isPartial)
                {
                    CorpseItemTransfers.RejectCorpseStackMerge(stackDisposition);
                }

                var targetDefinition = await context.LoadDefinitionAsync(
                    target.DefinitionId,
                    cancellationToken);
                var targetCapabilities = ItemPolicyRules.Evaluate(
                    targetDefinition.RuntimeDefinition,
                    ItemPolicyStates.ToPolicyStates(targetPolicies));
                if (!targetCapabilities.CanChangeOwningCharacter)
                {
                    ItemTransactionContext.Reject(
                        ItemTransactionErrorCodes.ItemPolicyRestricted,
                        "An active item policy prevents the corpse target from changing ownership.");
                }

                await context.SelectDestinationSlotAsync(
                    sourceContainer,
                    targetDefinition,
                    source.ContainerSlotIndex,
                    false,
                    null,
                    false,
                    cancellationToken,
                    source.ItemInstanceId);
                swapItems = true;
            }
        }
        else
        {
            await context.SelectDestinationSlotAsync(
                destination,
                definition,
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
                "character_item_swapped_to_corpse",
                "corpse_item_swapped_to_character",
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
                "corpse_stack_deposit_source",
                "corpse_stack_deposit_merged",
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
                "corpse_stack_deposit_source",
                "corpse_stack_deposit_created",
                cancellationToken);
        }
        else
        {
            await CorpseCustodyWrites.MoveCorpseItemAsync(
                context,
                source,
                destination,
                command.DestinationSlotIndex,
                "corpse_item_deposited",
                cancellationToken);
        }

        await CorpseCustody.AdvanceCorpseRevisionAsync(
            context,
            corpse,
            "corpse_deposit_revision_advanced",
            cancellationToken);
        context.TouchContainer(sourceContainer.ContainerId);
        context.TouchContainer(destination.ContainerId);
        context.TouchCharacter(command.CharacterId);
    }
}
