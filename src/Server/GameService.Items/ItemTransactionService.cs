using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.GameService.Items.Transactions.Handlers;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.WorldData.Items;
using Npgsql;

namespace Mmorpg.GameService.Items;

internal sealed partial class ItemTransactionService(
    NpgsqlDataSource dataSource,
    ICharacterLocks characterLocks,
    ICharacterWallet characterWallet,
    ISimulationSessionAuthority simulationSessionAuthority,
    ISimulationWorkerAuthority simulationWorkerAuthority,
    ISimulationPresence simulationPresence,
    ItemOperationsMetrics? operationsMetrics = null,
    ItemOperationsOptions? itemOperationsOptions = null,
    ILogger<ItemTransactionService>? logger = null,
    IHttpContextAccessor? httpContextAccessor = null)
{
    private readonly RecoveryExpiryCommandHandler recoveryExpiry = new();
    private readonly ItemDestructionCommandHandler itemDestruction = new();
    private readonly ItemEquipmentCommandHandler itemEquipment = new();
    private readonly ItemRelocationCommandHandler itemRelocation = new();
    private readonly ItemStackCommandHandler itemStack = new();
    private readonly ItemGrantCommandHandler itemGrant = new();
    private readonly QuestItemCommandHandler questItem = new();
    private readonly ItemTransactionKernel kernel = new(
        dataSource,
        itemOperationsOptions ?? ItemOperationsOptions.CreateDefaults(),
        operationsMetrics,
        new ItemTransactionTelemetry(operationsMetrics, logger, httpContextAccessor),
        new ItemOperationLedger(dataSource),
        new ItemTransactionDependencies(
            characterLocks,
            characterWallet,
            simulationSessionAuthority,
            simulationWorkerAuthority,
            simulationPresence));

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<GrantItemCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Grant,
            request.Command.CharacterId,
            itemGrant,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<RelocateItemCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Relocate,
            request.Command.CharacterId,
            itemRelocation,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<EquipItemCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Equip,
            request.Command.CharacterId,
            itemEquipment,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<UnequipItemCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Unequip,
            request.Command.CharacterId,
            itemEquipment,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<SplitItemStackCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.SplitStack,
            request.Command.CharacterId,
            itemStack,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<MergeItemStacksCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.MergeStacks,
            request.Command.CharacterId,
            itemStack,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<SwapContainerItemsCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.SwapContainerItems,
            request.Command.CharacterId,
            itemRelocation,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ConsumeItemQuantityCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.ConsumeQuantity,
            request.Command.CharacterId,
            itemStack,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<DestroyItemCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Destroy,
            request.Command.CharacterId,
            itemDestruction,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<SwapBagAggregatesCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.SwapBagAggregates,
            request.Command.FirstCharacterId,
            new DelegateItemCommandHandler<SwapBagAggregatesCommand>(ExecuteSwapBagAggregatesAsync),
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<AddRecoveryDeliveryCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.AddRecoveryDelivery,
            request.Command.CharacterId,
            new DelegateItemCommandHandler<AddRecoveryDeliveryCommand>(ExecuteAddRecoveryDeliveryAsync),
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ClaimRecoveryDeliveryCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.ClaimRecoveryDelivery,
            request.Command.CharacterId,
            new DelegateItemCommandHandler<ClaimRecoveryDeliveryCommand>(ExecuteClaimRecoveryDeliveryAsync),
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ChangeSecureContainerTierCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.ChangeSecureContainerTier,
            null,
            new DelegateItemCommandHandler<ChangeSecureContainerTierCommand>(ExecuteChangeSecureContainerTierAsync),
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ApplyItemPolicyCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.ApplyItemPolicy,
            request.Command.CharacterId,
            new DelegateItemCommandHandler<ApplyItemPolicyCommand>(ExecuteApplyItemPolicyAsync),
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<RemoveInsurancePolicyCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.RemoveInsurancePolicy,
            request.Command.CharacterId,
            new DelegateItemCommandHandler<RemoveInsurancePolicyCommand>(ExecuteRemoveInsurancePolicyAsync),
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<AbandonQuestItemsCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.AbandonQuestItems,
            request.Command.CharacterId,
            questItem,
            cancellationToken);
    }

    private static async Task ExecuteApplyItemPolicyAsync(
        ItemTransactionContext context,
        ApplyItemPolicyCommand command,
        CancellationToken cancellationToken)
    {
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
            if (context.Actor.Authority == ItemTransactionAuthority.SimulationWorker)
            {
                context.EnsureInsuranceNpcAccess();
                if (command.Price <= 0
                    || !string.Equals(
                        command.SourceKind.Trim(),
                        ItemPolicySourceKinds.InsuranceService,
                        StringComparison.Ordinal))
                {
                    ItemTransactionContext.Reject(
                        ItemTransactionErrorCodes.ItemPolicyRestricted,
                        "NPC insurance requires a positive server-owned price and insurance lineage.");
                }
            }
            else
            {
                context.EnsureSystemAuthority();
                if (command.Price != 0)
                {
                    ItemTransactionContext.Reject(
                        ItemTransactionErrorCodes.ItemPolicyRestricted,
                        "System insurance fixtures cannot charge character currency.");
                }
            }

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
        else
        {
            context.EnsureSystemAuthority();
            if (command.Price != 0)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.ItemPolicyRestricted,
                    "Protected item grants cannot charge insurance currency.");
            }
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

        if (command.Price > 0)
        {
            var debit = await context.CharacterWallet.DebitAsync(
                context.Scope,
                command.CharacterId,
                command.Price,
                cancellationToken);
            if (!debit.Succeeded)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.CurrencyInsufficient,
                    "The character does not have enough currency for this insurance policy.");
            }

            context.AddMetadataAudit(
                "item_insurance_currency_charged",
                new { command.CharacterId, Currency = debit.Before },
                new { command.CharacterId, Currency = debit.After, command.Price });
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
        if (context.Actor.Authority == ItemTransactionAuthority.SimulationWorker)
        {
            context.EnsureInsuranceNpcAccess();
        }
        else
        {
            context.EnsureSystemAuthority();
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
        BagAggregateGuards.EnsureEquippedBagAggregate(
            firstBag,
            command.FirstCharacterId,
            command.ExpectedFirstBagContentsRevision);
        BagAggregateGuards.EnsureEquippedBagAggregate(
            secondBag,
            command.SecondCharacterId,
            command.ExpectedSecondBagContentsRevision);
        await BagAggregateGuards.EnsureBagAggregateTransferAllowedAsync(context, firstBag, cancellationToken);
        await BagAggregateGuards.EnsureBagAggregateTransferAllowedAsync(context, secondBag, cancellationToken);

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
        RecoveryDeliveries.ValidateRecoverySource(command.SourceKind, command.SourceEventId);
        RecoveryDeliveries.ValidateRecoveryTimeline(command.AvailableAt, command.ExpiresAt);
        var expectations = RecoveryDeliveries.ValidateDistinctItemExpectations(command.Items);
        var states = await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        var state = states[command.CharacterId];
        await context.LockMutationScopeAsync(
            expectations.Keys,
            [state.RecoveryStorageContainerId],
            cancellationToken);
        var recovery = await context.LoadKernelPlacementContainerAsync(
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
        var nextSlotIndex = await RecoveryDeliveries.GetNextRecoverySlotIndexAsync(
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
        var expectedItems = RecoveryDeliveries.ValidateDistinctItemExpectations(command.Items);
        var states = await context.LockCharacterStatesAsync(
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

        await context.LockMutationScopeAsync(
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
                $"""
                select
                    state.character_id as "CharacterId",
                    state.revision as "Revision"
                from character_item_states state
                join {CharactersSql.ActiveCharacters} character on character.id = state.character_id
                where character.account_id = @AccountId
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

        // An account may always return to the free base tier. A larger tier is granted
        // only by a trusted entitlement or purchase source running with system authority.
        if (context.Actor.Authority != ItemTransactionAuthority.System)
        {
            var baseTierId = await context.Connection.ExecuteScalarAsync<string>(
                new CommandDefinition(
                    """
                    select base_secure_container_tier_id
                    from item_catalog_revisions
                    where is_current;
                    """,
                    transaction: context.Transaction,
                    cancellationToken: cancellationToken));
            if (!string.Equals(tier.TierId, baseTierId, StringComparison.Ordinal))
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.AuthorityRequired,
                    "Only an entitlement source can grant a Secure Container tier above the base tier.");
            }
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
            var recoveryContainer = await context.LoadKernelPlacementContainerAsync(
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
                var nextRecoverySlotIndex = await RecoveryDeliveries.GetNextRecoverySlotIndexAsync(
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
