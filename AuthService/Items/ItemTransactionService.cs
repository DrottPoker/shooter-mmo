using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using ShooterMmo.WorldData.Items;

namespace AuthService.Items;

public sealed partial class ItemTransactionService(NpgsqlDataSource dataSource)
{
    private const string MutationSavepoint = "item_mutation";
    private const string EmptyStackStateFingerprint = "item-stack-state-v1";
    private static readonly JsonSerializerOptions OperationJsonOptions = new(JsonSerializerDefaults.Web);

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<GrantItemCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.Grant,
            request.Command.CharacterId,
            ExecuteGrantAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<RelocateItemCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.Relocate,
            request.Command.CharacterId,
            ExecuteRelocateAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<EquipItemCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.Equip,
            request.Command.CharacterId,
            ExecuteEquipAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<UnequipItemCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.Unequip,
            request.Command.CharacterId,
            ExecuteUnequipAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<SplitItemStackCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.SplitStack,
            request.Command.CharacterId,
            ExecuteSplitStackAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<MergeItemStacksCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.MergeStacks,
            request.Command.CharacterId,
            ExecuteMergeStacksAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<SwapContainerItemsCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.SwapContainerItems,
            request.Command.CharacterId,
            ExecuteSwapContainerItemsAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ConsumeItemQuantityCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.ConsumeQuantity,
            request.Command.CharacterId,
            ExecuteConsumeQuantityAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<DestroyItemCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.Destroy,
            request.Command.CharacterId,
            ExecuteDestroyAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<SwapBagAggregatesCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.SwapBagAggregates,
            request.Command.FirstCharacterId,
            ExecuteSwapBagAggregatesAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<AddRecoveryDeliveryCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.AddRecoveryDelivery,
            request.Command.CharacterId,
            ExecuteAddRecoveryDeliveryAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ClaimRecoveryDeliveryCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.ClaimRecoveryDelivery,
            request.Command.CharacterId,
            ExecuteClaimRecoveryDeliveryAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ChangeSecureContainerTierCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.ChangeSecureContainerTier,
            null,
            ExecuteChangeSecureContainerTierAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ApplyItemPolicyCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.ApplyItemPolicy,
            request.Command.CharacterId,
            ExecuteApplyItemPolicyAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<RemoveInsurancePolicyCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.RemoveInsurancePolicy,
            request.Command.CharacterId,
            ExecuteRemoveInsurancePolicyAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<AbandonQuestItemsCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.AbandonQuestItems,
            request.Command.CharacterId,
            ExecuteAbandonQuestItemsAsync,
            cancellationToken);
    }

    private static async Task ExecuteGrantAsync(
        ItemTransactionContext context,
        GrantItemCommand command,
        CancellationToken cancellationToken)
    {
        await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.LockMutationScopeAsync(
            [],
            [command.DestinationContainerId],
            cancellationToken);

        var definition = await context.LoadDefinitionAsync(command.DefinitionId, cancellationToken);
        if (command.Quantity <= 0
            || command.Quantity > definition.RuntimeDefinition.MaximumStackSize)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStackLimitExceeded,
                "The grant quantity is outside the definition stack limit.");
        }

        var hasPolicySourceKind = !string.IsNullOrWhiteSpace(command.PolicySourceKind);
        var hasPolicySourceId = !string.IsNullOrWhiteSpace(command.PolicySourceId);
        if (hasPolicySourceKind != hasPolicySourceId)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.QuestGrantInvalid,
                "An explicit policy source requires both source kind and source id.");
        }

        var policySourceKind = hasPolicySourceKind
            ? command.PolicySourceKind!.Trim()
            : ItemPolicySourceKinds.CatalogDefault;
        var policySourceId = hasPolicySourceId
            ? command.PolicySourceId!.Trim()
            : definition.RuntimeDefinition.Id;
        if (hasPolicySourceKind)
        {
            context.EnsureSystemAuthority();
            if (!string.Equals(
                    policySourceKind,
                    ItemPolicySourceKinds.QuestGrant,
                    StringComparison.Ordinal)
                || !string.Equals(
                    definition.RuntimeDefinition.Category,
                    ItemCategoryIds.QuestItem,
                    StringComparison.Ordinal)
                || !definition.RuntimeDefinition.DefaultPolicies.Contains(
                    ItemPolicyIds.ProtectedOnDeath,
                    StringComparer.Ordinal))
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.QuestGrantInvalid,
                    "Explicit grant lineage is limited to protected quest item grants.");
            }

            var existingQuestItemId = await context.Connection.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    """
                    select item.id
                    from item_instances item
                    left join item_containers container on container.id = item.container_id
                    left join item_instances source_bag
                      on source_bag.id = container.bound_bag_item_instance_id
                    join item_instance_policies policy
                      on policy.item_instance_id = item.id
                     and policy.policy_kind = 'protected_on_death'
                     and policy.status = 'active'
                    where coalesce(
                            item.equipped_character_id,
                            container.owner_character_id,
                            source_bag.equipped_character_id) = @CharacterId
                      and item.definition_id = @DefinitionId
                      and policy.source_kind = @SourceKind
                      and policy.source_id = @SourceId
                    order by item.id
                    limit 1;
                    """,
                    new
                    {
                        command.CharacterId,
                        DefinitionId = definition.RuntimeDefinition.Id,
                        SourceKind = policySourceKind,
                        SourceId = policySourceId
                    },
                    context.Transaction,
                    cancellationToken: cancellationToken));
            if (existingQuestItemId is not null)
            {
                context.AddMetadataAudit(
                    "quest_grant_already_satisfied",
                    new { QuestGrantId = policySourceId, ItemInstanceId = existingQuestItemId },
                    new { QuestGrantId = policySourceId, ItemInstanceId = existingQuestItemId });
                context.IncludeResultItem(existingQuestItemId.Value);
                return;
            }
        }

        var destination = await context.LoadOwnedContainerAsync(
            command.DestinationContainerId,
            command.CharacterId,
            cancellationToken);
        var slotIndex = await context.SelectDestinationSlotAsync(
            destination,
            definition,
            command.DestinationSlotIndex,
            false,
            null,
            false,
            cancellationToken);
        var itemInstanceId = Guid.NewGuid();
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

        foreach (var policyKind in definition.RuntimeDefinition.DefaultPolicies)
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
                    ItemInstanceId = itemInstanceId,
                    PolicyKind = policyKind,
                    SourceKind = policySourceKind,
                    SourceId = policySourceId
                },
                context.Transaction,
                cancellationToken: cancellationToken));
        }

        if (definition.IsBag)
        {
            var bagContainerId = Guid.NewGuid();
            var slotCapacity = await context.Connection.QuerySingleAsync<int>(new CommandDefinition(
                """
                select count(*)
                from bag_definition_slots
                where definition_id = @DefinitionId;
                """,
                new { DefinitionId = definition.RuntimeDefinition.Id },
                context.Transaction,
                cancellationToken: cancellationToken));
            if (slotCapacity <= 0)
            {
                throw new InvalidOperationException(
                    $"Bag definition '{definition.RuntimeDefinition.Id}' has no slots.");
            }

            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                insert into item_containers (
                    id,
                    container_type,
                    bound_bag_item_instance_id,
                    slot_capacity,
                    lifecycle)
                values (
                    @ContainerId,
                    'bag_contents',
                    @ItemInstanceId,
                    @SlotCapacity,
                    'closed');

                insert into item_container_slots (container_id, slot_index, slot_kind)
                select @ContainerId, slot_index, slot_kind
                from bag_definition_slots
                where definition_id = @DefinitionId
                order by slot_index;

                insert into item_container_slot_tags (container_id, slot_index, tag_id)
                select @ContainerId, slot_index, tag_id
                from bag_definition_slot_tags
                where definition_id = @DefinitionId
                order by slot_index, tag_id;
                """,
                new
                {
                    ContainerId = bagContainerId,
                    ItemInstanceId = itemInstanceId,
                    SlotCapacity = slotCapacity,
                    DefinitionId = definition.RuntimeDefinition.Id
                },
                context.Transaction,
                cancellationToken: cancellationToken));
        }

        var afterState = await context.CaptureItemStateJsonAsync(
            itemInstanceId,
            cancellationToken);
        context.AddItemAudit("item_granted", itemInstanceId, null, afterState);
        context.TouchContainer(destination.ContainerId);
        context.TouchCharacter(command.CharacterId);
        context.IncludeResultItem(itemInstanceId);
    }

    private static async Task ExecuteRelocateAsync(
        ItemTransactionContext context,
        RelocateItemCommand command,
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
        if (item.ContainerId is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemSlotIncompatible,
                "Relocate accepts only an item currently assigned to a container slot.");
        }

        if (string.Equals(item.SourceContainerType, "recovery_storage", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.RecoveryAccessRequired,
                "Recovery Storage items must be withdrawn through a delivery claim.");
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

        var afterState = await context.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        context.AddItemAudit("item_relocated", item.ItemInstanceId, beforeState, afterState);
        context.TouchContainer(item.ContainerId);
        context.TouchContainer(destination.ContainerId);
        context.TouchCharacter(command.CharacterId);
        context.IncludeResultItem(item.ItemInstanceId);
    }

    private static async Task ExecuteSwapContainerItemsAsync(
        ItemTransactionContext context,
        SwapContainerItemsCommand command,
        CancellationToken cancellationToken)
    {
        if (command.FirstItemInstanceId == command.SecondItemInstanceId)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemSlotIncompatible,
                "An item cannot swap its container slot with itself.");
        }

        await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.LockMutationScopeAsync(
            [command.FirstItemInstanceId, command.SecondItemInstanceId],
            [],
            cancellationToken);
        var firstItem = await context.LoadOwnedItemAsync(
            command.FirstItemInstanceId,
            command.CharacterId,
            command.ExpectedFirstItemRevision,
            cancellationToken);
        var secondItem = await context.LoadOwnedItemAsync(
            command.SecondItemInstanceId,
            command.CharacterId,
            command.ExpectedSecondItemRevision,
            cancellationToken);
        if (firstItem.ContainerId is null
            || firstItem.ContainerSlotIndex is null
            || secondItem.ContainerId is null
            || secondItem.ContainerSlotIndex is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemSlotIncompatible,
                "Ordinary item swaps require two container-assigned items.");
        }

        if (string.Equals(
                firstItem.SourceContainerType,
                "recovery_storage",
                StringComparison.Ordinal)
            || string.Equals(
                secondItem.SourceContainerType,
                "recovery_storage",
                StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.RecoveryAccessRequired,
                "Recovery Storage items must be withdrawn through a delivery claim.");
        }

        var firstContainer = await context.LoadOwnedContainerAsync(
            firstItem.ContainerId.Value,
            command.CharacterId,
            cancellationToken);
        var secondContainer = await context.LoadOwnedContainerAsync(
            secondItem.ContainerId.Value,
            command.CharacterId,
            cancellationToken);
        var firstDefinition = await context.LoadDefinitionAsync(
            firstItem.DefinitionId,
            cancellationToken);
        var secondDefinition = await context.LoadDefinitionAsync(
            secondItem.DefinitionId,
            cancellationToken);
        var firstBagHasContents = await context.BagHasContentsAsync(
            firstItem,
            cancellationToken);
        var secondBagHasContents = await context.BagHasContentsAsync(
            secondItem,
            cancellationToken);

        await context.SelectDestinationSlotAsync(
            secondContainer,
            firstDefinition,
            secondItem.ContainerSlotIndex.Value,
            firstBagHasContents,
            firstItem.IsBag ? firstItem.ItemInstanceId : null,
            false,
            cancellationToken,
            secondItem.ItemInstanceId);
        await context.SelectDestinationSlotAsync(
            firstContainer,
            secondDefinition,
            firstItem.ContainerSlotIndex.Value,
            secondBagHasContents,
            secondItem.IsBag ? secondItem.ItemInstanceId : null,
            false,
            cancellationToken,
            firstItem.ItemInstanceId);

        var firstBeforeState = await context.CaptureItemStateJsonAsync(
            firstItem.ItemInstanceId,
            cancellationToken);
        var secondBeforeState = await context.CaptureItemStateJsonAsync(
            secondItem.ItemInstanceId,
            cancellationToken);

        await context.Connection.ExecuteAsync(new CommandDefinition(
            "set constraints ux_item_instances_container_slot deferred;",
            transaction: context.Transaction,
            cancellationToken: cancellationToken));
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update item_instances
            set container_id = case
                    when id = @FirstItemInstanceId then @SecondContainerId
                    else @FirstContainerId
                end,
                container_slot_index = case
                    when id = @FirstItemInstanceId then @SecondContainerSlotIndex
                    else @FirstContainerSlotIndex
                end,
                revision = revision + 1,
                updated_at = now()
            where id = any(@ItemInstanceIds);
            """,
            new
            {
                command.FirstItemInstanceId,
                command.SecondItemInstanceId,
                ItemInstanceIds = new[]
                {
                    command.FirstItemInstanceId,
                    command.SecondItemInstanceId
                },
                FirstContainerId = firstContainer.ContainerId,
                FirstContainerSlotIndex = firstItem.ContainerSlotIndex.Value,
                SecondContainerId = secondContainer.ContainerId,
                SecondContainerSlotIndex = secondItem.ContainerSlotIndex.Value
            },
            context.Transaction,
            cancellationToken: cancellationToken));

        var firstAfterState = await context.CaptureItemStateJsonAsync(
            firstItem.ItemInstanceId,
            cancellationToken);
        var secondAfterState = await context.CaptureItemStateJsonAsync(
            secondItem.ItemInstanceId,
            cancellationToken);
        context.AddItemAudit(
            "container_items_swapped",
            firstItem.ItemInstanceId,
            firstBeforeState,
            firstAfterState);
        context.AddItemAudit(
            "container_items_swapped",
            secondItem.ItemInstanceId,
            secondBeforeState,
            secondAfterState);
        context.TouchContainer(firstContainer.ContainerId);
        context.TouchContainer(secondContainer.ContainerId);
        context.TouchCharacter(command.CharacterId);
        context.IncludeResultItem(firstItem.ItemInstanceId);
        context.IncludeResultItem(secondItem.ItemInstanceId);
    }

    private static async Task ExecuteEquipAsync(
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

    private static async Task ExecuteUnequipAsync(
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

    private static async Task ExecuteSplitStackAsync(
        ItemTransactionContext context,
        SplitItemStackCommand command,
        CancellationToken cancellationToken)
    {
        await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.LockMutationScopeAsync(
            [command.ItemInstanceId],
            [command.DestinationContainerId],
            cancellationToken);
        var source = await context.LoadOwnedItemAsync(
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

        var definition = await context.LoadDefinitionAsync(source.DefinitionId, cancellationToken);
        if (definition.IsBag || definition.RuntimeDefinition.MaximumStackSize <= 1)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStackIncompatible,
                "The item definition is not stackable.");
        }

        var destination = await context.LoadOwnedContainerAsync(
            command.DestinationContainerId,
            command.CharacterId,
            cancellationToken);
        var destinationSlotIndex = await context.SelectDestinationSlotAsync(
            destination,
            definition,
            command.DestinationSlotIndex,
            false,
            null,
            false,
            cancellationToken);
        var policies = await context.LoadPoliciesAsync(source.ItemInstanceId, cancellationToken);
        var beforeSource = await context.CaptureItemStateJsonAsync(
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
                     string.Equals(policy.Status, "active", StringComparison.Ordinal)))
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

        var afterSource = await context.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var afterSplit = await context.CaptureItemStateJsonAsync(
            splitItemInstanceId,
            cancellationToken);
        context.AddItemAudit(
            "stack_split_source",
            source.ItemInstanceId,
            beforeSource,
            afterSource);
        context.AddItemAudit(
            "stack_split_created",
            splitItemInstanceId,
            null,
            afterSplit);
        context.TouchContainer(source.ContainerId);
        context.TouchContainer(destination.ContainerId);
        context.TouchCharacter(command.CharacterId);
        context.IncludeResultItem(source.ItemInstanceId);
        context.IncludeResultItem(splitItemInstanceId);
    }

    private static async Task ExecuteMergeStacksAsync(
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

        await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.LockMutationScopeAsync(
            [command.SourceItemInstanceId, command.TargetItemInstanceId],
            [],
            cancellationToken);
        var source = await context.LoadOwnedItemAsync(
            command.SourceItemInstanceId,
            command.CharacterId,
            command.ExpectedSourceItemRevision,
            cancellationToken);
        var target = await context.LoadOwnedItemAsync(
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

        var definition = await context.LoadDefinitionAsync(source.DefinitionId, cancellationToken);
        var sourcePolicies = await context.LoadPoliciesAsync(source.ItemInstanceId, cancellationToken);
        var targetPolicies = await context.LoadPoliciesAsync(target.ItemInstanceId, cancellationToken);
        var sourceCapabilities = ItemPolicyRules.Evaluate(
            definition.RuntimeDefinition,
            ToPolicyStates(sourcePolicies));
        var targetCapabilities = ItemPolicyRules.Evaluate(
            definition.RuntimeDefinition,
            ToPolicyStates(targetPolicies));
        if (!sourceCapabilities.CanStack || !targetCapabilities.CanStack)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "The effective item policy does not permit stacking.");
        }

        var sourceState = new ItemStackState(
            source.DefinitionId,
            source.Quantity,
            ItemTransactionContext.CalculatePolicyFingerprint(
                sourcePolicies.Where(policy => string.Equals(
                    policy.Status,
                    "active",
                    StringComparison.Ordinal))),
            EmptyStackStateFingerprint);
        var targetState = new ItemStackState(
            target.DefinitionId,
            target.Quantity,
            ItemTransactionContext.CalculatePolicyFingerprint(
                targetPolicies.Where(policy => string.Equals(
                    policy.Status,
                    "active",
                    StringComparison.Ordinal))),
            EmptyStackStateFingerprint);
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

        var beforeSource = await context.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var beforeTarget = await context.CaptureItemStateJsonAsync(
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
        var afterTarget = await context.CaptureItemStateJsonAsync(
            target.ItemInstanceId,
            cancellationToken);
        context.AddItemAudit(
            "stack_merge_target",
            target.ItemInstanceId,
            beforeTarget,
            afterTarget);
        context.AddItemAudit(
            "stack_merge_source_removed",
            source.ItemInstanceId,
            beforeSource,
            null);
        context.TouchContainer(source.ContainerId);
        context.TouchContainer(target.ContainerId);
        context.TouchCharacter(command.CharacterId);
        context.IncludeResultItem(target.ItemInstanceId);
    }

    private static async Task ExecuteConsumeQuantityAsync(
        ItemTransactionContext context,
        ConsumeItemQuantityCommand command,
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
        var definition = await context.LoadDefinitionAsync(item.DefinitionId, cancellationToken);
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

        var beforeState = await context.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        if (command.Quantity == item.Quantity)
        {
            await context.Connection.ExecuteAsync(new CommandDefinition(
                "delete from item_instances where id = @ItemInstanceId;",
                new { item.ItemInstanceId },
                context.Transaction,
                cancellationToken: cancellationToken));
            context.AddItemAudit(
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
            var afterState = await context.CaptureItemStateJsonAsync(
                item.ItemInstanceId,
                cancellationToken);
            context.AddItemAudit(
                "item_quantity_consumed",
                item.ItemInstanceId,
                beforeState,
                afterState);
            context.IncludeResultItem(item.ItemInstanceId);
        }

        context.TouchContainer(item.ContainerId);
        context.TouchCharacter(command.CharacterId);
    }

    private static async Task ExecuteDestroyAsync(
        ItemTransactionContext context,
        DestroyItemCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemDestroyForbidden,
                "An item destruction reason is required.");
        }

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
        if (string.Equals(item.SourceContainerType, "recovery_storage", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.RecoveryAccessRequired,
                "Recovery Storage items cannot use player destruction.");
        }

        var definition = await context.LoadDefinitionAsync(item.DefinitionId, cancellationToken);
        var policies = await context.LoadPoliciesAsync(item.ItemInstanceId, cancellationToken);
        var capabilities = ItemPolicyRules.Evaluate(
            definition.RuntimeDefinition,
            ToPolicyStates(policies));
        if (string.Equals(
                definition.RuntimeDefinition.Category,
                ItemCategoryIds.QuestItem,
                StringComparison.Ordinal)
            && capabilities.DeathDisposition == ItemDeathDisposition.ProtectedRecovery)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "A protected quest item cannot use direct player destruction.");
        }

        if (!capabilities.CanPlayerDestroy)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemDestroyForbidden,
                "The item definition does not permit player destruction.");
        }

        if (await context.BagHasContentsAsync(item, cancellationToken))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagNotEmpty,
                "A non-empty Bag cannot be destroyed.");
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
                @Reason);

            delete from item_instances
            where id = @ItemInstanceId;
            """,
            new
            {
                item.ItemInstanceId,
                item.DefinitionId,
                context.OperationId,
                item.Quantity,
                Reason = command.Reason.Trim()
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        context.AddItemAudit(
            "item_destroyed",
            item.ItemInstanceId,
            beforeState,
            null);
        context.TouchContainer(item.ContainerId);
        context.TouchCharacter(command.CharacterId);
    }

    private static async Task ExecuteApplyItemPolicyAsync(
        ItemTransactionContext context,
        ApplyItemPolicyCommand command,
        CancellationToken cancellationToken)
    {
        context.EnsureSystemAuthority();
        if (string.IsNullOrWhiteSpace(command.PolicyKind)
            || string.IsNullOrWhiteSpace(command.SourceKind)
            || string.IsNullOrWhiteSpace(command.SourceId))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "Policy kind and source lineage are required.");
        }

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
        var definition = await context.LoadDefinitionAsync(item.DefinitionId, cancellationToken);
        var policyKind = command.PolicyKind.Trim();
        if (string.Equals(policyKind, ItemPolicyIds.Insured, StringComparison.Ordinal))
        {
            if (!ItemPolicyRules.CanApplyInsurance(definition.RuntimeDefinition))
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.ItemPolicyRestricted,
                    "Insurance requires a non-stackable equipment-compatible item definition.");
            }
        }
        else if (!string.Equals(
                     policyKind,
                     ItemPolicyIds.ProtectedOnDeath,
                     StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "The requested item policy kind is unsupported.");
        }

        var policies = await context.LoadPoliciesAsync(item.ItemInstanceId, cancellationToken);
        if (policies.Any(policy =>
                string.Equals(policy.Status, ItemPolicyRules.ActiveStatus, StringComparison.Ordinal)
                && string.Equals(policy.PolicyKind, policyKind, StringComparison.Ordinal)))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "The item already has an active policy of the requested kind.");
        }

        var beforeState = await context.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
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

            update item_instances
            set revision = revision + 1,
                updated_at = now()
            where id = @ItemInstanceId;
            """,
            new
            {
                PolicyId = Guid.NewGuid(),
                item.ItemInstanceId,
                PolicyKind = policyKind,
                SourceKind = command.SourceKind.Trim(),
                SourceId = command.SourceId.Trim()
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        var afterState = await context.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        context.AddItemAudit("item_policy_applied", item.ItemInstanceId, beforeState, afterState);
        context.TouchContainer(item.ContainerId);
        context.TouchCharacter(command.CharacterId);
        context.IncludeResultItem(item.ItemInstanceId);
    }

    private static async Task ExecuteRemoveInsurancePolicyAsync(
        ItemTransactionContext context,
        RemoveInsurancePolicyCommand command,
        CancellationToken cancellationToken)
    {
        context.EnsureSystemAuthority();
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
        var policies = await context.LoadPoliciesAsync(item.ItemInstanceId, cancellationToken);
        var insurance = policies.SingleOrDefault(policy =>
            string.Equals(policy.Status, ItemPolicyRules.ActiveStatus, StringComparison.Ordinal)
            && string.Equals(policy.PolicyKind, ItemPolicyIds.Insured, StringComparison.Ordinal));
        if (insurance is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyNotFound,
                "The item does not have active insurance.");
        }

        var beforeState = await context.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update item_instance_policies
            set status = 'removed',
                revision = revision + 1,
                removed_at = now()
            where id = @PolicyId;

            update item_instances
            set revision = revision + 1,
                updated_at = now()
            where id = @ItemInstanceId;
            """,
            new
            {
                insurance.PolicyId,
                item.ItemInstanceId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        var afterState = await context.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        context.AddItemAudit("item_insurance_removed", item.ItemInstanceId, beforeState, afterState);
        context.TouchContainer(item.ContainerId);
        context.TouchCharacter(command.CharacterId);
        context.IncludeResultItem(item.ItemInstanceId);
    }

    private static async Task ExecuteAbandonQuestItemsAsync(
        ItemTransactionContext context,
        AbandonQuestItemsCommand command,
        CancellationToken cancellationToken)
    {
        context.EnsureSystemAuthority();
        if (string.IsNullOrWhiteSpace(command.QuestGrantId))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.QuestGrantInvalid,
                "A quest grant id is required.");
        }

        await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        var questGrantId = command.QuestGrantId.Trim();
        var itemIds = (await context.Connection.QueryAsync<Guid>(new CommandDefinition(
            QuestGrantItemIdsSql,
            new
            {
                command.CharacterId,
                QuestGrantId = questGrantId,
                SourceKind = ItemPolicySourceKinds.QuestGrant
            },
            context.Transaction,
            cancellationToken: cancellationToken))).ToArray();
        await context.LockMutationScopeAsync(itemIds, [], cancellationToken);
        itemIds = (await context.Connection.QueryAsync<Guid>(new CommandDefinition(
            QuestGrantItemIdsSql,
            new
            {
                command.CharacterId,
                QuestGrantId = questGrantId,
                SourceKind = ItemPolicySourceKinds.QuestGrant
            },
            context.Transaction,
            cancellationToken: cancellationToken))).ToArray();

        if (itemIds.Length == 0)
        {
            var unchanged = new { QuestGrantId = questGrantId, RemovedItemCount = 0 };
            context.AddMetadataAudit("quest_items_already_absent", unchanged, unchanged);
            return;
        }

        foreach (var itemInstanceId in itemIds.Order())
        {
            var item = await context.LoadItemAsync(itemInstanceId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Quest item '{itemInstanceId}' disappeared while its owner was locked.");
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
                    'quest_abandoned');

                delete from item_instances
                where id = @ItemInstanceId;
                """,
                new
                {
                    item.ItemInstanceId,
                    item.DefinitionId,
                    context.OperationId,
                    item.Quantity
                },
                context.Transaction,
                cancellationToken: cancellationToken));
            context.AddItemAudit("quest_item_removed", item.ItemInstanceId, beforeState, null);
            context.TouchContainer(item.ContainerId);
        }

        context.TouchCharacter(command.CharacterId);
    }

    private const string QuestGrantItemIdsSql =
        """
        select item.id
        from item_instances item
        join item_definitions definition
          on definition.id = item.definition_id
         and definition.category_id = 'quest_item'
        left join item_containers container on container.id = item.container_id
        left join item_instances source_bag
          on source_bag.id = container.bound_bag_item_instance_id
        join item_instance_policies policy
          on policy.item_instance_id = item.id
         and policy.policy_kind = 'protected_on_death'
         and policy.status = 'active'
         and policy.source_kind = @SourceKind
        where coalesce(
                item.equipped_character_id,
                container.owner_character_id,
                source_bag.equipped_character_id) = @CharacterId
          and policy.source_id = @QuestGrantId
        order by item.id;
        """;

    private static IEnumerable<ItemPolicyState> ToPolicyStates(
        IEnumerable<LockedItemPolicy> policies)
    {
        return policies.Select(policy => new ItemPolicyState(
            policy.PolicyKind,
            policy.Status,
            policy.SourceKind,
            policy.SourceId));
    }

    private static async Task EnsureBagAggregateTransferAllowedAsync(
        ItemTransactionContext context,
        LockedItem bag,
        CancellationToken cancellationToken)
    {
        var aggregateItemIds = await context.LoadBagAggregateItemIdsAsync(
            bag.ItemInstanceId,
            cancellationToken);
        foreach (var itemInstanceId in aggregateItemIds)
        {
            var item = itemInstanceId == bag.ItemInstanceId
                ? bag
                : await context.LoadItemAsync(itemInstanceId, cancellationToken);
            if (item is null)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.BagStateChanged,
                    "A Bag aggregate item changed before policy validation completed.");
            }

            var definition = await context.LoadDefinitionAsync(
                item.DefinitionId,
                cancellationToken);
            var policies = await context.LoadPoliciesAsync(
                item.ItemInstanceId,
                cancellationToken);
            var capabilities = ItemPolicyRules.Evaluate(
                definition.RuntimeDefinition,
                ToPolicyStates(policies));
            if (!capabilities.CanChangeOwningCharacter)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.ItemPolicyRestricted,
                    "An active item policy prevents the Bag aggregate from changing character ownership.");
            }
        }
    }

    private static async Task ExecuteSwapBagAggregatesAsync(
        ItemTransactionContext context,
        SwapBagAggregatesCommand command,
        CancellationToken cancellationToken)
    {
        if (command.FirstCharacterId == command.SecondCharacterId
            || command.FirstBagItemInstanceId == command.SecondBagItemInstanceId)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "A Bag aggregate swap requires two distinct characters and Bag items.");
        }

        await context.LockCharacterStatesAsync(
            [
                new CharacterLockRequest(
                    command.FirstCharacterId,
                    command.ExpectedFirstCharacterRevision),
                new CharacterLockRequest(
                    command.SecondCharacterId,
                    command.ExpectedSecondCharacterRevision)
            ],
            cancellationToken);
        await context.LockMutationScopeAsync(
            [command.FirstBagItemInstanceId, command.SecondBagItemInstanceId],
            [],
            cancellationToken);
        var firstBag = await context.LoadOwnedItemAsync(
            command.FirstBagItemInstanceId,
            command.FirstCharacterId,
            command.ExpectedFirstBagRevision,
            cancellationToken);
        var secondBag = await context.LoadOwnedItemAsync(
            command.SecondBagItemInstanceId,
            command.SecondCharacterId,
            command.ExpectedSecondBagRevision,
            cancellationToken);
        EnsureEquippedBagAggregate(
            firstBag,
            command.FirstCharacterId,
            command.ExpectedFirstBagContentsRevision);
        EnsureEquippedBagAggregate(
            secondBag,
            command.SecondCharacterId,
            command.ExpectedSecondBagContentsRevision);
        await EnsureBagAggregateTransferAllowedAsync(context, firstBag, cancellationToken);
        await EnsureBagAggregateTransferAllowedAsync(context, secondBag, cancellationToken);

        var beforeFirst = await context.CaptureItemStateJsonAsync(
            firstBag.ItemInstanceId,
            cancellationToken);
        var beforeSecond = await context.CaptureItemStateJsonAsync(
            secondBag.ItemInstanceId,
            cancellationToken);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            set constraints ux_item_instances_equipment_slot deferred;

            update item_instances
            set equipped_character_id = case id
                    when @FirstBagItemInstanceId then @SecondCharacterId
                    when @SecondBagItemInstanceId then @FirstCharacterId
                end,
                revision = revision + 1,
                updated_at = now()
            where id in (@FirstBagItemInstanceId, @SecondBagItemInstanceId);
            """,
            new
            {
                FirstBagItemInstanceId = firstBag.ItemInstanceId,
                SecondBagItemInstanceId = secondBag.ItemInstanceId,
                command.FirstCharacterId,
                command.SecondCharacterId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        var afterFirst = await context.CaptureItemStateJsonAsync(
            firstBag.ItemInstanceId,
            cancellationToken);
        var afterSecond = await context.CaptureItemStateJsonAsync(
            secondBag.ItemInstanceId,
            cancellationToken);
        context.AddItemAudit(
            "bag_aggregate_swapped",
            firstBag.ItemInstanceId,
            beforeFirst,
            afterFirst);
        context.AddItemAudit(
            "bag_aggregate_swapped",
            secondBag.ItemInstanceId,
            beforeSecond,
            afterSecond);
        context.TouchContainer(firstBag.BagContentsContainerId);
        context.TouchContainer(secondBag.BagContentsContainerId);
        context.TouchCharacter(command.FirstCharacterId);
        context.TouchCharacter(command.SecondCharacterId);
        context.IncludeResultItem(firstBag.ItemInstanceId);
        context.IncludeResultItem(secondBag.ItemInstanceId);
    }

    private static async Task ExecuteAddRecoveryDeliveryAsync(
        ItemTransactionContext context,
        AddRecoveryDeliveryCommand command,
        CancellationToken cancellationToken)
    {
        context.EnsureSystemAuthority();
        ValidateRecoverySource(command.SourceKind, command.SourceEventId);
        ValidateRecoveryTimeline(command.AvailableAt, command.ExpiresAt);
        var expectations = ValidateDistinctItemExpectations(command.Items);
        var states = await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        var state = states[command.CharacterId];
        await context.LockMutationScopeAsync(
            expectations.Keys,
            [state.RecoveryStorageContainerId],
            cancellationToken);
        var recovery = await context.LoadOwnedContainerAsync(
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
            var item = await context.LoadOwnedItemAsync(
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

            if (await context.BagHasContentsAsync(item, cancellationToken))
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
        var nextSlotIndex = await GetNextRecoverySlotIndexAsync(
            context,
            recovery.ContainerId,
            cancellationToken);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var beforeState = await context.CaptureItemStateJsonAsync(
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
                context.TouchContainer(item.BagContentsContainerId);
            }

            var afterState = await context.CaptureItemStateJsonAsync(
                item.ItemInstanceId,
                cancellationToken);
            context.AddItemAudit(
                "recovery_delivery_added",
                item.ItemInstanceId,
                beforeState,
                afterState);
            context.TouchContainer(item.ContainerId);
            context.IncludeResultItem(item.ItemInstanceId);
        }

        context.TouchContainer(recovery.ContainerId);
        context.TouchCharacter(command.CharacterId);
        context.AddRecoveryDelivery(deliveryId);
    }

    private static async Task ExecuteClaimRecoveryDeliveryAsync(
        ItemTransactionContext context,
        ClaimRecoveryDeliveryCommand command,
        CancellationToken cancellationToken)
    {
        var expectedItems = ValidateDistinctItemExpectations(command.Items);
        var states = await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        var state = states[command.CharacterId];
        var preview = await LoadRecoveryDeliveryAsync(
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

        await context.LockMutationScopeAsync(
            preview.Items.Select(item => item.ItemInstanceId),
            [state.RecoveryStorageContainerId, command.DestinationContainerId],
            cancellationToken);
        var delivery = await LoadRecoveryDeliveryAsync(
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

        var destination = await context.LoadOwnedContainerAsync(
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
            var item = await context.LoadOwnedItemAsync(
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

            var definition = await context.LoadDefinitionAsync(item.DefinitionId, cancellationToken);
            var bagHasContents = await context.BagHasContentsAsync(item, cancellationToken);
            var destinationSlotIndex = await context.SelectDestinationSlotAsync(
                destination,
                definition,
                null,
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
            var afterState = await context.CaptureItemStateJsonAsync(
                item.ItemInstanceId,
                cancellationToken);
            context.AddItemAudit(
                "recovery_delivery_claimed",
                item.ItemInstanceId,
                beforeState,
                afterState);
            context.IncludeResultItem(item.ItemInstanceId);
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
        context.TouchContainer(state.RecoveryStorageContainerId);
        context.TouchContainer(destination.ContainerId);
        context.TouchCharacter(command.CharacterId);
        context.AddRecoveryDelivery(delivery.DeliveryId);
    }

    private static async Task ExecuteChangeSecureContainerTierAsync(
        ItemTransactionContext context,
        ChangeSecureContainerTierCommand command,
        CancellationToken cancellationToken)
    {
        if (command.AccountId == Guid.Empty || string.IsNullOrWhiteSpace(command.TierId))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemNotFound,
                "An account id and Secure Container tier id are required.");
        }

        context.EnsureAccountAuthority(command.AccountId);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            select pg_advisory_xact_lock(
                hashtextextended('item-secure-entitlement:' || cast(@AccountId as text), 0));
            """,
            new { command.AccountId },
            context.Transaction,
            cancellationToken: cancellationToken));

        var candidates = (await context.Connection.QueryAsync<CharacterLockCandidate>(
            new CommandDefinition(
                """
                select
                    state.character_id as "CharacterId",
                    state.revision as "Revision"
                from character_item_states state
                join characters character on character.id = state.character_id
                where character.account_id = @AccountId
                  and character.deleted_at is null
                order by state.character_id;
                """,
                new { command.AccountId },
                context.Transaction,
                cancellationToken: cancellationToken))).ToArray();
        var suppliedCharacterRevisions = command.CharacterRevisions
            .GroupBy(expectation => expectation.CharacterId)
            .ToDictionary(group => group.Key, group => group.Select(value => value.Revision).Distinct().ToArray());
        if (suppliedCharacterRevisions.Values.Any(revisions => revisions.Length != 1))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "Secure Container tier change character revisions contain duplicates.");
        }

        if (context.Actor.Authority == ItemTransactionAuthority.Account
            && !candidates.Select(candidate => candidate.CharacterId).Order().SequenceEqual(
                suppliedCharacterRevisions.Keys.Order()))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "An account tier change requires every affected character revision.");
        }

        if (suppliedCharacterRevisions.Keys.Any(characterId =>
                candidates.All(candidate => candidate.CharacterId != characterId)))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "A supplied character revision does not belong to the account.");
        }

        var lockRequests = candidates.Select(candidate => new CharacterLockRequest(
            candidate.CharacterId,
            suppliedCharacterRevisions.TryGetValue(candidate.CharacterId, out var revisions)
                ? revisions[0]
                : null));
        var states = await context.LockCharacterStatesAsync(lockRequests, cancellationToken);
        var entitlement = await context.Connection.QuerySingleOrDefaultAsync<SecureEntitlementRow>(
            new CommandDefinition(
                """
                select
                    account_id as "AccountId",
                    tier_id as "TierId",
                    revision as "Revision"
                from account_secure_container_entitlements
                where account_id = @AccountId
                for update;
                """,
                new { command.AccountId },
                context.Transaction,
                cancellationToken: cancellationToken));
        if (entitlement is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemNotFound,
                "The account Secure Container entitlement was not found.");
        }

        if (entitlement.Revision != command.ExpectedEntitlementRevision)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The Secure Container entitlement changed before the operation committed.");
        }

        var tier = await context.Connection.QuerySingleOrDefaultAsync<SecureTierRow>(
            new CommandDefinition(
                """
                select
                    id as "TierId",
                    slot_capacity as "SlotCapacity"
                from secure_container_tiers
                where id = @TierId
                  and is_active;
                """,
                new { command.TierId },
                context.Transaction,
                cancellationToken: cancellationToken));
        if (tier is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemNotFound,
                "The Secure Container tier was not found.");
        }

        if (string.Equals(entitlement.TierId, tier.TierId, StringComparison.Ordinal))
        {
            var unchanged = new SecureEntitlementAuditState(
                command.AccountId,
                entitlement.TierId,
                entitlement.Revision);
            context.AddMetadataAudit("secure_tier_unchanged", unchanged, unchanged);
            context.SetSecureContainerEntitlementRevision(entitlement.Revision);
            return;
        }

        var allContainerIds = states.Values
            .SelectMany(state => new[]
            {
                state.SecureContainerId,
                state.RecoveryStorageContainerId
            })
            .Distinct()
            .ToArray();
        var overflowItems = await LoadSecureOverflowItemsAsync(
            context,
            states.Values.Select(state => state.SecureContainerId),
            tier.SlotCapacity,
            cancellationToken);
        await context.LockMutationScopeAsync(
            overflowItems.Select(item => item.ItemInstanceId),
            allContainerIds,
            cancellationToken);
        overflowItems = await LoadSecureOverflowItemsAsync(
            context,
            states.Values.Select(state => state.SecureContainerId),
            tier.SlotCapacity,
            cancellationToken);

        var entitlementBefore = new SecureEntitlementAuditState(
            command.AccountId,
            entitlement.TierId,
            entitlement.Revision);
        var entitlementRevision = await context.Connection.QuerySingleAsync<long>(
            new CommandDefinition(
                """
                update account_secure_container_entitlements
                set tier_id = @TierId,
                    revision = revision + 1,
                    updated_at = now()
                where account_id = @AccountId
                returning revision;
                """,
                new { command.AccountId, TierId = tier.TierId },
                context.Transaction,
                cancellationToken: cancellationToken));

        foreach (var state in states.Values.OrderBy(value => value.CharacterId))
        {
            var secureContainer = await context.LoadOwnedContainerAsync(
                state.SecureContainerId,
                state.CharacterId,
                cancellationToken);
            var recoveryContainer = await context.LoadOwnedContainerAsync(
                state.RecoveryStorageContainerId,
                state.CharacterId,
                cancellationToken);
            if (!string.Equals(secureContainer.ContainerType, "secure_container", StringComparison.Ordinal)
                || !string.Equals(recoveryContainer.ContainerType, "recovery_storage", StringComparison.Ordinal)
                || secureContainer.SlotCapacity is null)
            {
                throw new InvalidOperationException(
                    $"Character '{state.CharacterId}' has invalid Secure Container state.");
            }

            var characterOverflow = overflowItems
                .Where(item => item.CharacterId == state.CharacterId)
                .OrderByDescending(item => item.ContainerSlotIndex)
                .ThenBy(item => item.ItemInstanceId)
                .ToArray();
            if (characterOverflow.Length > 0)
            {
                var deliveryId = Guid.NewGuid();
                await context.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into recovery_deliveries (
                        id,
                        character_id,
                        recovery_storage_container_id,
                        source_kind,
                        source_event_id,
                        available_at)
                    values (
                        @DeliveryId,
                        @CharacterId,
                        @RecoveryStorageContainerId,
                        'secure_capacity_reduction',
                        @SourceEventId,
                        now());
                    """,
                    new
                    {
                        DeliveryId = deliveryId,
                        state.CharacterId,
                        RecoveryStorageContainerId = recoveryContainer.ContainerId,
                        SourceEventId = context.OperationId.ToString("N")
                    },
                    context.Transaction,
                    cancellationToken: cancellationToken));
                var nextRecoverySlotIndex = await GetNextRecoverySlotIndexAsync(
                    context,
                    recoveryContainer.ContainerId,
                    cancellationToken);
                for (var index = 0; index < characterOverflow.Length; index++)
                {
                    var overflow = characterOverflow[index];
                    var item = await context.LoadOwnedItemAsync(
                        overflow.ItemInstanceId,
                        state.CharacterId,
                        overflow.Revision,
                        cancellationToken);
                    var beforeState = await context.CaptureItemStateJsonAsync(
                        item.ItemInstanceId,
                        cancellationToken);
                    await context.Connection.ExecuteAsync(new CommandDefinition(
                        """
                        insert into item_container_slots (container_id, slot_index, slot_kind)
                        values (@RecoveryStorageContainerId, @RecoverySlotIndex, 'general');

                        update item_instances
                        set container_id = @RecoveryStorageContainerId,
                            container_slot_index = @RecoverySlotIndex,
                            revision = revision + 1,
                            updated_at = now()
                        where id = @ItemInstanceId;

                        insert into recovery_delivery_items (
                            recovery_delivery_id,
                            item_instance_id,
                            item_order)
                        values (@DeliveryId, @ItemInstanceId, @ItemOrder);
                        """,
                        new
                        {
                            RecoveryStorageContainerId = recoveryContainer.ContainerId,
                            RecoverySlotIndex = nextRecoverySlotIndex + index,
                            item.ItemInstanceId,
                            DeliveryId = deliveryId,
                            ItemOrder = index
                        },
                        context.Transaction,
                        cancellationToken: cancellationToken));
                    var afterState = await context.CaptureItemStateJsonAsync(
                        item.ItemInstanceId,
                        cancellationToken);
                    context.AddItemAudit(
                        "secure_capacity_reduction_recovery",
                        item.ItemInstanceId,
                        beforeState,
                        afterState);
                    context.IncludeResultItem(item.ItemInstanceId);
                }

                context.TouchContainer(recoveryContainer.ContainerId);
                context.AddRecoveryDelivery(deliveryId);
            }

            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                delete from item_container_slots
                where container_id = @SecureContainerId
                  and slot_index >= @SlotCapacity;

                insert into item_container_slots (container_id, slot_index, slot_kind)
                select @SecureContainerId, generated.slot_index, 'general'
                from generate_series(@CurrentSlotCapacity, @SlotCapacity - 1) generated(slot_index)
                on conflict (container_id, slot_index) do nothing;

                update item_containers
                set slot_capacity = @SlotCapacity,
                    updated_at = now()
                where id = @SecureContainerId;
                """,
                new
                {
                    SecureContainerId = secureContainer.ContainerId,
                    SlotCapacity = tier.SlotCapacity,
                    CurrentSlotCapacity = secureContainer.SlotCapacity.Value
                },
                context.Transaction,
                cancellationToken: cancellationToken));
            context.TouchContainer(secureContainer.ContainerId);
            context.TouchCharacter(state.CharacterId);
        }

        var entitlementAfter = new SecureEntitlementAuditState(
            command.AccountId,
            tier.TierId,
            entitlementRevision);
        context.AddMetadataAudit(
            "secure_tier_changed",
            entitlementBefore,
            entitlementAfter);
        context.SetSecureContainerEntitlementRevision(entitlementRevision);
    }

    private static void EnsureEquippedBagAggregate(
        LockedItem bag,
        Guid expectedCharacterId,
        long expectedContentsRevision)
    {
        if (!bag.IsBag
            || bag.EquippedCharacterId != expectedCharacterId
            || !string.Equals(bag.EquipmentSlotId, ItemEquipmentSlotIds.Bag, StringComparison.Ordinal)
            || bag.BagContentsContainerId is null
            || !string.Equals(bag.BagContentsLifecycle, "active", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "The expected equipped Bag aggregate is no longer valid.");
        }

        if (bag.BagContentsRevision != expectedContentsRevision)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "The Bag contents changed before the aggregate swap committed.");
        }
    }

    private static IReadOnlyDictionary<Guid, long> ValidateDistinctItemExpectations(
        IReadOnlyList<ItemRevisionExpectation> expectations)
    {
        if (expectations is null || expectations.Count == 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemNotFound,
                "At least one item revision expectation is required.");
        }

        var grouped = expectations
            .GroupBy(expectation => expectation.ItemInstanceId)
            .ToArray();
        if (grouped.Any(group =>
                group.Key == Guid.Empty
                || group.Count() != 1
                || group.Single().Revision < 0))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "Item revision expectations must contain unique valid item ids and revisions.");
        }

        return grouped.ToDictionary(group => group.Key, group => group.Single().Revision);
    }

    private static void ValidateRecoverySource(string sourceKind, string sourceEventId)
    {
        if (string.IsNullOrWhiteSpace(sourceKind) || string.IsNullOrWhiteSpace(sourceEventId))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemOperationConflict,
                "A Recovery Storage source kind and source event id are required.");
        }
    }

    private static void ValidateRecoveryTimeline(DateTime? availableAt, DateTime? expiresAt)
    {
        if (availableAt is not null && availableAt.Value.Kind != DateTimeKind.Utc)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemOperationConflict,
                "Recovery Storage availability time must use UTC.");
        }

        if (expiresAt is not null && expiresAt.Value.Kind != DateTimeKind.Utc)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemOperationConflict,
                "Recovery Storage expiry time must use UTC.");
        }

        if (availableAt is not null
            && expiresAt is not null
            && expiresAt.Value < availableAt.Value)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemOperationConflict,
                "Recovery Storage expiry cannot precede availability.");
        }
    }

    private static Task<int> GetNextRecoverySlotIndexAsync(
        ItemTransactionContext context,
        Guid recoveryContainerId,
        CancellationToken cancellationToken)
    {
        return context.Connection.QuerySingleAsync<int>(new CommandDefinition(
            """
            select coalesce(max(slot_index) + 1, 0)
            from item_container_slots
            where container_id = @ContainerId;
            """,
            new { ContainerId = recoveryContainerId },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<RecoveryDeliveryState?> LoadRecoveryDeliveryAsync(
        ItemTransactionContext context,
        Guid deliveryId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var deliveryLock = forUpdate ? "for update" : string.Empty;
        var linkLock = forUpdate ? "for update of link" : string.Empty;
        using var grid = await context.Connection.QueryMultipleAsync(new CommandDefinition(
            $"""
            select
                id as "DeliveryId",
                character_id as "CharacterId",
                recovery_storage_container_id as "RecoveryStorageContainerId",
                revision as "Revision",
                claimed_at as "ClaimedAt",
                (available_at is null or available_at <= now()) as "IsAvailable",
                (expires_at is not null and expires_at <= now()) as "IsExpired"
            from recovery_deliveries
            where id = @DeliveryId
            {deliveryLock};

            select
                link.item_instance_id as "ItemInstanceId",
                link.item_order as "ItemOrder",
                item.container_slot_index as "ContainerSlotIndex"
            from recovery_delivery_items link
            join item_instances item on item.id = link.item_instance_id
            where link.recovery_delivery_id = @DeliveryId
            order by link.item_order, link.item_instance_id
            {linkLock};
            """,
            new { DeliveryId = deliveryId },
            context.Transaction,
            cancellationToken: cancellationToken));
        var row = await grid.ReadSingleOrDefaultAsync<RecoveryDeliveryRow>();
        var items = (await grid.ReadAsync<RecoveryDeliveryItemRow>()).ToArray();
        return row is null
            ? null
            : new RecoveryDeliveryState(
                row.DeliveryId,
                row.CharacterId,
                row.RecoveryStorageContainerId,
                row.Revision,
                row.ClaimedAt,
                row.IsAvailable,
                row.IsExpired,
                items);
    }

    private static async Task<SecureOverflowItem[]> LoadSecureOverflowItemsAsync(
        ItemTransactionContext context,
        IEnumerable<Guid> secureContainerIds,
        int slotCapacity,
        CancellationToken cancellationToken)
    {
        var containerIds = secureContainerIds.Distinct().Order().ToArray();
        if (containerIds.Length == 0)
        {
            return [];
        }

        return (await context.Connection.QueryAsync<SecureOverflowItem>(new CommandDefinition(
            """
            select
                container.owner_character_id as "CharacterId",
                item.id as "ItemInstanceId",
                item.revision as "Revision",
                item.container_slot_index as "ContainerSlotIndex"
            from item_instances item
            join item_containers container on container.id = item.container_id
            where item.container_id = any(@ContainerIds)
              and item.container_slot_index >= @SlotCapacity
            order by
                container.owner_character_id,
                item.container_slot_index desc,
                item.id;
            """,
            new { ContainerIds = containerIds, SlotCapacity = slotCapacity },
            context.Transaction,
            cancellationToken: cancellationToken))).ToArray();
    }

    private async Task<ItemTransactionResult> ExecuteInternalAsync<TCommand>(
        ItemTransactionRequest<TCommand> request,
        string operationKind,
        Guid? actorCharacterId,
        Func<ItemTransactionContext, TCommand, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Actor);
        ArgumentNullException.ThrowIfNull(request.Command);
        if (request.OperationId == Guid.Empty)
        {
            throw new ArgumentException("An item operation id is required.", nameof(request));
        }

        var requestPayload = JsonSerializer.Serialize(
            new CanonicalOperationRequest(
                CanonicalizeActor(request.Actor),
                CanonicalizeCommand(request.Command)),
            OperationJsonOptions);
        var requestHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(requestPayload)));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            var context = new ItemTransactionContext(
                connection,
                transaction,
                request.OperationId,
                operationKind,
                request.Actor);
            try
            {
                context.EnsureActorIsValid();
                await context.ValidateLiveAuthorityAsync(
                    actorCharacterId,
                    cancellationToken);
            }
            catch (ItemTransactionRejectedException exception)
            {
                await transaction.RollbackAsync(cancellationToken);
                return CreateRejectedResult(
                    request.OperationId,
                    operationKind,
                    exception.Code,
                    exception.Message);
            }

            var replay = await ClaimOperationAsync(
                connection,
                transaction,
                request.OperationId,
                request.Actor,
                actorCharacterId,
                operationKind,
                requestHash,
                requestPayload,
                cancellationToken);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                $"savepoint {MutationSavepoint};",
                transaction: transaction,
                cancellationToken: cancellationToken));

            try
            {
                await handler(context, request.Command, cancellationToken);
                var result = await context.FinalizeSuccessAsync(cancellationToken);
                await CompleteOperationAsync(
                    connection,
                    transaction,
                    result,
                    "committed",
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (ItemTransactionRejectedException exception)
            {
                return await RollBackMutationAndRejectAsync(
                    connection,
                    transaction,
                    request.OperationId,
                    operationKind,
                    exception.Code,
                    exception.Message,
                    cancellationToken);
            }
            catch (PostgresException exception)
            {
                var mapped = MapPostgresError(exception);
                if (mapped is null)
                {
                    throw;
                }

                return await RollBackMutationAndRejectAsync(
                    connection,
                    transaction,
                    request.OperationId,
                    operationKind,
                    mapped.Code,
                    mapped.Message,
                    cancellationToken);
            }
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<ItemTransactionResult?> ClaimOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        ItemTransactionActor actor,
        Guid? actorCharacterId,
        string operationKind,
        string requestHash,
        string requestPayload,
        CancellationToken cancellationToken)
    {
        var claimed = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            """
            insert into item_operations (
                operation_id,
                actor_account_id,
                actor_character_id,
                operation_kind,
                request_hash,
                request_payload)
            values (
                @OperationId,
                (
                    select id
                    from accounts
                    where id = cast(@ActorAccountId as uuid)
                ),
                (
                    select id
                    from characters
                    where id = cast(@ActorCharacterId as uuid)
                ),
                @OperationKind,
                @RequestHash,
                cast(@RequestPayload as jsonb))
            on conflict (operation_id) do nothing
            returning 1;
            """,
            new
            {
                OperationId = operationId,
                ActorAccountId = actor.AccountId,
                ActorCharacterId = actorCharacterId,
                OperationKind = operationKind,
                RequestHash = requestHash,
                RequestPayload = requestPayload
            },
            transaction,
            cancellationToken: cancellationToken));
        if (claimed is not null)
        {
            return null;
        }

        var existing = await connection.QuerySingleAsync<OperationRow>(new CommandDefinition(
            """
            select
                operation_kind as "OperationKind",
                request_hash as "RequestHash",
                status as "Status",
                result_payload::text as "ResultPayload"
            from item_operations
            where operation_id = @OperationId
            for update;
            """,
            new { OperationId = operationId },
            transaction,
            cancellationToken: cancellationToken));

        if (!string.Equals(existing.OperationKind, operationKind, StringComparison.Ordinal)
            || !string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return CreateRejectedResult(
                operationId,
                operationKind,
                ItemTransactionErrorCodes.ItemOperationConflict,
                "The operation id was already used with a different request.");
        }

        if (string.Equals(existing.Status, "pending", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(existing.ResultPayload))
        {
            return CreateRejectedResult(
                operationId,
                operationKind,
                ItemTransactionErrorCodes.ItemOperationConflict,
                "The operation id is already pending.");
        }

        return JsonSerializer.Deserialize<ItemTransactionResult>(
                existing.ResultPayload,
                OperationJsonOptions)
            ?? throw new InvalidOperationException(
                $"Stored result for item operation '{operationId}' is invalid.");
    }

    private static async Task<ItemTransactionResult> RollBackMutationAndRejectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        string operationKind,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            $"rollback to savepoint {MutationSavepoint};",
            transaction: transaction,
            cancellationToken: cancellationToken));
        var result = CreateRejectedResult(
            operationId,
            operationKind,
            errorCode,
            errorMessage);
        await CompleteOperationAsync(
            connection,
            transaction,
            result,
            "rejected",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static Task CompleteOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemTransactionResult result,
        string status,
        CancellationToken cancellationToken)
    {
        var resultPayload = JsonSerializer.Serialize(result, OperationJsonOptions);
        return connection.ExecuteAsync(new CommandDefinition(
            """
            update item_operations
            set status = @Status,
                result_payload = cast(@ResultPayload as jsonb),
                revision = revision + 1,
                completed_at = now()
            where operation_id = @OperationId;
            """,
            new
            {
                result.OperationId,
                Status = status,
                ResultPayload = resultPayload
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static ItemTransactionResult CreateRejectedResult(
        Guid operationId,
        string operationKind,
        string errorCode,
        string errorMessage)
    {
        return new ItemTransactionResult(
            operationId,
            operationKind,
            false,
            new ItemTransactionError(errorCode, errorMessage),
            [],
            [],
            [],
            [],
            null);
    }

    private static ItemTransactionError? MapPostgresError(PostgresException exception)
    {
        return exception.ConstraintName switch
        {
            "ux_item_instances_container_slot" => new ItemTransactionError(
                ItemTransactionErrorCodes.ItemSlotOccupied,
                "The requested destination slot is occupied."),
            "ux_item_instances_equipment_slot" => new ItemTransactionError(
                ItemTransactionErrorCodes.EquipmentSlotOccupied,
                "The requested equipment slot is occupied."),
            "ux_recovery_deliveries_source" => new ItemTransactionError(
                ItemTransactionErrorCodes.ItemOperationConflict,
                "The Recovery Storage source event was already applied."),
            "ck_item_instances_quantity" => new ItemTransactionError(
                ItemTransactionErrorCodes.ItemQuantityChanged,
                "The requested item quantity is no longer valid."),
            _ => null
        };
    }

    private static object CanonicalizeCommand<TCommand>(TCommand command)
        where TCommand : notnull
    {
        return command switch
        {
            ClaimRecoveryDeliveryCommand claim => claim with
            {
                Items = claim.Items
                    .OrderBy(item => item.ItemInstanceId)
                    .ThenBy(item => item.Revision)
                    .ToArray()
            },
            ChangeSecureContainerTierCommand tier => tier with
            {
                CharacterRevisions = tier.CharacterRevisions
                    .OrderBy(item => item.CharacterId)
                    .ThenBy(item => item.Revision)
                    .ToArray()
            },
            _ => command
        };
    }

    private static CanonicalOperationActor CanonicalizeActor(ItemTransactionActor actor)
    {
        return new CanonicalOperationActor(
            actor.Authority,
            actor.AccountId,
            actor.RequiresOfflineCharacter,
            actor.Simulation?.SimulationSessionId,
            actor.Simulation?.CharacterId,
            actor.Simulation?.WorkerId,
            actor.Simulation?.WorkerRuntimeId,
            actor.Simulation?.ShardId);
    }

    private sealed record CanonicalOperationRequest(
        CanonicalOperationActor Actor,
        object Command);

    private sealed record CanonicalOperationActor(
        ItemTransactionAuthority Authority,
        Guid? AccountId,
        bool RequiresOfflineCharacter,
        Guid? SimulationSessionId,
        Guid? CharacterId,
        string? WorkerId,
        string? WorkerRuntimeId,
        string? ShardId);

    private sealed class OperationRow
    {
        public string OperationKind { get; set; } = string.Empty;

        public string RequestHash { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? ResultPayload { get; set; }
    }

    private sealed class RecoveryDeliveryRow
    {
        public Guid DeliveryId { get; set; }

        public Guid CharacterId { get; set; }

        public Guid RecoveryStorageContainerId { get; set; }

        public long Revision { get; set; }

        public DateTime? ClaimedAt { get; set; }

        public bool IsAvailable { get; set; }

        public bool IsExpired { get; set; }
    }

    private sealed class RecoveryDeliveryItemRow
    {
        public Guid ItemInstanceId { get; set; }

        public int ItemOrder { get; set; }

        public int ContainerSlotIndex { get; set; }
    }

    private sealed record RecoveryDeliveryState(
        Guid DeliveryId,
        Guid CharacterId,
        Guid RecoveryStorageContainerId,
        long Revision,
        DateTime? ClaimedAt,
        bool IsAvailable,
        bool IsExpired,
        IReadOnlyList<RecoveryDeliveryItemRow> Items);

    private sealed class CharacterLockCandidate
    {
        public Guid CharacterId { get; set; }

        public long Revision { get; set; }
    }

    private sealed class SecureEntitlementRow
    {
        public Guid AccountId { get; set; }

        public string TierId { get; set; } = string.Empty;

        public long Revision { get; set; }
    }

    private sealed class SecureTierRow
    {
        public string TierId { get; set; } = string.Empty;

        public int SlotCapacity { get; set; }
    }

    private sealed class SecureOverflowItem
    {
        public Guid CharacterId { get; set; }

        public Guid ItemInstanceId { get; set; }

        public long Revision { get; set; }

        public int ContainerSlotIndex { get; set; }
    }

    private sealed record SecureEntitlementAuditState(
        Guid AccountId,
        string TierId,
        long Revision);
}
