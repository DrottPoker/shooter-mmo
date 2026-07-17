using Dapper;
using ShooterMmo.WorldData.Items;

namespace AuthService.Items;

public sealed partial class ItemTransactionService
{
    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<LootCorpseItemCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.LootCorpseItem,
            request.Command.CharacterId,
            ExecuteLootCorpseItemAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<LootCorpsePartialStackCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.LootCorpsePartialStack,
            request.Command.CharacterId,
            ExecuteLootCorpsePartialStackAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<SwapCorpseBagCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.SwapCorpseBag,
            request.Command.CharacterId,
            ExecuteSwapCorpseBagAsync,
            cancellationToken);
    }

    private static Task ExecuteLootCorpseItemAsync(
        ItemTransactionContext context,
        LootCorpseItemCommand command,
        CancellationToken cancellationToken)
    {
        return ExecuteLootCorpseStackAsync(
            context,
            new CorpseLootStackCommand(
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

    private static Task ExecuteLootCorpsePartialStackAsync(
        ItemTransactionContext context,
        LootCorpsePartialStackCommand command,
        CancellationToken cancellationToken)
    {
        return ExecuteLootCorpseStackAsync(
            context,
            new CorpseLootStackCommand(
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
        CorpseLootStackCommand command,
        CancellationToken cancellationToken)
    {
        ValidateCorpseLootStackCommand(command);
        await context.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var corpse = await LockOpenCorpseAsync(
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

        var source = await context.LoadItemAsync(command.ItemInstanceId, cancellationToken);
        if (source is null
            || source.ContainerId is null
            || !await IsCorpseSectionContainerAsync(
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

        var definition = await context.LoadDefinitionAsync(
            source.DefinitionId,
            cancellationToken);
        var sourcePolicies = await context.LoadPoliciesAsync(
            source.ItemInstanceId,
            cancellationToken);
        var sourceCapabilities = ItemPolicyRules.Evaluate(
            definition.RuntimeDefinition,
            ToPolicyStates(sourcePolicies));
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

        var destination = await context.LoadOwnedContainerAsync(
            command.DestinationContainerId,
            command.CharacterId,
            cancellationToken);
        EnsureCarriedLootDestination(destination);
        if (destination.Revision != command.ExpectedDestinationContainerRevision)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The loot destination changed before the operation committed.");
        }

        LockedItem? target = null;
        IReadOnlyList<LockedItemPolicy>? targetPolicies = null;
        if (command.TargetItemInstanceId is not null)
        {
            target = await context.LoadOwnedItemAsync(
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

            await context.SelectDestinationSlotAsync(
                destination,
                definition,
                command.DestinationSlotIndex,
                false,
                null,
                false,
                cancellationToken,
                target.ItemInstanceId);
            targetPolicies = await context.LoadPoliciesAsync(
                target.ItemInstanceId,
                cancellationToken);
            EnsureCorpseStackMergeAllowed(
                source,
                sourcePolicies,
                sourceCapabilities,
                target,
                targetPolicies,
                definition,
                movedQuantity);
        }
        else
        {
            await context.SelectDestinationSlotAsync(
                destination,
                definition,
                command.DestinationSlotIndex,
                false,
                null,
                false,
                cancellationToken);
        }

        if (target is not null)
        {
            await MergeCorpseQuantityAsync(
                context,
                source,
                target,
                movedQuantity,
                isPartial,
                cancellationToken);
        }
        else if (isPartial)
        {
            await SplitCorpseQuantityAsync(
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
            await MoveCorpseItemAsync(
                context,
                source,
                destination,
                command.DestinationSlotIndex,
                cancellationToken);
        }

        await AdvanceCorpseRevisionAsync(context, corpse, cancellationToken);
        context.TouchContainer(source.ContainerId);
        context.TouchContainer(destination.ContainerId);
        context.TouchCharacter(command.CharacterId);
    }

    private static async Task ExecuteSwapCorpseBagAsync(
        ItemTransactionContext context,
        SwapCorpseBagCommand command,
        CancellationToken cancellationToken)
    {
        ValidateCorpseBagCommand(command);
        await context.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var corpse = await LockOpenCorpseAsync(
            context,
            command.CorpseId,
            cancellationToken);
        await context.LockMutationScopeAsync(
            [command.CorpseBagItemInstanceId, command.PlayerBagItemInstanceId],
            [command.CorpseBagContentsContainerId, command.PlayerBagContentsContainerId],
            cancellationToken);

        var corpseBag = await context.LoadItemAsync(
            command.CorpseBagItemInstanceId,
            cancellationToken);
        var playerBag = await context.LoadItemAsync(
            command.PlayerBagItemInstanceId,
            cancellationToken);
        var sections = await LoadCorpseBagSectionAsync(
            context,
            command.CorpseId,
            cancellationToken);
        if (corpseBag is null
            || !corpseBag.IsBag
            || corpseBag.Revision != command.ExpectedCorpseBagRevision
            || corpseBag.ContainerId != sections.EquipmentContainerId
            || corpseBag.ContainerSlotIndex is null
            || corpseBag.BagContentsContainerId != command.CorpseBagContentsContainerId
            || corpseBag.BagContentsContainerId != sections.BagContainerId
            || corpseBag.BagContentsRevision != command.ExpectedCorpseBagContentsRevision
            || !string.Equals(corpseBag.BagContentsLifecycle, "active", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "The corpse Bag aggregate changed before the swap committed.");
        }

        if (playerBag is null
            || !playerBag.IsBag
            || playerBag.OwningCharacterId != command.CharacterId
            || playerBag.Revision != command.ExpectedPlayerBagRevision
            || playerBag.EquippedCharacterId != command.CharacterId
            || !string.Equals(
                playerBag.EquipmentSlotId,
                ItemEquipmentSlotIds.Bag,
                StringComparison.Ordinal)
            || playerBag.BagContentsContainerId != command.PlayerBagContentsContainerId
            || playerBag.BagContentsRevision != command.ExpectedPlayerBagContentsRevision
            || !string.Equals(playerBag.BagContentsLifecycle, "active", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "The equipped player Bag aggregate changed before the swap committed.");
        }

        await EnsureBagAggregateTransferAllowedAsync(context, corpseBag, cancellationToken);
        await EnsureBagAggregateTransferAllowedAsync(context, playerBag, cancellationToken);

        var corpseBagBefore = await context.CaptureItemStateJsonAsync(
            corpseBag.ItemInstanceId,
            cancellationToken);
        var playerBagBefore = await context.CaptureItemStateJsonAsync(
            playerBag.ItemInstanceId,
            cancellationToken);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            set constraints ux_item_instances_equipment_slot deferred;
            set constraints ux_item_instances_container_slot deferred;

            update item_instances
            set container_id = case id
                    when @CorpseBagItemInstanceId then null
                    when @PlayerBagItemInstanceId then @CorpseEquipmentContainerId
                end,
                container_slot_index = case id
                    when @CorpseBagItemInstanceId then null
                    when @PlayerBagItemInstanceId then @CorpseEquipmentSlotIndex
                end,
                equipped_character_id = case id
                    when @CorpseBagItemInstanceId then @CharacterId
                    when @PlayerBagItemInstanceId then null
                end,
                equipment_slot_id = case id
                    when @CorpseBagItemInstanceId then 'bag'
                    when @PlayerBagItemInstanceId then null
                end,
                revision = revision + 1,
                updated_at = now()
            where id in (@CorpseBagItemInstanceId, @PlayerBagItemInstanceId);

            update corpse_sections
            set container_id = @PlayerBagContentsContainerId
            where corpse_id = @CorpseId
              and section_kind = 'bag';
            """,
            new
            {
                command.CorpseBagItemInstanceId,
                command.PlayerBagItemInstanceId,
                CorpseEquipmentContainerId = sections.EquipmentContainerId,
                CorpseEquipmentSlotIndex = corpseBag.ContainerSlotIndex.Value,
                command.CharacterId,
                command.PlayerBagContentsContainerId,
                command.CorpseId
            },
            context.Transaction,
            cancellationToken: cancellationToken));

        var corpseBagAfter = await context.CaptureItemStateJsonAsync(
            corpseBag.ItemInstanceId,
            cancellationToken);
        var playerBagAfter = await context.CaptureItemStateJsonAsync(
            playerBag.ItemInstanceId,
            cancellationToken);
        context.AddItemAudit(
            "corpse_bag_swapped_to_character",
            corpseBag.ItemInstanceId,
            corpseBagBefore,
            corpseBagAfter);
        context.AddItemAudit(
            "character_bag_swapped_to_corpse",
            playerBag.ItemInstanceId,
            playerBagBefore,
            playerBagAfter);
        await AdvanceCorpseRevisionAsync(context, corpse, cancellationToken);
        context.TouchContainer(sections.EquipmentContainerId);
        context.TouchContainer(corpseBag.BagContentsContainerId);
        context.TouchContainer(playerBag.BagContentsContainerId);
        context.TouchCharacter(command.CharacterId);
        context.IncludeResultItem(corpseBag.ItemInstanceId);
        context.IncludeResultItem(playerBag.ItemInstanceId);
    }

    private static async Task MoveCorpseItemAsync(
        ItemTransactionContext context,
        LockedItem source,
        LockedContainer destination,
        int destinationSlotIndex,
        CancellationToken cancellationToken)
    {
        var before = await context.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
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
                DestinationSlotIndex = destinationSlotIndex,
                source.ItemInstanceId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        var after = await context.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        context.AddItemAudit(
            "corpse_item_looted",
            source.ItemInstanceId,
            before,
            after);
        context.IncludeResultItem(source.ItemInstanceId);
    }

    private static async Task SplitCorpseQuantityAsync(
        ItemTransactionContext context,
        LockedItem source,
        IReadOnlyList<LockedItemPolicy> policies,
        int quantity,
        LockedContainer destination,
        int destinationSlotIndex,
        CancellationToken cancellationToken)
    {
        var sourceBefore = await context.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var createdItemId = Guid.NewGuid();
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
                @CreatedItemId,
                @DefinitionId,
                @Quantity,
                @DestinationContainerId,
                @DestinationSlotIndex);
            """,
            new
            {
                Quantity = quantity,
                SourceItemInstanceId = source.ItemInstanceId,
                CreatedItemId = createdItemId,
                source.DefinitionId,
                DestinationContainerId = destination.ContainerId,
                DestinationSlotIndex = destinationSlotIndex
            },
            context.Transaction,
            cancellationToken: cancellationToken));

        foreach (var policy in policies.Where(policy => string.Equals(
                     policy.Status,
                     ItemPolicyRules.ActiveStatus,
                     StringComparison.Ordinal)))
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
                    ItemInstanceId = createdItemId,
                    policy.PolicyKind,
                    policy.SourceKind,
                    policy.SourceId
                },
                context.Transaction,
                cancellationToken: cancellationToken));
        }

        var sourceAfter = await context.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var createdAfter = await context.CaptureItemStateJsonAsync(
            createdItemId,
            cancellationToken);
        context.AddItemAudit(
            "corpse_stack_loot_source",
            source.ItemInstanceId,
            sourceBefore,
            sourceAfter);
        context.AddItemAudit(
            "corpse_stack_loot_created",
            createdItemId,
            null,
            createdAfter);
        context.IncludeResultItem(source.ItemInstanceId);
        context.IncludeResultItem(createdItemId);
    }

    private static async Task MergeCorpseQuantityAsync(
        ItemTransactionContext context,
        LockedItem source,
        LockedItem target,
        int quantity,
        bool isPartial,
        CancellationToken cancellationToken)
    {
        var sourceBefore = await context.CaptureItemStateJsonAsync(
            source.ItemInstanceId,
            cancellationToken);
        var targetBefore = await context.CaptureItemStateJsonAsync(
            target.ItemInstanceId,
            cancellationToken);
        if (isPartial)
        {
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                update item_instances
                set quantity = quantity - @Quantity,
                    revision = revision + 1,
                    updated_at = now()
                where id = @SourceItemInstanceId;

                update item_instances
                set quantity = quantity + @Quantity,
                    revision = revision + 1,
                    updated_at = now()
                where id = @TargetItemInstanceId;
                """,
                new
                {
                    Quantity = quantity,
                    SourceItemInstanceId = source.ItemInstanceId,
                    TargetItemInstanceId = target.ItemInstanceId
                },
                context.Transaction,
                cancellationToken: cancellationToken));
        }
        else
        {
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                update item_instances
                set quantity = quantity + @Quantity,
                    revision = revision + 1,
                    updated_at = now()
                where id = @TargetItemInstanceId;

                delete from item_instances
                where id = @SourceItemInstanceId;
                """,
                new
                {
                    Quantity = quantity,
                    SourceItemInstanceId = source.ItemInstanceId,
                    TargetItemInstanceId = target.ItemInstanceId
                },
                context.Transaction,
                cancellationToken: cancellationToken));
        }

        var sourceAfter = isPartial
            ? await context.CaptureItemStateJsonAsync(source.ItemInstanceId, cancellationToken)
            : null;
        var targetAfter = await context.CaptureItemStateJsonAsync(
            target.ItemInstanceId,
            cancellationToken);
        context.AddItemAudit(
            isPartial ? "corpse_stack_loot_source" : "corpse_stack_loot_source_removed",
            source.ItemInstanceId,
            sourceBefore,
            sourceAfter);
        context.AddItemAudit(
            "corpse_stack_loot_merged",
            target.ItemInstanceId,
            targetBefore,
            targetAfter);
        if (isPartial)
        {
            context.IncludeResultItem(source.ItemInstanceId);
        }

        context.IncludeResultItem(target.ItemInstanceId);
    }

    private static void EnsureCorpseStackMergeAllowed(
        LockedItem source,
        IReadOnlyList<LockedItemPolicy> sourcePolicies,
        ItemPolicyCapabilities sourceCapabilities,
        LockedItem target,
        IReadOnlyList<LockedItemPolicy> targetPolicies,
        ResolvedItemDefinition definition,
        int movedQuantity)
    {
        var targetCapabilities = ItemPolicyRules.Evaluate(
            definition.RuntimeDefinition,
            ToPolicyStates(targetPolicies));
        var sourceState = new ItemStackState(
            source.DefinitionId,
            movedQuantity,
            ItemTransactionContext.CalculatePolicyFingerprint(
                sourcePolicies.Where(policy => string.Equals(
                    policy.Status,
                    ItemPolicyRules.ActiveStatus,
                    StringComparison.Ordinal))),
            EmptyStackStateFingerprint);
        var targetState = new ItemStackState(
            target.DefinitionId,
            target.Quantity,
            ItemTransactionContext.CalculatePolicyFingerprint(
                targetPolicies.Where(policy => string.Equals(
                    policy.Status,
                    ItemPolicyRules.ActiveStatus,
                    StringComparison.Ordinal))),
            EmptyStackStateFingerprint);
        if (!sourceCapabilities.CanStack
            || !targetCapabilities.CanStack
            || !ItemStackRules.CanMerge(definition.RuntimeDefinition, sourceState, targetState))
        {
            var compatible = ItemStackRules.AreCompatible(sourceState, targetState);
            ItemTransactionContext.Reject(
                compatible
                    ? ItemTransactionErrorCodes.ItemStackLimitExceeded
                    : ItemTransactionErrorCodes.ItemStackIncompatible,
                compatible
                    ? "The looted quantity would exceed the target stack limit."
                    : "The corpse item and target stack have incompatible state.");
        }
    }

    private static void EnsureCarriedLootDestination(LockedContainer destination)
    {
        if (!string.Equals(
                destination.ContainerType,
                "permanent_inventory",
                StringComparison.Ordinal)
            && !string.Equals(
                destination.ContainerType,
                "secure_container",
                StringComparison.Ordinal)
            && !string.Equals(
                destination.ContainerType,
                "bag_contents",
                StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemSlotIncompatible,
                "Corpse loot must enter carried character storage.");
        }
    }

    private static async Task<LockedCorpseRow> LockOpenCorpseAsync(
        ItemTransactionContext context,
        Guid corpseId,
        CancellationToken cancellationToken)
    {
        var corpse = await context.Connection.QuerySingleOrDefaultAsync<LockedCorpseRow>(
            new CommandDefinition(
                """
                select
                    id as "CorpseId",
                    shard_id as "ShardId",
                    revision as "Revision",
                    expires_at as "ExpiresAt",
                    closed_at as "ClosedAt",
                    close_reason as "CloseReason",
                    expires_at <= now() as "IsExpired"
                from corpses
                where id = @CorpseId
                for update;
                """,
                new { CorpseId = corpseId },
                context.Transaction,
                cancellationToken: cancellationToken));
        if (corpse is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.CorpseNotFound,
                "The corpse was not found.");
        }

        if (corpse.IsExpired
            || string.Equals(corpse.CloseReason, "expired", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.CorpseExpired,
                "The corpse has reached its absolute expiry time.");
        }

        if (corpse.ClosedAt is not null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.CorpseInvalidated,
                "The corpse is no longer available for interaction.");
        }

        if (context.Actor.Authority == ItemTransactionAuthority.SimulationWorker
            && !string.Equals(
                context.Actor.Simulation!.ShardId,
                corpse.ShardId,
                StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The corpse belongs to another Shard.");
        }

        return corpse;
    }

    private static Task<bool> IsCorpseSectionContainerAsync(
        ItemTransactionContext context,
        Guid corpseId,
        Guid containerId,
        CancellationToken cancellationToken)
    {
        return context.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists (
                select 1
                from corpse_sections
                where corpse_id = @CorpseId
                  and container_id = @ContainerId);
            """,
            new { CorpseId = corpseId, ContainerId = containerId },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<CorpseBagSectionRow> LoadCorpseBagSectionAsync(
        ItemTransactionContext context,
        Guid corpseId,
        CancellationToken cancellationToken)
    {
        var sections = await context.Connection.QuerySingleAsync<CorpseBagSectionRow>(
            new CommandDefinition(
                """
                select
                    (
                        select container_id
                        from corpse_sections
                        where corpse_id = @CorpseId
                          and section_kind = 'equipment') as "EquipmentContainerId",
                    (
                        select container_id
                        from corpse_sections
                        where corpse_id = @CorpseId
                          and section_kind = 'bag') as "BagContainerId";
                """,
                new { CorpseId = corpseId },
                context.Transaction,
                cancellationToken: cancellationToken));
        if (sections.EquipmentContainerId == Guid.Empty
            || sections.BagContainerId == Guid.Empty)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "The corpse Bag sections are unavailable.");
        }

        return sections;
    }

    private static async Task AdvanceCorpseRevisionAsync(
        ItemTransactionContext context,
        LockedCorpseRow corpse,
        CancellationToken cancellationToken)
    {
        var nextRevision = await context.Connection.QuerySingleAsync<long>(
            new CommandDefinition(
                """
                update corpses
                set revision = revision + 1
                where id = @CorpseId
                  and revision = @ExpectedRevision
                returning revision;
                """,
                new
                {
                    corpse.CorpseId,
                    ExpectedRevision = corpse.Revision
                },
                context.Transaction,
                cancellationToken: cancellationToken));
        context.AddMetadataAudit(
            "corpse_loot_revision_advanced",
            new { corpse.CorpseId, corpse.Revision },
            new { corpse.CorpseId, Revision = nextRevision });
    }

    private static void ValidateCorpseLootStackCommand(CorpseLootStackCommand command)
    {
        var hasTarget = command.TargetItemInstanceId is not null;
        if (command.CharacterId == Guid.Empty
            || command.CorpseId == Guid.Empty
            || command.ItemInstanceId == Guid.Empty
            || command.ExpectedItemRevision < 0
            || command.DestinationContainerId == Guid.Empty
            || command.ExpectedDestinationContainerRevision < 0
            || command.DestinationSlotIndex < 0
            || hasTarget != (command.ExpectedTargetItemRevision is not null)
            || command.TargetItemInstanceId == Guid.Empty
            || command.ExpectedTargetItemRevision < 0)
        {
            ItemTransactionContext.Reject(
                ItemApiErrorCodes.SimulationOperationInvalid,
                "The corpse loot command is incomplete or invalid.");
        }
    }

    private static void ValidateCorpseBagCommand(SwapCorpseBagCommand command)
    {
        if (command.CharacterId == Guid.Empty
            || command.CorpseId == Guid.Empty
            || command.CorpseBagItemInstanceId == Guid.Empty
            || command.ExpectedCorpseBagRevision < 0
            || command.CorpseBagContentsContainerId == Guid.Empty
            || command.ExpectedCorpseBagContentsRevision < 0
            || command.PlayerBagItemInstanceId == Guid.Empty
            || command.ExpectedPlayerBagRevision < 0
            || command.PlayerBagContentsContainerId == Guid.Empty
            || command.ExpectedPlayerBagContentsRevision < 0
            || command.CorpseBagItemInstanceId == command.PlayerBagItemInstanceId
            || command.CorpseBagContentsContainerId == command.PlayerBagContentsContainerId)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "The corpse Bag swap command is incomplete or invalid.");
        }
    }

    private sealed record CorpseLootStackCommand(
        Guid CharacterId,
        Guid CorpseId,
        Guid ItemInstanceId,
        long ExpectedItemRevision,
        int? Quantity,
        Guid DestinationContainerId,
        long ExpectedDestinationContainerRevision,
        int DestinationSlotIndex,
        Guid? TargetItemInstanceId,
        long? ExpectedTargetItemRevision);

    private sealed class LockedCorpseRow
    {
        public Guid CorpseId { get; set; }

        public string ShardId { get; set; } = string.Empty;

        public long Revision { get; set; }

        public DateTime ExpiresAt { get; set; }

        public DateTime? ClosedAt { get; set; }

        public string? CloseReason { get; set; }

        public bool IsExpired { get; set; }
    }

    private sealed class CorpseBagSectionRow
    {
        public Guid EquipmentContainerId { get; set; }

        public Guid BagContainerId { get; set; }
    }
}
