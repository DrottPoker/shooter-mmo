using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using ShooterMmo.WorldData.Items;

namespace AuthService.Items;

public sealed partial class ItemTransactionService
{
    private const string PlayerCorpsePresentationKey = "corpse.generic_loot_crate";
    private const string DeathProtectionRecoverySource = "death_protection";
    private const string ProtectedBagRecoverySource = "protected_bag";
    private const string InsuranceRecoverySource = "insurance";

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ProcessPlayerDeathCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.ProcessPlayerDeath,
            request.Command.CharacterId,
            ExecuteProcessPlayerDeathAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ExpireCorpseCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.ExpireCorpse,
            null,
            ExecuteExpireCorpseAsync,
            cancellationToken);
    }

    private static async Task ExecuteProcessPlayerDeathAsync(
        ItemTransactionContext context,
        ProcessPlayerDeathCommand command,
        CancellationToken cancellationToken)
    {
        ValidatePlayerDeathCommand(command);
        EnsureDeathAuthority(context);
        await LockDeathEventKeyAsync(context, command.DeathEventId, cancellationToken);

        var characterState = await context.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var requestHash = CalculateDeathEventRequestHash(command);
        var existingEvent = await LoadDeathEventAsync(
            context,
            command.DeathEventId,
            cancellationToken);
        if (existingEvent is not null)
        {
            if (!string.Equals(existingEvent.RequestHash, requestHash, StringComparison.Ordinal))
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.DeathEventConflict,
                    "The death event id was already used with different authoritative death data.");
            }

            context.AddMetadataAudit(
                "death_event_replayed",
                new
                {
                    command.DeathEventId,
                    existingEvent.CorpseId,
                    existingEvent.OriginalOperationId
                },
                new
                {
                    command.DeathEventId,
                    existingEvent.CorpseId,
                    existingEvent.OriginalOperationId
                });
            return;
        }

        if (context.Actor.Authority != ItemTransactionAuthority.System
            && command.ExpectedCharacterRevision is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "Simulation-authorized death processing requires an expected character revision.");
        }

        if (command.ExpectedCharacterRevision is not null
            && characterState.Revision != command.ExpectedCharacterRevision.Value)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The character item state changed before the death event committed.");
        }

        context.AllowInvoluntaryDeathHardCapOverflow();

        var character = await context.Connection.QuerySingleOrDefaultAsync<CharacterDeathSourceRow>(
            new CommandDefinition(
                """
                select
                    character.name as "DisplayName",
                    exists (
                        select 1
                        from shards shard
                        where shard.id = @ShardId) as "ShardExists"
                from characters character
                where character.id = @CharacterId
                  and character.deleted_at is null;
                """,
                new { command.CharacterId, command.ShardId },
                context.Transaction,
                cancellationToken: cancellationToken));
        if (character is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemNotFound,
                "The death event character was not found.");
        }

        if (!character.ShardExists)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.SimulationSessionInvalid,
                "The death event Shard was not found.");
        }

        var corpseId = Guid.NewGuid();
        var expiryOperationId = Guid.NewGuid();
        var corpseTimes = await context.Connection.QuerySingleAsync<CreatedCorpseTimes>(
            new CommandDefinition(
                """
                insert into corpses (
                    id,
                    source_type,
                    source_character_id,
                    source_display_name,
                    shard_id,
                    position_x,
                    position_y,
                    position_z,
                    rotation_x,
                    rotation_y,
                    rotation_z,
                    rotation_w,
                    persistence_mode,
                    presentation_key,
                    expires_at,
                    expiry_operation_id)
                values (
                    @CorpseId,
                    'player',
                    @CharacterId,
                    @SourceDisplayName,
                    @ShardId,
                    @PositionX,
                    @PositionY,
                    @PositionZ,
                    @RotationX,
                    @RotationY,
                    @RotationZ,
                    @RotationW,
                    'durable',
                    @PresentationKey,
                    now() + interval '5 minutes',
                    @ExpiryOperationId)
                returning
                    created_at as "CreatedAt",
                    expires_at as "ExpiresAt";
                """,
                new
                {
                    CorpseId = corpseId,
                    command.CharacterId,
                    SourceDisplayName = character.DisplayName,
                    command.ShardId,
                    command.PositionX,
                    command.PositionY,
                    command.PositionZ,
                    command.RotationX,
                    command.RotationY,
                    command.RotationZ,
                    command.RotationW,
                    command.PresentationKey,
                    ExpiryOperationId = expiryOperationId
                },
                context.Transaction,
                cancellationToken: cancellationToken));

        var sourceRows = (await context.Connection.QueryAsync<DeathItemSourceRow>(
            new CommandDefinition(
                """
                select
                    item.id as "ItemInstanceId",
                    item.container_id as "ContainerId",
                    item.container_slot_index as "ContainerSlotIndex",
                    item.equipment_slot_id as "EquipmentSlotId",
                    equipment.sort_order as "EquipmentSortOrder",
                    bag_container.id as "BagContentsContainerId",
                    case
                        when item.equipped_character_id = @CharacterId then 'equipment'
                        when item.container_id = @PermanentInventoryContainerId then 'permanent_inventory'
                        else 'bag_contents'
                    end as "SourceKind"
                from item_instances item
                left join item_containers source_container on source_container.id = item.container_id
                left join item_instances source_bag
                  on source_bag.id = source_container.bound_bag_item_instance_id
                left join item_containers bag_container
                  on bag_container.bound_bag_item_instance_id = item.id
                 and bag_container.container_type = 'bag_contents'
                left join equipment_slots equipment on equipment.id = item.equipment_slot_id
                where item.container_id = @PermanentInventoryContainerId
                   or item.equipped_character_id = @CharacterId
                   or (
                        source_container.container_type = 'bag_contents'
                        and source_container.lifecycle = 'active'
                        and source_bag.equipped_character_id = @CharacterId
                        and source_bag.equipment_slot_id = 'bag')
                order by item.id;
                """,
                new
                {
                    command.CharacterId,
                    characterState.PermanentInventoryContainerId
                },
                context.Transaction,
                cancellationToken: cancellationToken))).ToArray();
        var sourceContainerIds = sourceRows
            .Select(row => row.ContainerId)
            .Where(containerId => containerId is not null)
            .Select(containerId => containerId!.Value)
            .Append(characterState.PermanentInventoryContainerId)
            .Append(characterState.RecoveryStorageContainerId)
            .Distinct()
            .Order()
            .ToArray();
        await context.LockMutationScopeAsync(
            sourceRows.Select(row => row.ItemInstanceId),
            sourceContainerIds,
            cancellationToken);

        var partitionItems = new List<DeathPartitionItem>(sourceRows.Length);
        foreach (var source in sourceRows)
        {
            var item = await context.LoadItemAsync(source.ItemInstanceId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Death item '{source.ItemInstanceId}' disappeared after locking.");
            var definition = await context.LoadDefinitionAsync(
                item.DefinitionId,
                cancellationToken);
            var policies = await context.LoadPoliciesAsync(
                item.ItemInstanceId,
                cancellationToken);
            var capabilities = ItemPolicyRules.Evaluate(
                definition.RuntimeDefinition,
                policies.Select(policy => new ItemPolicyState(
                    policy.PolicyKind,
                    policy.Status,
                    policy.SourceKind,
                    policy.SourceId)));
            partitionItems.Add(new DeathPartitionItem(
                source,
                item,
                definition,
                capabilities.DeathDisposition,
                await context.CaptureItemStateJsonAsync(item.ItemInstanceId, cancellationToken)));
        }

        var equippedBag = partitionItems.SingleOrDefault(item => item.IsEquippedBag);
        var generalContainerId = Guid.NewGuid();
        var equipmentContainerId = Guid.NewGuid();
        var useLiveBagAggregate = equippedBag is not null
            && equippedBag.Disposition == ItemDeathDisposition.Lootable;
        var bagContainerId = useLiveBagAggregate
            ? equippedBag!.Item.BagContentsContainerId
                ?? throw new InvalidOperationException("The equipped Bag has no content container.")
            : Guid.NewGuid();

        await CreateCorpseSectionContainersAsync(
            context,
            corpseId,
            characterState.PermanentInventoryContainerId,
            generalContainerId,
            equipmentContainerId,
            bagContainerId,
            useLiveBagAggregate,
            equippedBag,
            cancellationToken);

        await CreateCorpseSnapshotsAsync(
            context,
            corpseId,
            characterState,
            partitionItems,
            equippedBag,
            cancellationToken);

        foreach (var item in partitionItems
                     .Where(item => item.Disposition == ItemDeathDisposition.Lootable)
                     .OrderBy(item => item.Item.ItemInstanceId))
        {
            await MoveLootableItemToCorpseAsync(
                context,
                item,
                generalContainerId,
                equipmentContainerId,
                bagContainerId,
                useLiveBagAggregate,
                cancellationToken);
        }

        await AddDeathRecoveryDeliveriesAsync(
            context,
            command.CharacterId,
            characterState.RecoveryStorageContainerId,
            command.DeathEventId,
            [
                new DeathRecoveryGroup(
                    DeathProtectionRecoverySource,
                    false,
                    partitionItems.Where(item =>
                            item.Disposition == ItemDeathDisposition.ProtectedRecovery
                            && !item.Definition.IsBag)
                        .ToArray()),
                new DeathRecoveryGroup(
                    ProtectedBagRecoverySource,
                    false,
                    partitionItems.Where(item =>
                            item.Disposition == ItemDeathDisposition.ProtectedRecovery
                            && item.Definition.IsBag)
                        .ToArray()),
                new DeathRecoveryGroup(
                    InsuranceRecoverySource,
                    true,
                    partitionItems.Where(item =>
                            item.Disposition == ItemDeathDisposition.InsuredRecovery)
                        .ToArray())
            ],
            cancellationToken);

        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update corpses
            set revision = revision + 1
            where id = @CorpseId;

            insert into death_events (
                id,
                character_id,
                corpse_id,
                operation_id,
                request_hash,
                result_payload)
            values (
                @DeathEventId,
                @CharacterId,
                @CorpseId,
                @OperationId,
                @RequestHash,
                cast(@ResultPayload as jsonb));
            """,
            new
            {
                DeathEventId = command.DeathEventId,
                command.CharacterId,
                CorpseId = corpseId,
                context.OperationId,
                RequestHash = requestHash,
                ResultPayload = JsonSerializer.Serialize(
                    new
                    {
                        corpseId,
                        expiryOperationId,
                        corpseTimes.CreatedAt,
                        corpseTimes.ExpiresAt
                    },
                    OperationJsonOptions)
            },
            context.Transaction,
            cancellationToken: cancellationToken));

        context.TouchContainer(characterState.PermanentInventoryContainerId);
        context.TouchContainer(generalContainerId);
        context.TouchContainer(equipmentContainerId);
        context.TouchContainer(bagContainerId);
        context.TouchCharacter(command.CharacterId);
        context.AddMetadataAudit(
            "player_death_partitioned",
            new
            {
                command.CharacterId,
                PreviousCharacterRevision = characterState.Revision,
                ItemCount = partitionItems.Count
            },
            new
            {
                command.DeathEventId,
                CorpseId = corpseId,
                ExpiresAt = corpseTimes.ExpiresAt,
                LootableItemCount = partitionItems.Count(item =>
                    item.Disposition == ItemDeathDisposition.Lootable),
                ProtectedItemCount = partitionItems.Count(item =>
                    item.Disposition == ItemDeathDisposition.ProtectedRecovery),
                InsuredItemCount = partitionItems.Count(item =>
                    item.Disposition == ItemDeathDisposition.InsuredRecovery)
            });
    }

    private static async Task ExecuteExpireCorpseAsync(
        ItemTransactionContext context,
        ExpireCorpseCommand command,
        CancellationToken cancellationToken)
    {
        context.EnsureSystemAuthority();
        if (command.CorpseId == Guid.Empty)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.CorpseNotFound,
                "A corpse id is required.");
        }

        var corpse = await context.Connection.QuerySingleOrDefaultAsync<ExpiringCorpseRow>(
            new CommandDefinition(
                """
                select
                    id as "CorpseId",
                    expires_at as "ExpiresAt",
                    closed_at as "ClosedAt",
                    close_reason as "CloseReason",
                    expiry_operation_id as "ExpiryOperationId"
                from corpses
                where id = @CorpseId
                for update;
                """,
                new { command.CorpseId },
                context.Transaction,
                cancellationToken: cancellationToken));
        if (corpse is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.CorpseNotFound,
                "The corpse was not found.");
        }

        if (corpse.ExpiryOperationId != context.OperationId)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.CorpseStateChanged,
                "Corpse expiry must use the durable expiry operation id.");
        }

        if (corpse.ClosedAt is not null)
        {
            if (!string.Equals(corpse.CloseReason, "expired", StringComparison.Ordinal))
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.CorpseStateChanged,
                    "The corpse was closed by another lifecycle operation.");
            }

            context.AddMetadataAudit(
                "corpse_expiry_replayed",
                new { command.CorpseId, corpse.ClosedAt },
                new { command.CorpseId, corpse.ClosedAt });
            return;
        }

        var databaseTime = await context.Connection.QuerySingleAsync<DateTime>(
            new CommandDefinition(
                "select now();",
                transaction: context.Transaction,
                cancellationToken: cancellationToken));
        if (corpse.ExpiresAt > databaseTime)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.CorpseNotExpired,
                "The corpse has not reached its absolute expiry time.");
        }

        var sectionContainerIds = (await context.Connection.QueryAsync<Guid>(
            new CommandDefinition(
                """
                select container_id
                from corpse_sections
                where corpse_id = @CorpseId
                order by container_id;
                """,
                new { command.CorpseId },
                context.Transaction,
                cancellationToken: cancellationToken))).ToArray();
        var directItemIds = sectionContainerIds.Length == 0
            ? []
            : (await context.Connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select id
                from item_instances
                where container_id = any(@ContainerIds)
                order by id;
                """,
                new { ContainerIds = sectionContainerIds },
                context.Transaction,
                cancellationToken: cancellationToken))).ToArray();
        await context.LockMutationScopeAsync(
            directItemIds,
            sectionContainerIds,
            cancellationToken);

        var expiringItems = directItemIds.Length == 0
            ? []
            : (await context.Connection.QueryAsync<ExpiringCorpseItemRow>(new CommandDefinition(
                """
                select distinct
                    item.id as "ItemInstanceId",
                    item.definition_id as "DefinitionId",
                    item.quantity as "Quantity",
                    (bag.definition_id is not null) as "IsBag"
                from item_instances item
                left join bag_definitions bag on bag.definition_id = item.definition_id
                where item.container_id = any(@ContainerIds)
                   or item.container_id in (
                        select bag_container.id
                        from item_instances root
                        join item_containers bag_container
                          on bag_container.bound_bag_item_instance_id = root.id
                         and bag_container.container_type = 'bag_contents'
                        where root.id = any(@DirectItemIds))
                order by item.id;
                """,
                new
                {
                    ContainerIds = sectionContainerIds,
                    DirectItemIds = directItemIds
                },
                context.Transaction,
                cancellationToken: cancellationToken))).ToArray();

        foreach (var item in expiringItems)
        {
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
                    'corpse_expired');
                """,
                new
                {
                    item.ItemInstanceId,
                    item.DefinitionId,
                    OperationId = context.OperationId,
                    item.Quantity
                },
                context.Transaction,
                cancellationToken: cancellationToken));
            context.AddItemAudit(
                "corpse_item_expired",
                item.ItemInstanceId,
                beforeState,
                null);
        }

        var ordinaryItemIds = expiringItems
            .Where(item => !item.IsBag)
            .Select(item => item.ItemInstanceId)
            .ToArray();
        var bagItemIds = expiringItems
            .Where(item => item.IsBag)
            .Select(item => item.ItemInstanceId)
            .ToArray();
        await DeleteItemsAsync(context, ordinaryItemIds, cancellationToken);
        await DeleteItemsAsync(context, bagItemIds, cancellationToken);

        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update item_containers
            set lifecycle = 'closed',
                revision = revision + 1,
                updated_at = now()
            where id in (
                select container_id
                from corpse_sections
                where corpse_id = @CorpseId);

            update corpses
            set revision = revision + 1,
                closed_at = now(),
                close_reason = 'expired'
            where id = @CorpseId
              and closed_at is null;
            """,
            new { command.CorpseId },
            context.Transaction,
            cancellationToken: cancellationToken));
        context.AddMetadataAudit(
            "corpse_expired",
            new
            {
                command.CorpseId,
                corpse.ExpiresAt,
                RemainingItemCount = expiringItems.Length
            },
            new
            {
                command.CorpseId,
                ClosedAt = databaseTime,
                DestroyedItemCount = expiringItems.Length
            });
    }

    private static async Task CreateCorpseSectionContainersAsync(
        ItemTransactionContext context,
        Guid corpseId,
        Guid sourceInventoryContainerId,
        Guid generalContainerId,
        Guid equipmentContainerId,
        Guid bagContainerId,
        bool useLiveBagAggregate,
        DeathPartitionItem? equippedBag,
        CancellationToken cancellationToken)
    {
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into item_containers (
                id,
                container_type,
                slot_capacity,
                lifecycle)
            select
                @GeneralContainerId,
                'corpse_inventory',
                source.slot_capacity,
                'active'
            from item_containers source
            where source.id = @SourceInventoryContainerId;

            insert into item_container_slots (container_id, slot_index, slot_kind)
            select @GeneralContainerId, slot_index, slot_kind
            from item_container_slots
            where container_id = @SourceInventoryContainerId
            order by slot_index;

            insert into item_container_slot_tags (container_id, slot_index, tag_id)
            select @GeneralContainerId, slot_index, tag_id
            from item_container_slot_tags
            where container_id = @SourceInventoryContainerId
            order by slot_index, tag_id;

            insert into item_containers (
                id,
                container_type,
                slot_capacity,
                lifecycle)
            select
                @EquipmentContainerId,
                'corpse_equipment',
                max(sort_order) + 1,
                'active'
            from equipment_slots;

            insert into item_container_slots (container_id, slot_index, slot_kind)
            select @EquipmentContainerId, sort_order, 'general'
            from equipment_slots
            order by sort_order;
            """,
            new
            {
                GeneralContainerId = generalContainerId,
                SourceInventoryContainerId = sourceInventoryContainerId,
                EquipmentContainerId = equipmentContainerId
            },
            context.Transaction,
            cancellationToken: cancellationToken));

        if (!useLiveBagAggregate)
        {
            if (equippedBag?.Item.BagContentsContainerId is Guid sourceBagContainerId)
            {
                await context.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into item_containers (
                        id,
                        container_type,
                        slot_capacity,
                        lifecycle)
                    select
                        @BagContainerId,
                        'corpse_bag_contents',
                        slot_capacity,
                        'active'
                    from item_containers
                    where id = @SourceBagContainerId;

                    insert into item_container_slots (container_id, slot_index, slot_kind)
                    select @BagContainerId, slot_index, slot_kind
                    from item_container_slots
                    where container_id = @SourceBagContainerId
                    order by slot_index;

                    insert into item_container_slot_tags (container_id, slot_index, tag_id)
                    select @BagContainerId, slot_index, tag_id
                    from item_container_slot_tags
                    where container_id = @SourceBagContainerId
                    order by slot_index, tag_id;
                    """,
                    new { BagContainerId = bagContainerId, SourceBagContainerId = sourceBagContainerId },
                    context.Transaction,
                    cancellationToken: cancellationToken));
            }
            else
            {
                await context.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into item_containers (
                        id,
                        container_type,
                        slot_capacity,
                        lifecycle)
                    values (@BagContainerId, 'corpse_bag_contents', 1, 'active');

                    insert into item_container_slots (container_id, slot_index, slot_kind)
                    values (@BagContainerId, 0, 'general');
                    """,
                    new { BagContainerId = bagContainerId },
                    context.Transaction,
                    cancellationToken: cancellationToken));
            }
        }

        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into corpse_sections (corpse_id, section_kind, container_id)
            values
                (@CorpseId, 'general_inventory', @GeneralContainerId),
                (@CorpseId, 'equipment', @EquipmentContainerId),
                (@CorpseId, 'bag', @BagContainerId);
            """,
            new
            {
                CorpseId = corpseId,
                GeneralContainerId = generalContainerId,
                EquipmentContainerId = equipmentContainerId,
                BagContainerId = bagContainerId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task CreateCorpseSnapshotsAsync(
        ItemTransactionContext context,
        Guid corpseId,
        LockedCharacterState characterState,
        IReadOnlyList<DeathPartitionItem> items,
        DeathPartitionItem? equippedBag,
        CancellationToken cancellationToken)
    {
        var secureTier = await context.Connection.QuerySingleAsync<SecureTierSnapshotRow>(
            new CommandDefinition(
                """
                select
                    tier.id as "TierId",
                    tier.display_name as "DisplayName",
                    tier.slot_capacity as "SlotCapacity"
                from characters character
                join account_secure_container_entitlements entitlement
                  on entitlement.account_id = character.account_id
                join secure_container_tiers tier on tier.id = entitlement.tier_id
                where character.id = @CharacterId;
                """,
                new { characterState.CharacterId },
                context.Transaction,
                cancellationToken: cancellationToken));
        var sortOrder = 0;
        await InsertCorpseSnapshotAsync(
            context,
            corpseId,
            "secure_container",
            sortOrder++,
            null,
            null,
            null,
            new
            {
                tierId = secureTier.TierId,
                displayName = secureTier.DisplayName,
                slotCapacity = secureTier.SlotCapacity
            },
            cancellationToken);

        foreach (var item in items
                     .Where(item =>
                         item.Disposition == ItemDeathDisposition.InsuredRecovery
                         && string.Equals(item.Source.SourceKind, "equipment", StringComparison.Ordinal)
                         && !item.IsEquippedBag)
                     .OrderBy(item => item.Source.EquipmentSortOrder)
                     .ThenBy(item => item.Item.ItemInstanceId))
        {
            await InsertCorpseSnapshotAsync(
                context,
                corpseId,
                "insured_equipment",
                sortOrder++,
                item.Item.DefinitionId,
                item.Source.EquipmentSlotId,
                ItemPolicyIds.Insured,
                new
                {
                    definitionId = item.Item.DefinitionId,
                    equipmentSlotId = item.Source.EquipmentSlotId,
                    insured = true
                },
                cancellationToken);
        }

        if (equippedBag is not null
            && equippedBag.Disposition is ItemDeathDisposition.ProtectedRecovery
                or ItemDeathDisposition.InsuredRecovery)
        {
            var layoutRows = (await context.Connection.QueryAsync<BagSnapshotSlotRow>(
                new CommandDefinition(
                    """
                    select
                        slot.slot_index as "SlotIndex",
                        slot.slot_kind as "SlotKind",
                        tag.tag_id as "TagId"
                    from bag_definition_slots slot
                    left join bag_definition_slot_tags tag
                      on tag.definition_id = slot.definition_id
                     and tag.slot_index = slot.slot_index
                    where slot.definition_id = @DefinitionId
                    order by slot.slot_index, tag.tag_id;
                    """,
                    new { equippedBag.Item.DefinitionId },
                    context.Transaction,
                    cancellationToken: cancellationToken))).ToArray();
            var slots = layoutRows
                .GroupBy(row => new { row.SlotIndex, row.SlotKind })
                .Select(group => new
                {
                    index = group.Key.SlotIndex,
                    kind = group.Key.SlotKind,
                    acceptedTags = group
                        .Where(row => row.TagId is not null)
                        .Select(row => row.TagId!)
                        .Order(StringComparer.Ordinal)
                        .ToArray()
                })
                .OrderBy(slot => slot.index)
                .ToArray();
            var protectedBag = equippedBag.Disposition == ItemDeathDisposition.ProtectedRecovery;
            await InsertCorpseSnapshotAsync(
                context,
                corpseId,
                protectedBag ? "protected_bag" : "insured_bag",
                sortOrder,
                equippedBag.Item.DefinitionId,
                "bag",
                protectedBag ? ItemPolicyIds.ProtectedOnDeath : ItemPolicyIds.Insured,
                new
                {
                    definitionId = equippedBag.Item.DefinitionId,
                    policyKind = protectedBag
                        ? ItemPolicyIds.ProtectedOnDeath
                        : ItemPolicyIds.Insured,
                    slots
                },
                cancellationToken);
        }
    }

    private static Task InsertCorpseSnapshotAsync(
        ItemTransactionContext context,
        Guid corpseId,
        string snapshotKind,
        int sortOrder,
        string? definitionId,
        string? equipmentSlotId,
        string? policyKind,
        object presentationPayload,
        CancellationToken cancellationToken)
    {
        return context.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into corpse_snapshots (
                id,
                corpse_id,
                snapshot_kind,
                sort_order,
                definition_id,
                equipment_slot_id,
                policy_kind,
                presentation_payload)
            values (
                @SnapshotId,
                @CorpseId,
                @SnapshotKind,
                @SortOrder,
                @DefinitionId,
                @EquipmentSlotId,
                @PolicyKind,
                cast(@PresentationPayload as jsonb));
            """,
            new
            {
                SnapshotId = Guid.NewGuid(),
                CorpseId = corpseId,
                SnapshotKind = snapshotKind,
                SortOrder = sortOrder,
                DefinitionId = definitionId,
                EquipmentSlotId = equipmentSlotId,
                PolicyKind = policyKind,
                PresentationPayload = JsonSerializer.Serialize(
                    presentationPayload,
                    OperationJsonOptions)
            },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task MoveLootableItemToCorpseAsync(
        ItemTransactionContext context,
        DeathPartitionItem item,
        Guid generalContainerId,
        Guid equipmentContainerId,
        Guid bagContainerId,
        bool useLiveBagAggregate,
        CancellationToken cancellationToken)
    {
        Guid destinationContainerId;
        int destinationSlotIndex;
        if (string.Equals(item.Source.SourceKind, "permanent_inventory", StringComparison.Ordinal))
        {
            destinationContainerId = generalContainerId;
            destinationSlotIndex = item.Source.ContainerSlotIndex
                ?? throw new InvalidOperationException("A permanent inventory item has no slot.");
        }
        else if (string.Equals(item.Source.SourceKind, "equipment", StringComparison.Ordinal))
        {
            destinationContainerId = equipmentContainerId;
            destinationSlotIndex = item.Source.EquipmentSortOrder
                ?? throw new InvalidOperationException("An equipped death item has no stable sort order.");
        }
        else
        {
            destinationContainerId = bagContainerId;
            destinationSlotIndex = item.Source.ContainerSlotIndex
                ?? throw new InvalidOperationException("A Bag child item has no slot.");
            if (useLiveBagAggregate
                && item.Source.ContainerId == bagContainerId)
            {
                await context.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    update item_instances
                    set revision = revision + 1,
                        updated_at = now()
                    where id = @ItemInstanceId;
                    """,
                    new { item.Item.ItemInstanceId },
                    context.Transaction,
                    cancellationToken: cancellationToken));
                await RecordDeathItemAuditAsync(
                    context,
                    item,
                    "death_partitioned_to_corpse_bag",
                    cancellationToken);
                context.TouchContainer(item.Source.ContainerId);
                context.IncludeResultItem(item.Item.ItemInstanceId);
                return;
            }
        }

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
                DestinationContainerId = destinationContainerId,
                DestinationSlotIndex = destinationSlotIndex,
                item.Item.ItemInstanceId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        await RecordDeathItemAuditAsync(
            context,
            item,
            "death_partitioned_to_corpse",
            cancellationToken);
        context.TouchContainer(item.Source.ContainerId);
        context.TouchContainer(destinationContainerId);
        context.IncludeResultItem(item.Item.ItemInstanceId);
    }

    private static async Task AddDeathRecoveryDeliveriesAsync(
        ItemTransactionContext context,
        Guid characterId,
        Guid recoveryStorageContainerId,
        Guid deathEventId,
        IReadOnlyList<DeathRecoveryGroup> requestedGroups,
        CancellationToken cancellationToken)
    {
        var groups = requestedGroups
            .Where(group => group.Items.Count > 0)
            .ToArray();
        if (groups.Length == 0)
        {
            return;
        }

        var nextSlotIndex = await GetNextRecoverySlotIndexAsync(
            context,
            recoveryStorageContainerId,
            cancellationToken);
        var sourceEventId = deathEventId.ToString("D");
        var assignments = new List<DeathRecoveryAssignment>();
        foreach (var group in groups)
        {
            ValidateRecoverySource(group.SourceKind, sourceEventId);
            var deliveryId = Guid.NewGuid();
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                insert into recovery_deliveries (
                    id,
                    character_id,
                    recovery_storage_container_id,
                    source_kind,
                    source_event_id)
                values (
                    @DeliveryId,
                    @CharacterId,
                    @RecoveryStorageContainerId,
                    @SourceKind,
                    @SourceEventId);
                """,
                new
                {
                    DeliveryId = deliveryId,
                    CharacterId = characterId,
                    RecoveryStorageContainerId = recoveryStorageContainerId,
                    SourceKind = group.SourceKind,
                    SourceEventId = sourceEventId
                },
                context.Transaction,
                cancellationToken: cancellationToken));
            context.AddRecoveryDelivery(deliveryId);

            var orderedGroupItems = group.Items
                .OrderBy(item => item.Item.ItemInstanceId)
                .ToArray();
            for (var itemOrder = 0; itemOrder < orderedGroupItems.Length; itemOrder++)
            {
                assignments.Add(new DeathRecoveryAssignment(
                    group,
                    orderedGroupItems[itemOrder],
                    deliveryId,
                    itemOrder,
                    nextSlotIndex++));
            }
        }

        foreach (var assignment in assignments
                     .OrderBy(assignment => assignment.Item.IsEquippedBag ? 1 : 0)
                     .ThenBy(assignment => assignment.Item.Item.ItemInstanceId))
        {
            var item = assignment.Item;
            if (assignment.Group.ConsumeInsurance)
            {
                var consumed = await context.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    update item_instance_policies
                    set status = 'consumed',
                        revision = revision + 1,
                        consumed_at = now()
                    where item_instance_id = @ItemInstanceId
                      and policy_kind = 'insured'
                      and status = 'active';
                    """,
                    new { item.Item.ItemInstanceId },
                    context.Transaction,
                    cancellationToken: cancellationToken));
                if (consumed != 1)
                {
                    throw new InvalidOperationException(
                        $"Insured death item '{item.Item.ItemInstanceId}' has no active insurance policy.");
                }
            }

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
                    RecoveryStorageContainerId = recoveryStorageContainerId,
                    assignment.SlotIndex,
                    item.Item.ItemInstanceId,
                    assignment.DeliveryId,
                    assignment.ItemOrder
                },
                context.Transaction,
                cancellationToken: cancellationToken));

            if (item.Definition.IsBag && item.Item.BagContentsContainerId is Guid bagContentsId)
            {
                var remainingChildren = await context.Connection.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        """
                        select count(*)
                        from item_instances
                        where container_id = @ContainerId;
                        """,
                        new { ContainerId = bagContentsId },
                        context.Transaction,
                        cancellationToken: cancellationToken));
                if (remainingChildren != 0)
                {
                    throw new InvalidOperationException(
                        $"Recovered Bag '{item.Item.ItemInstanceId}' still has child items after death partition.");
                }

                await context.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    update item_containers
                    set lifecycle = 'closed',
                        updated_at = now()
                    where id = @ContainerId;
                    """,
                    new { ContainerId = bagContentsId },
                    context.Transaction,
                    cancellationToken: cancellationToken));
                context.TouchContainer(bagContentsId);
            }

            await RecordDeathItemAuditAsync(
                context,
                item,
                assignment.Group.ConsumeInsurance
                    ? "death_recovered_insured"
                    : "death_recovered_protected",
                cancellationToken);
            context.TouchContainer(item.Source.ContainerId);
            context.TouchContainer(recoveryStorageContainerId);
            context.IncludeResultItem(item.Item.ItemInstanceId);
        }
    }

    private static async Task RecordDeathItemAuditAsync(
        ItemTransactionContext context,
        DeathPartitionItem item,
        string changeKind,
        CancellationToken cancellationToken)
    {
        var afterState = await context.CaptureItemStateJsonAsync(
            item.Item.ItemInstanceId,
            cancellationToken);
        context.AddItemAudit(
            changeKind,
            item.Item.ItemInstanceId,
            item.BeforeState,
            afterState);
    }

    private static async Task DeleteItemsAsync(
        ItemTransactionContext context,
        Guid[] itemIds,
        CancellationToken cancellationToken)
    {
        if (itemIds.Length == 0)
        {
            return;
        }

        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            delete from item_instances
            where id = any(@ItemIds);
            """,
            new { ItemIds = itemIds },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task LockDeathEventKeyAsync(
        ItemTransactionContext context,
        Guid deathEventId,
        CancellationToken cancellationToken)
    {
        var bytes = deathEventId.ToByteArray();
        var firstKey = BitConverter.ToInt32(bytes, 0) ^ 0x44_45_41_54;
        var secondKey = BitConverter.ToInt32(bytes, 4) ^ BitConverter.ToInt32(bytes, 8);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(@FirstKey, @SecondKey);",
            new { FirstKey = firstKey, SecondKey = secondKey },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    private static Task<DeathEventRow?> LoadDeathEventAsync(
        ItemTransactionContext context,
        Guid deathEventId,
        CancellationToken cancellationToken)
    {
        return context.Connection.QuerySingleOrDefaultAsync<DeathEventRow>(new CommandDefinition(
            """
            select
                event.request_hash as "RequestHash",
                event.corpse_id as "CorpseId",
                event.operation_id as "OriginalOperationId"
            from death_events event
            join corpses corpse on corpse.id = event.corpse_id
            where event.id = @DeathEventId
            for update of event, corpse;
            """,
            new { DeathEventId = deathEventId },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    private static string CalculateDeathEventRequestHash(ProcessPlayerDeathCommand command)
    {
        var canonical = JsonSerializer.Serialize(
            new DeathEventRequestFingerprint(
                command.DeathEventId,
                command.CharacterId,
                command.ShardId,
                command.PositionX,
                command.PositionY,
                command.PositionZ,
                command.RotationX,
                command.RotationY,
                command.RotationZ,
                command.RotationW,
                command.PresentationKey),
            OperationJsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void EnsureDeathAuthority(ItemTransactionContext context)
    {
        if (context.Actor.Authority is not ItemTransactionAuthority.System
            and not ItemTransactionAuthority.SimulationWorker)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.AuthorityRequired,
                "Player death partition requires system or simulation authority.");
        }
    }

    private static void ValidatePlayerDeathCommand(ProcessPlayerDeathCommand command)
    {
        if (command.DeathEventId == Guid.Empty || command.CharacterId == Guid.Empty)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.DeathEventConflict,
                "A death event and character id are required.");
        }

        if (!IsDeathIdentifier(command.ShardId))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.SimulationSessionInvalid,
                "The death event Shard id is invalid.");
        }

        if (!IsPresentationKey(command.PresentationKey)
            || !string.Equals(
                command.PresentationKey,
                PlayerCorpsePresentationKey,
                StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.DeathEventConflict,
                "Player death must use the authoritative generic corpse presentation key.");
        }

        if (!IsFinitePosition(command.PositionX)
            || !IsFinitePosition(command.PositionY)
            || !IsFinitePosition(command.PositionZ))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.DeathEventConflict,
                "The death transform position is invalid.");
        }

        var rotationLengthSquared = (command.RotationX * command.RotationX)
            + (command.RotationY * command.RotationY)
            + (command.RotationZ * command.RotationZ)
            + (command.RotationW * command.RotationW);
        if (!double.IsFinite(rotationLengthSquared)
            || Math.Abs(rotationLengthSquared - 1d) > 0.0001d
            || Math.Abs(command.RotationX) > 1d
            || Math.Abs(command.RotationY) > 1d
            || Math.Abs(command.RotationZ) > 1d
            || Math.Abs(command.RotationW) > 1d)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.DeathEventConflict,
                "The death transform rotation must be a normalized quaternion.");
        }
    }

    private static bool IsDeathIdentifier(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool IsPresentationKey(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static bool IsFinitePosition(double value)
    {
        return double.IsFinite(value) && value is >= -1_000_000d and <= 1_000_000d;
    }

    internal static string DefaultPlayerCorpsePresentationKey => PlayerCorpsePresentationKey;

    private sealed record DeathEventRequestFingerprint(
        Guid DeathEventId,
        Guid CharacterId,
        string ShardId,
        double PositionX,
        double PositionY,
        double PositionZ,
        double RotationX,
        double RotationY,
        double RotationZ,
        double RotationW,
        string PresentationKey);

    private sealed record DeathPartitionItem(
        DeathItemSourceRow Source,
        LockedItem Item,
        ResolvedItemDefinition Definition,
        ItemDeathDisposition Disposition,
        string? BeforeState)
    {
        public bool IsEquippedBag => Definition.IsBag
            && string.Equals(Source.SourceKind, "equipment", StringComparison.Ordinal)
            && string.Equals(Source.EquipmentSlotId, "bag", StringComparison.Ordinal);
    }

    private sealed record DeathRecoveryGroup(
        string SourceKind,
        bool ConsumeInsurance,
        IReadOnlyList<DeathPartitionItem> Items);

    private sealed record DeathRecoveryAssignment(
        DeathRecoveryGroup Group,
        DeathPartitionItem Item,
        Guid DeliveryId,
        int ItemOrder,
        int SlotIndex);

    private sealed class DeathItemSourceRow
    {
        public Guid ItemInstanceId { get; set; }

        public Guid? ContainerId { get; set; }

        public int? ContainerSlotIndex { get; set; }

        public string? EquipmentSlotId { get; set; }

        public int? EquipmentSortOrder { get; set; }

        public Guid? BagContentsContainerId { get; set; }

        public string SourceKind { get; set; } = string.Empty;
    }

    private sealed class CharacterDeathSourceRow
    {
        public string DisplayName { get; set; } = string.Empty;

        public bool ShardExists { get; set; }
    }

    private sealed class CreatedCorpseTimes
    {
        public DateTime CreatedAt { get; set; }

        public DateTime ExpiresAt { get; set; }
    }

    private sealed class DeathEventRow
    {
        public string RequestHash { get; set; } = string.Empty;

        public Guid CorpseId { get; set; }

        public Guid OriginalOperationId { get; set; }
    }

    private sealed class SecureTierSnapshotRow
    {
        public string TierId { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public int SlotCapacity { get; set; }
    }

    private sealed class BagSnapshotSlotRow
    {
        public int SlotIndex { get; set; }

        public string SlotKind { get; set; } = string.Empty;

        public string? TagId { get; set; }
    }

    private sealed class ExpiringCorpseRow
    {
        public Guid CorpseId { get; set; }

        public DateTime ExpiresAt { get; set; }

        public DateTime? ClosedAt { get; set; }

        public string? CloseReason { get; set; }

        public Guid ExpiryOperationId { get; set; }
    }

    private sealed class ExpiringCorpseItemRow
    {
        public Guid ItemInstanceId { get; set; }

        public string DefinitionId { get; set; } = string.Empty;

        public int Quantity { get; set; }

        public bool IsBag { get; set; }
    }
}
