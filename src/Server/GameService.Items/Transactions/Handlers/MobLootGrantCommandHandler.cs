using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.Server.Common.Identity;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Grants live Mob loot to the looting character: under a derived item id into an empty container
// or equipment slot, or as a top-up of a compatible stack that the worker names in that slot. It
// never swaps, because the worker holds the corpse in memory and cannot take an item back.
internal sealed class MobLootGrantCommandHandler
    : IItemCommandHandler<GrantMobLootCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        GrantMobLootCommand command,
        CancellationToken cancellationToken)
    {
        var toEquipment = command.EquipmentSlotId is not null;
        if (context.Actor.Authority != ItemTransactionAuthority.SimulationWorker
            || command.GrantId == Guid.Empty
            || command.GrantId != context.OperationId
            || command.SourceCorpseId == Guid.Empty
            || !Identifiers.IsContentIdentifier(command.SourceActorDefinitionId)
            || command.CharacterId == Guid.Empty
            || command.ExpectedCharacterRevision < 0
            || (command.TargetItemInstanceId is null) != (command.ExpectedTargetItemRevision is null)
            || command.TargetItemInstanceId == Guid.Empty
            || command.ExpectedTargetItemRevision < 0
            || (toEquipment
                ? string.IsNullOrWhiteSpace(command.EquipmentSlotId)
                    || !MobLootRules.NamesEquipmentDestinationOnly(
                        command.DestinationContainerId,
                        command.ExpectedDestinationContainerRevision,
                        command.DestinationSlotIndex)
                : command.DestinationContainerId == Guid.Empty
                    || command.ExpectedDestinationContainerRevision < 0
                    || command.DestinationSlotIndex < 0))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.MobLootGrantInvalid,
                "The live Mob loot grant is incomplete or invalid.");
        }

        await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(
                command.CharacterId,
                command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            command.TargetItemInstanceId is Guid targetId ? [targetId] : [],
            toEquipment ? [] : [command.DestinationContainerId],
            cancellationToken);
        var definition = await context.State.LoadDefinitionAsync(
            command.DefinitionId,
            cancellationToken);
        MobLootRules.ValidateMobLootDefinition(definition, command.Quantity);
        await MobLootRules.RecordGrantAsync(
            context,
            command.GrantId,
            command.CharacterId,
            command.SourceCorpseId,
            cancellationToken);
        var grant = new MobLootGrantSource(
            command.GrantId,
            command.SourceCorpseId,
            command.SourceActorDefinitionId,
            command.CharacterId);
        var destination = toEquipment
            ? null
            : await LoadDestinationAsync(context, command, cancellationToken);

        if (command.TargetItemInstanceId is not null)
        {
            await TopUpAsync(context, command, grant, definition, destination, cancellationToken);
            return;
        }

        if (destination is null)
        {
            await context.Placement.EnsureEquipmentDestinationAsync(
                command.CharacterId,
                command.EquipmentSlotId!,
                definition,
                command.Quantity,
                false,
                cancellationToken);
            await MobLootWrites.CreateEquippedAsync(
                context,
                grant,
                definition,
                command.Quantity,
                command.EquipmentSlotId!,
                cancellationToken);
            return;
        }

        var slotIndex = await context.Placement.SelectDestinationSlotAsync(
            destination,
            definition,
            command.Quantity,
            command.DestinationSlotIndex,
            false,
            null,
            false,
            cancellationToken);
        await MobLootWrites.CreateInContainerAsync(
            context,
            grant,
            definition,
            command.Quantity,
            destination.ContainerId,
            slotIndex,
            cancellationToken);
    }

    private static async Task<LockedContainer> LoadDestinationAsync(
        ItemTransactionContext context,
        GrantMobLootCommand command,
        CancellationToken cancellationToken)
    {
        var destination = await context.State.LoadOwnedContainerAsync(
            command.DestinationContainerId,
            command.CharacterId,
            cancellationToken);
        if (destination.Revision != command.ExpectedDestinationContainerRevision)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The Mob loot destination container revision is stale.");
        }

        return destination;
    }

    // Adds the loot to the named stack, which must still be in the named slot, have the loot's
    // definition, merge under the stack rules, and stay within its slot's limit.
    private static async Task TopUpAsync(
        ItemTransactionContext context,
        GrantMobLootCommand command,
        MobLootGrantSource grant,
        ResolvedItemDefinition definition,
        LockedContainer? destination,
        CancellationToken cancellationToken)
    {
        var target = await context.State.LoadOwnedItemAsync(
            command.TargetItemInstanceId!.Value,
            command.CharacterId,
            command.ExpectedTargetItemRevision!.Value,
            cancellationToken);
        var inNamedSlot = destination is null
            ? target.EquippedCharacterId == command.CharacterId
                && string.Equals(target.EquipmentSlotId, command.EquipmentSlotId, StringComparison.Ordinal)
            : target.ContainerId == destination.ContainerId
                && target.ContainerSlotIndex == command.DestinationSlotIndex;
        if (!inNamedSlot)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The stack moved before the Mob loot top-up committed.");
        }

        if (!string.Equals(target.DefinitionId, definition.RuntimeDefinition.Id, StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                destination is null
                    ? ItemTransactionErrorCodes.EquipmentSlotOccupied
                    : ItemTransactionErrorCodes.ItemSlotOccupied,
                "Live Mob loot cannot swap with another item. Drop it on an empty slot or a stack of the same item.");
        }

        var targetPolicies = await context.State.LoadPoliciesAsync(target.ItemInstanceId, cancellationToken);
        var activeTargetPolicies = targetPolicies.Where(policy => string.Equals(
            policy.Status,
            ItemPolicyRules.ActiveStatus,
            StringComparison.Ordinal));
        var lootState = new ItemStackState(
            definition.RuntimeDefinition.Id,
            command.Quantity,
            ItemPolicyStates.CalculatePolicyFingerprint([]),
            ItemPolicyStates.EmptyStackStateFingerprint);
        var targetState = new ItemStackState(
            target.DefinitionId,
            target.Quantity,
            ItemPolicyStates.CalculatePolicyFingerprint(activeTargetPolicies),
            ItemPolicyStates.EmptyStackStateFingerprint);
        var targetCanStack = ItemPolicyRules.Evaluate(
            definition.RuntimeDefinition,
            ItemPolicyStates.ToPolicyStates(targetPolicies)).CanStack;
        if (!targetCanStack || !ItemStackRules.CanMerge(definition.RuntimeDefinition, lootState, targetState))
        {
            var limitExceeded = targetCanStack && ItemStackRules.AreCompatible(lootState, targetState);
            ItemTransactionContext.Reject(
                limitExceeded
                    ? ItemTransactionErrorCodes.ItemStackLimitExceeded
                    : ItemTransactionErrorCodes.ItemStackIncompatible,
                limitExceeded
                    ? "The Mob loot would exceed the target stack limit."
                    : "The Mob loot and the target stack have incompatible state.");
        }

        if (destination is null)
        {
            await context.Placement.EnsureEquippedStackQuantityAsync(
                command.CharacterId,
                command.EquipmentSlotId!,
                definition,
                target.Quantity + command.Quantity,
                cancellationToken);
        }
        else
        {
            await context.Placement.SelectDestinationSlotAsync(
                destination,
                definition,
                target.Quantity + command.Quantity,
                command.DestinationSlotIndex,
                false,
                null,
                false,
                cancellationToken,
                target.ItemInstanceId);
        }

        await MobLootWrites.TopUpAsync(context, grant, target.ItemInstanceId, command.Quantity, cancellationToken);
        if (destination is not null)
        {
            context.Journal.TouchContainer(destination.ContainerId);
        }
    }
}
