using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Loots a whole or partial corpse stack into carried character storage, merging
// or swapping with a target stack.
internal sealed class CorpseLootCommandHandler
    : IItemCommandHandler<LootCorpseItemCommand>,
    IItemCommandHandler<LootCorpsePartialStackCommand>
{
    public Task ExecuteAsync(
        ItemTransactionContext context,
        LootCorpseItemCommand command,
        CancellationToken cancellationToken)
    {
        return ExecuteLootCorpseStackAsync(
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
        LootCorpsePartialStackCommand command,
        CancellationToken cancellationToken)
    {
        return ExecuteLootCorpseStackAsync(
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

    private static async Task ExecuteLootCorpseStackAsync(
        ItemTransactionContext context,
        CorpseItemTransferCommand command,
        CancellationToken cancellationToken)
    {
        CorpseItemTransfers.ValidateCorpseItemTransferCommand(command);
        await context.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var corpse = await CorpseCustody.LockOpenCorpseAsync(
            context,
            command.CorpseId,
            cancellationToken);

        var itemIds = command.TargetItemInstanceId is null
            ? new[] { command.ItemInstanceId }
            : new[] { command.ItemInstanceId, command.TargetItemInstanceId.Value };
        await context.LockMutationScopeAsync(
            itemIds,
            [command.DestinationContainerId],
            cancellationToken);

        var source = await context.State.LoadItemAsync(command.ItemInstanceId, cancellationToken);
        if (source is null
            || source.ContainerId is null
            || source.ContainerSlotIndex is null
            || !await CorpseCustody.IsCorpseSectionContainerAsync(
                context,
                command.CorpseId,
                source.ContainerId.Value,
                cancellationToken))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemAlreadyLooted,
                "The requested item is no longer on this corpse.");
        }

        if (source.Revision != command.ExpectedItemRevision)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The corpse item changed before the loot operation committed.");
        }

        if (source.IsBag)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "Corpse Bags move only through an atomic Bag aggregate swap.");
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
        if (!sourceCapabilities.CanChangeOwningCharacter)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "An active item policy prevents this corpse item from changing ownership.");
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

        var destination = await context.State.LoadOwnedContainerAsync(
            command.DestinationContainerId,
            command.CharacterId,
            cancellationToken);
        CorpseItemTransfers.EnsureCarriedLootDestination(destination);
        if (destination.Revision != command.ExpectedDestinationContainerRevision)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The loot destination changed before the operation committed.");
        }

        LockedItem? target = null;
        IReadOnlyList<LockedItemPolicy>? targetPolicies = null;
        var swapItems = false;
        if (command.TargetItemInstanceId is not null)
        {
            target = await context.State.LoadOwnedItemAsync(
                command.TargetItemInstanceId.Value,
                command.CharacterId,
                command.ExpectedTargetItemRevision!.Value,
                cancellationToken);
            if (target.ContainerId != destination.ContainerId
                || target.ContainerSlotIndex != command.DestinationSlotIndex)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.ItemStateConflict,
                    "The target stack moved before the loot operation committed.");
            }

            await context.Placement.SelectDestinationSlotAsync(
                destination,
                definition,
                command.DestinationSlotIndex,
                false,
                null,
                false,
                cancellationToken,
                target.ItemInstanceId);
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
            if (stackDisposition != CorpseStackDisposition.Merge)
            {
                if (isPartial)
                {
                    CorpseItemTransfers.RejectCorpseStackMerge(stackDisposition);
                }

                var targetDefinition = await context.State.LoadDefinitionAsync(
                    target.DefinitionId,
                    cancellationToken);
                var targetCapabilities = ItemPolicyRules.Evaluate(
                    targetDefinition.RuntimeDefinition,
                    ItemPolicyStates.ToPolicyStates(targetPolicies));
                if (!targetCapabilities.CanChangeOwningCharacter)
                {
                    ItemTransactionContext.Reject(
                        ItemTransactionErrorCodes.ItemPolicyRestricted,
                        "An active item policy prevents the carried target from entering corpse custody.");
                }

                var sourceContainer = await context.State.LoadContainerAsync(
                    source.ContainerId.Value,
                    cancellationToken);
                var targetBagHasContents = await context.State.BagHasContentsAsync(
                    target,
                    cancellationToken);
                await context.Placement.SelectDestinationSlotAsync(
                    sourceContainer,
                    targetDefinition,
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
                command.DestinationSlotIndex,
                false,
                null,
                false,
                cancellationToken);
        }

        if (target is not null && swapItems)
        {
            await CorpseCustodyWrites.SwapCorpseContainerItemsAsync(
                context,
                source,
                target,
                "corpse_item_swapped_to_character",
                "character_item_swapped_to_corpse",
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
                cancellationToken);
        }
        else
        {
            await CorpseCustodyWrites.MoveCorpseItemAsync(
                context,
                source,
                destination,
                command.DestinationSlotIndex,
                cancellationToken);
        }

        await CorpseCustody.AdvanceCorpseRevisionAsync(context, corpse, cancellationToken);
        context.Journal.TouchContainer(source.ContainerId);
        context.Journal.TouchContainer(destination.ContainerId);
        context.Journal.TouchCharacter(command.CharacterId);
    }
}
