using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Loots a whole or partial corpse stack into carried character storage, merging
// or swapping with a target stack, or a whole stack straight into an equipment slot.
internal sealed class CorpseLootCommandHandler
    : IItemCommandHandler<LootCorpseItemCommand>,
    IItemCommandHandler<LootCorpsePartialStackCommand>,
    IItemCommandHandler<LootCorpseItemToEquipmentCommand>
{
    // An equipped stack of the same definition takes the loot as a top-up when the stacks can
    // merge. Otherwise the loot equips under the normal equip rules, and an equipped item in the
    // slot swaps into the corpse slot the loot came from, as a deposit into that slot would.
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        LootCorpseItemToEquipmentCommand command,
        CancellationToken cancellationToken)
    {
        ValidateEquipmentLootCommand(command);
        await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var corpse = await CorpseCustody.LockOpenCorpseAsync(
            context,
            command.CorpseId,
            cancellationToken);
        var occupantId = await context.State.LoadEquippedItemIdAsync(
            command.CharacterId,
            command.EquipmentSlotId,
            cancellationToken);
        if (occupantId != command.TargetItemInstanceId)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The equipment slot changed before the loot operation committed.");
        }

        await context.Locks.LockMutationScopeAsync(
            occupantId is null ? [command.ItemInstanceId] : [command.ItemInstanceId, occupantId.Value],
            [],
            cancellationToken);
        var source = await LoadCorpseSourceAsync(
            context,
            command.CorpseId,
            command.ItemInstanceId,
            command.ExpectedItemRevision,
            cancellationToken);
        var definition = await context.State.LoadDefinitionAsync(source.DefinitionId, cancellationToken);
        var sourcePolicies = await context.State.LoadPoliciesAsync(source.ItemInstanceId, cancellationToken);
        var sourceCapabilities = ItemPolicyRules.Evaluate(
            definition.RuntimeDefinition,
            ItemPolicyStates.ToPolicyStates(sourcePolicies));
        if (!sourceCapabilities.CanChangeOwningCharacter)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "An active item policy prevents this corpse item from changing ownership.");
        }

        var occupant = occupantId is null
            ? null
            : await context.State.LoadOwnedItemAsync(
                occupantId.Value,
                command.CharacterId,
                command.ExpectedTargetItemRevision!.Value,
                cancellationToken);
        if (occupant is not null
            && string.Equals(occupant.DefinitionId, source.DefinitionId, StringComparison.Ordinal))
        {
            var occupantPolicies = await context.State.LoadPoliciesAsync(
                occupant.ItemInstanceId,
                cancellationToken);
            if (CorpseItemTransfers.GetCorpseStackDisposition(
                    source,
                    sourcePolicies,
                    sourceCapabilities,
                    occupant,
                    occupantPolicies,
                    definition,
                    source.Quantity) == CorpseStackDisposition.Merge)
            {
                await context.Placement.EnsureEquippedStackQuantityAsync(
                    command.CharacterId,
                    command.EquipmentSlotId,
                    definition,
                    occupant.Quantity + source.Quantity,
                    cancellationToken);
                await CorpseCustodyWrites.MergeCorpseQuantityAsync(
                    context,
                    source,
                    occupant,
                    source.Quantity,
                    false,
                    cancellationToken);
                await FinishEquipmentLootAsync(context, corpse, source, command.CharacterId, cancellationToken);
                return;
            }
        }

        await context.Placement.EnsureEquipmentDestinationAsync(
            command.CharacterId,
            command.EquipmentSlotId,
            definition,
            source.Quantity,
            false,
            cancellationToken,
            occupantId);
        if (occupant is null)
        {
            await CorpseCustodyWrites.EquipCorpseItemAsync(
                context,
                source,
                command.CharacterId,
                command.EquipmentSlotId,
                cancellationToken);
        }
        else
        {
            await EnsureOccupantCanEnterCorpseSlotAsync(context, source, occupant, cancellationToken);
            await CorpseCustodyWrites.SwapCorpseItemWithEquipmentAsync(
                context,
                source,
                occupant,
                command.CharacterId,
                command.EquipmentSlotId,
                cancellationToken);
        }

        await FinishEquipmentLootAsync(context, corpse, source, command.CharacterId, cancellationToken);
    }

    // The swapped-out item enters corpse custody in the loot's source slot, under the rules a
    // deposit into that slot follows.
    private static async Task EnsureOccupantCanEnterCorpseSlotAsync(
        ItemTransactionContext context,
        LockedItem source,
        LockedItem occupant,
        CancellationToken cancellationToken)
    {
        if (occupant.IsBag)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "An equipped Bag swaps with a corpse Bag only through the atomic Bag swap.");
        }

        var occupantDefinition = await context.State.LoadDefinitionAsync(
            occupant.DefinitionId,
            cancellationToken);
        var occupantCapabilities = ItemPolicyRules.Evaluate(
            occupantDefinition.RuntimeDefinition,
            ItemPolicyStates.ToPolicyStates(
                await context.State.LoadPoliciesAsync(occupant.ItemInstanceId, cancellationToken)));
        if (!occupantCapabilities.CanChangeOwningCharacter)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "An active item policy prevents the equipped item from entering corpse custody.");
        }

        var sourceContainer = await context.State.LoadContainerAsync(
            source.ContainerId!.Value,
            cancellationToken);
        await context.Placement.SelectDestinationSlotAsync(
            sourceContainer,
            occupantDefinition,
            occupant.Quantity,
            source.ContainerSlotIndex,
            false,
            null,
            false,
            cancellationToken,
            source.ItemInstanceId);
    }

    private static async Task FinishEquipmentLootAsync(
        ItemTransactionContext context,
        LockedCorpseRow corpse,
        LockedItem source,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        await CorpseCustody.AdvanceCorpseRevisionAsync(context, corpse, cancellationToken);
        context.Journal.TouchContainer(source.ContainerId);
        context.Journal.TouchCharacter(characterId);
    }

    private static async Task<LockedItem> LoadCorpseSourceAsync(
        ItemTransactionContext context,
        Guid corpseId,
        Guid itemInstanceId,
        long expectedItemRevision,
        CancellationToken cancellationToken)
    {
        var source = await context.State.LoadItemAsync(itemInstanceId, cancellationToken);
        if (source is null
            || source.ContainerId is null
            || source.ContainerSlotIndex is null
            || !await CorpseCustody.IsCorpseSectionContainerAsync(
                context,
                corpseId,
                source.ContainerId.Value,
                cancellationToken))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemAlreadyLooted,
                "The requested item is no longer on this corpse.");
        }

        if (source.Revision != expectedItemRevision)
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

        return source;
    }

    private static void ValidateEquipmentLootCommand(LootCorpseItemToEquipmentCommand command)
    {
        if (command.CharacterId == Guid.Empty
            || command.CorpseId == Guid.Empty
            || command.ItemInstanceId == Guid.Empty
            || command.ExpectedItemRevision < 0
            || string.IsNullOrWhiteSpace(command.EquipmentSlotId)
            || (command.TargetItemInstanceId is null) != (command.ExpectedTargetItemRevision is null)
            || command.TargetItemInstanceId == Guid.Empty
            || command.TargetItemInstanceId == command.ItemInstanceId
            || command.ExpectedTargetItemRevision < 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.SimulationOperationInvalid,
                "The corpse equipment loot command is incomplete or invalid.");
        }
    }

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
        await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var corpse = await CorpseCustody.LockOpenCorpseAsync(
            context,
            command.CorpseId,
            cancellationToken);

        var itemIds = command.TargetItemInstanceId is null
            ? new[] { command.ItemInstanceId }
            : new[] { command.ItemInstanceId, command.TargetItemInstanceId.Value };
        await context.Locks.LockMutationScopeAsync(
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
                false,
                null,
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
