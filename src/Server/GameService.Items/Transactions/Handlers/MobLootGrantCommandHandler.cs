using Dapper;
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
        var destination = toEquipment
            ? null
            : await LoadDestinationAsync(context, command, cancellationToken);

        if (command.TargetItemInstanceId is not null)
        {
            await TopUpAsync(context, command, definition, destination, cancellationToken);
            return;
        }

        if (destination is null)
        {
            await GrantIntoEquipmentAsync(context, command, definition, cancellationToken);
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
        var itemInstanceId = ScopedGuid.Create("mob-loot-item", command.GrantId);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (
                @ItemInstanceId,
                @DefinitionId,
                @Quantity,
                @ContainerId,
                @SlotIndex);
            """,
            new
            {
                ItemInstanceId = itemInstanceId,
                DefinitionId = definition.RuntimeDefinition.Id,
                command.Quantity,
                ContainerId = destination.ContainerId,
                SlotIndex = slotIndex
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        await JournalGrantAsync(context, command, itemInstanceId, "live_mob_loot_granted", null, cancellationToken);
        context.Journal.TouchContainer(destination.ContainerId);
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

    private static async Task GrantIntoEquipmentAsync(
        ItemTransactionContext context,
        GrantMobLootCommand command,
        ResolvedItemDefinition definition,
        CancellationToken cancellationToken)
    {
        var equipmentSlotId = command.EquipmentSlotId!;
        await context.Placement.EnsureEquipmentDestinationAsync(
            command.CharacterId,
            equipmentSlotId,
            definition,
            command.Quantity,
            false,
            cancellationToken);
        var itemInstanceId = ScopedGuid.Create("mob-loot-item", command.GrantId);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into item_instances (
                id,
                definition_id,
                quantity,
                equipped_character_id,
                equipment_slot_id)
            values (
                @ItemInstanceId,
                @DefinitionId,
                @Quantity,
                @CharacterId,
                @EquipmentSlotId);
            """,
            new
            {
                ItemInstanceId = itemInstanceId,
                DefinitionId = definition.RuntimeDefinition.Id,
                command.Quantity,
                command.CharacterId,
                EquipmentSlotId = equipmentSlotId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        await JournalGrantAsync(context, command, itemInstanceId, "live_mob_loot_granted", null, cancellationToken);
    }

    // Adds the loot to the named stack, which must still be in the named slot, have the loot's
    // definition, merge under the stack rules, and stay within its slot's limit.
    private static async Task TopUpAsync(
        ItemTransactionContext context,
        GrantMobLootCommand command,
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

        var beforeState = await context.Journal.CaptureItemStateJsonAsync(
            target.ItemInstanceId,
            cancellationToken);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update item_instances
            set quantity = quantity + @Quantity,
                revision = revision + 1,
                updated_at = now()
            where id = @ItemInstanceId;
            """,
            new { command.Quantity, target.ItemInstanceId },
            context.Transaction,
            cancellationToken: cancellationToken));
        await JournalGrantAsync(
            context,
            command,
            target.ItemInstanceId,
            "live_mob_loot_merged",
            beforeState,
            cancellationToken);
        if (destination is not null)
        {
            context.Journal.TouchContainer(destination.ContainerId);
        }
    }

    private static async Task JournalGrantAsync(
        ItemTransactionContext context,
        GrantMobLootCommand command,
        Guid itemInstanceId,
        string auditKind,
        string? beforeState,
        CancellationToken cancellationToken)
    {
        var afterState = await context.Journal.CaptureItemStateJsonAsync(
            itemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit(
            auditKind,
            itemInstanceId,
            beforeState,
            afterState);
        context.Journal.AddMetadataAudit(
            "live_mob_loot_grant_committed",
            null,
            new
            {
                command.GrantId,
                command.SourceCorpseId,
                command.SourceActorDefinitionId,
                command.CharacterId,
                ItemInstanceId = itemInstanceId
            });
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.IncludeResultItem(itemInstanceId);
    }
}
