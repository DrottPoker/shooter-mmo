using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Splits, merges, and consumes item stacks.
internal sealed class ItemStackCommandHandler
    : IItemCommandHandler<SplitItemStackCommand>,
    IItemCommandHandler<MergeItemStacksCommand>,
    IItemCommandHandler<ConsumeItemQuantityCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        SplitItemStackCommand command,
        CancellationToken cancellationToken)
    {
        await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            [command.ItemInstanceId],
            [command.DestinationContainerId],
            cancellationToken);
        var source = await context.State.LoadOwnedItemAsync(
            command.ItemInstanceId,
            command.CharacterId,
            command.ExpectedItemRevision,
            cancellationToken);
        if (source.ContainerId is null
            || string.Equals(source.SourceContainerType, "recovery_storage", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStackIncompatible,
                "The source stack is not in a splittable container location.");
        }

        if (command.Quantity <= 0 || command.Quantity >= source.Quantity)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemQuantityChanged,
                "A split quantity must be positive and smaller than the source quantity.");
        }

        var definition = await context.State.LoadDefinitionAsync(source.DefinitionId, cancellationToken);
        if (definition.IsBag || definition.RuntimeDefinition.MaximumStackSize <= 1)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStackIncompatible,
                "The item definition is not stackable.");
        }

        var destination = await context.State.LoadOwnedContainerAsync(
            command.DestinationContainerId,
            command.CharacterId,
            cancellationToken);
        var destinationSlotIndex = await context.Placement.SelectDestinationSlotAsync(
            destination,
            definition,
            command.DestinationSlotIndex,
            false,
            null,
            false,
            cancellationToken);
        var policies = await context.State.LoadPoliciesAsync(source.ItemInstanceId, cancellationToken);
        var beforeSource = await context.Journal.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var splitItemInstanceId = Guid.NewGuid();

        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update item_instances
            set quantity = quantity - @Quantity,
                revision = revision + 1,
                updated_at = now()
            where id = @SourceItemInstanceId;

            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (
                @SplitItemInstanceId,
                @DefinitionId,
                @Quantity,
                @DestinationContainerId,
                @DestinationSlotIndex);
            """,
            new
            {
                command.Quantity,
                SourceItemInstanceId = source.ItemInstanceId,
                SplitItemInstanceId = splitItemInstanceId,
                source.DefinitionId,
                DestinationContainerId = destination.ContainerId,
                DestinationSlotIndex = destinationSlotIndex
            },
            context.Transaction,
            cancellationToken: cancellationToken));

        foreach (var policy in policies.Where(policy =>
                     string.Equals(policy.Status, ItemPolicyRules.ActiveStatus, StringComparison.Ordinal)))
        {
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                insert into item_instance_policies (
                    id,
                    item_instance_id,
                    policy_kind,
                    source_kind,
                    source_id)
                values (
                    @PolicyId,
                    @ItemInstanceId,
                    @PolicyKind,
                    @SourceKind,
                    @SourceId);
                """,
                new
                {
                    PolicyId = Guid.NewGuid(),
                    ItemInstanceId = splitItemInstanceId,
                    policy.PolicyKind,
                    policy.SourceKind,
                    policy.SourceId
                },
                context.Transaction,
                cancellationToken: cancellationToken));
        }

        var afterSource = await context.Journal.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var afterSplit = await context.Journal.CaptureItemStateJsonAsync(
            splitItemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit(
            "stack_split_source",
            source.ItemInstanceId,
            beforeSource,
            afterSource);
        context.Journal.AddItemAudit(
            "stack_split_created",
            splitItemInstanceId,
            null,
            afterSplit);
        context.Journal.TouchContainer(source.ContainerId);
        context.Journal.TouchContainer(destination.ContainerId);
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.IncludeResultItem(source.ItemInstanceId);
        context.Journal.IncludeResultItem(splitItemInstanceId);
    }

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        MergeItemStacksCommand command,
        CancellationToken cancellationToken)
    {
        if (command.SourceItemInstanceId == command.TargetItemInstanceId)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStackIncompatible,
                "A stack cannot merge into itself.");
        }

        await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            [command.SourceItemInstanceId, command.TargetItemInstanceId],
            [],
            cancellationToken);
        var source = await context.State.LoadOwnedItemAsync(
            command.SourceItemInstanceId,
            command.CharacterId,
            command.ExpectedSourceItemRevision,
            cancellationToken);
        var target = await context.State.LoadOwnedItemAsync(
            command.TargetItemInstanceId,
            command.CharacterId,
            command.ExpectedTargetItemRevision,
            cancellationToken);
        if (source.ContainerId is null
            || target.ContainerId is null
            || string.Equals(source.SourceContainerType, "recovery_storage", StringComparison.Ordinal)
            || string.Equals(target.SourceContainerType, "recovery_storage", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStackIncompatible,
                "Both stacks must be in compatible ordinary container slots.");
        }

        var definition = await context.State.LoadDefinitionAsync(source.DefinitionId, cancellationToken);
        var sourcePolicies = await context.State.LoadPoliciesAsync(source.ItemInstanceId, cancellationToken);
        var targetPolicies = await context.State.LoadPoliciesAsync(target.ItemInstanceId, cancellationToken);
        var sourceCapabilities = ItemPolicyRules.Evaluate(
            definition.RuntimeDefinition,
            ItemPolicyStates.ToPolicyStates(sourcePolicies));
        var targetCapabilities = ItemPolicyRules.Evaluate(
            definition.RuntimeDefinition,
            ItemPolicyStates.ToPolicyStates(targetPolicies));
        if (!sourceCapabilities.CanStack || !targetCapabilities.CanStack)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "The effective item policy does not permit stacking.");
        }

        var sourceState = new ItemStackState(
            source.DefinitionId,
            source.Quantity,
            ItemPolicyStates.CalculatePolicyFingerprint(
                sourcePolicies.Where(policy => string.Equals(
                    policy.Status,
                    ItemPolicyRules.ActiveStatus,
                    StringComparison.Ordinal))),
            ItemPolicyStates.EmptyStackStateFingerprint);
        var targetState = new ItemStackState(
            target.DefinitionId,
            target.Quantity,
            ItemPolicyStates.CalculatePolicyFingerprint(
                targetPolicies.Where(policy => string.Equals(
                    policy.Status,
                    ItemPolicyRules.ActiveStatus,
                    StringComparison.Ordinal))),
            ItemPolicyStates.EmptyStackStateFingerprint);
        if (!ItemStackRules.CanMerge(definition.RuntimeDefinition, sourceState, targetState))
        {
            var sameDefinitionAndPolicy = ItemStackRules.AreCompatible(sourceState, targetState);
            ItemTransactionContext.Reject(
                sameDefinitionAndPolicy
                    ? ItemTransactionErrorCodes.ItemStackLimitExceeded
                    : ItemTransactionErrorCodes.ItemStackIncompatible,
                sameDefinitionAndPolicy
                    ? "The merged quantity would exceed the definition stack limit."
                    : "The item stacks have incompatible definition or policy state.");
        }

        var beforeSource = await context.Journal.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var beforeTarget = await context.Journal.CaptureItemStateJsonAsync(
            target.ItemInstanceId,
            cancellationToken);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update item_instances
            set quantity = quantity + @SourceQuantity,
                revision = revision + 1,
                updated_at = now()
            where id = @TargetItemInstanceId;

            delete from item_instances
            where id = @SourceItemInstanceId;
            """,
            new
            {
                SourceQuantity = source.Quantity,
                TargetItemInstanceId = target.ItemInstanceId,
                SourceItemInstanceId = source.ItemInstanceId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        var afterTarget = await context.Journal.CaptureItemStateJsonAsync(
            target.ItemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit(
            "stack_merge_target",
            target.ItemInstanceId,
            beforeTarget,
            afterTarget);
        context.Journal.AddItemAudit(
            "stack_merge_source_removed",
            source.ItemInstanceId,
            beforeSource,
            null);
        context.Journal.TouchContainer(source.ContainerId);
        context.Journal.TouchContainer(target.ContainerId);
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.IncludeResultItem(target.ItemInstanceId);
    }

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        ConsumeItemQuantityCommand command,
        CancellationToken cancellationToken)
    {
        await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            [command.ItemInstanceId],
            [],
            cancellationToken);
        var item = await context.State.LoadOwnedItemAsync(
            command.ItemInstanceId,
            command.CharacterId,
            command.ExpectedItemRevision,
            cancellationToken);
        var definition = await context.State.LoadDefinitionAsync(item.DefinitionId, cancellationToken);
        if (definition.IsBag)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStackIncompatible,
                "A Bag item cannot be consumed.");
        }

        if (string.Equals(item.SourceContainerType, "recovery_storage", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.RecoveryAccessRequired,
                "Recovery Storage items must be claimed before use.");
        }

        if (command.Quantity <= 0 || command.Quantity > item.Quantity)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemQuantityChanged,
                "The consume quantity exceeds the current item quantity.");
        }

        var beforeState = await context.Journal.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        if (command.Quantity == item.Quantity)
        {
            await context.Connection.ExecuteAsync(new CommandDefinition(
                "delete from item_instances where id = @ItemInstanceId;",
                new { item.ItemInstanceId },
                context.Transaction,
                cancellationToken: cancellationToken));
            context.Journal.AddItemAudit(
                "item_consumed",
                item.ItemInstanceId,
                beforeState,
                null);
        }
        else
        {
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                update item_instances
                set quantity = quantity - @Quantity,
                    revision = revision + 1,
                    updated_at = now()
                where id = @ItemInstanceId;
                """,
                new { command.Quantity, item.ItemInstanceId },
                context.Transaction,
                cancellationToken: cancellationToken));
            var afterState = await context.Journal.CaptureItemStateJsonAsync(
                item.ItemInstanceId,
                cancellationToken);
            context.Journal.AddItemAudit(
                "item_quantity_consumed",
                item.ItemInstanceId,
                beforeState,
                afterState);
            context.Journal.IncludeResultItem(item.ItemInstanceId);
        }

        context.Journal.TouchContainer(item.ContainerId);
        context.Journal.TouchCharacter(command.CharacterId);
    }
}
