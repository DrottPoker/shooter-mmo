using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.WorldData.Items;
using Npgsql;

namespace Mmorpg.GameService.Items.Transactions;

// The handler-facing API of one item transaction. The kernel creates it after the
// transaction-local timeouts are set; it never commits or rolls back.
internal sealed class ItemTransactionContext
{
    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HashSet<Guid> touchedCharacterIds = [];
    private readonly HashSet<Guid> touchedContainerIds = [];
    private readonly HashSet<Guid> resultItemIds = [];
    private readonly List<ItemAuditChange> auditChanges = [];
    private readonly List<Guid> recoveryDeliveryIds = [];
    private long? secureContainerEntitlementRevision;
    private bool allowInvoluntaryHardCapOverflow;
    private readonly ItemTransactionDependencies dependencies;
    private readonly ItemOperationsMetrics? operationsMetrics;

    public ItemTransactionContext(
        ItemTransactionSession session,
        ItemTransactionDependencies dependencies,
        ItemOperationsMetrics? operationsMetrics = null)
    {
        Session = session;
        this.dependencies = dependencies;
        this.operationsMetrics = operationsMetrics;
        Authority = new ItemActorAuthority(session.Actor);
        State = new ItemStateReader(session, Authority);
    }

    public ItemTransactionSession Session { get; }

    public NpgsqlConnection Connection => Session.Connection;

    public NpgsqlTransaction Transaction => Session.Transaction;

    // The transaction handed to other modules' contracts.
    public PostgresTransactionScope Scope => Session.Scope;

    public Guid OperationId => Session.OperationId;

    public string OperationKind => Session.OperationKind;

    public ItemTransactionActor Actor => Session.Actor;

    public ItemActorAuthority Authority { get; }

    public ItemStateReader State { get; }

    public async Task ValidateLiveAuthorityAsync(
        Guid? actorCharacterId,
        CancellationToken cancellationToken)
    {
        if (Actor.Authority != ItemTransactionAuthority.SimulationWorker)
        {
            return;
        }

        var simulation = Actor.Simulation!;
        if (actorCharacterId is null
            || actorCharacterId == Guid.Empty
            || actorCharacterId != simulation.CharacterId)
        {
            Reject(
                ItemTransactionErrorCodes.SimulationSessionInvalid,
                "The item operation character does not match the simulation session.");
        }

        var characterAccountId = await dependencies.CharacterLocks.LockActiveForItemMutationAsync(
            Scope,
            simulation.CharacterId,
            cancellationToken);
        if (characterAccountId is null || characterAccountId != Actor.AccountId)
        {
            Reject(
                ItemTransactionErrorCodes.ItemNotOwned,
                "The simulation account does not own the requested character.");
        }

        var session = await dependencies.SimulationSessions.LockForItemMutationAsync(
            Scope,
            simulation.SimulationSessionId,
            cancellationToken);
        if (session is null || !session.IsActive)
        {
            Reject(
                ItemTransactionErrorCodes.SimulationSessionInvalid,
                "The simulation session is invalid, released, or expired.");
        }

        if (session.AccountId != Actor.AccountId
            || session.CharacterId != simulation.CharacterId)
        {
            Reject(
                ItemTransactionErrorCodes.ItemNotOwned,
                "The simulation session does not own the requested character item state.");
        }

        if (!string.Equals(session.WorkerId, simulation.WorkerId, StringComparison.Ordinal))
        {
            Reject(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The simulation session is owned by another simulation worker.");
        }

        if (!string.Equals(
                session.WorkerRuntimeId,
                simulation.WorkerRuntimeId,
                StringComparison.Ordinal))
        {
            Reject(
                ItemTransactionErrorCodes.WorkerRuntimeChanged,
                "The simulation session is owned by another worker runtime.");
        }

        if (!string.Equals(session.ShardId, simulation.ShardId, StringComparison.Ordinal)
            || !ItemActorAuthority.HashesMatch(session.SessionTokenHash, simulation.SessionTokenHash))
        {
            Reject(
                ItemTransactionErrorCodes.SimulationSessionInvalid,
                "The simulation session credentials or Shard binding are invalid.");
        }

        var worker = await dependencies.SimulationWorkers.GetRuntimeAsync(
            Scope,
            simulation.WorkerId,
            cancellationToken);
        if (worker is null
            || !worker.IsOnline
            || !string.Equals(
                worker.RuntimeId,
                simulation.WorkerRuntimeId,
                StringComparison.Ordinal))
        {
            Reject(
                ItemTransactionErrorCodes.WorkerRuntimeChanged,
                "The simulation worker runtime no longer owns live authority.");
        }

        var ownsAssignment = await dependencies.SimulationWorkers.LockAssignmentForShareAsync(
            Scope,
            simulation.WorkerId,
            simulation.ShardId,
            cancellationToken);
        if (!ownsAssignment)
        {
            Reject(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The simulation worker no longer owns the requested Shard assignment.");
        }
    }

    public async Task<IReadOnlyDictionary<Guid, LockedCharacterState>> LockCharacterStatesAsync(
        IEnumerable<CharacterLockRequest> requests,
        CancellationToken cancellationToken)
    {
        var requested = requests
            .GroupBy(request => request.CharacterId)
            .Select(group =>
            {
                var revisions = group
                    .Where(request => request.ExpectedRevision is not null)
                    .Select(request => request.ExpectedRevision!.Value)
                    .Distinct()
                    .ToArray();
                if (revisions.Length > 1)
                {
                    Reject(
                        ItemTransactionErrorCodes.ItemStateConflict,
                        $"Character '{group.Key}' has conflicting expected revisions.");
                }

                return new CharacterLockRequest(
                    group.Key,
                    revisions.Length == 0 ? null : revisions[0]);
            })
            .OrderBy(request => request.CharacterId)
            .ToArray();

        if (requested.Length == 0)
        {
            return new Dictionary<Guid, LockedCharacterState>();
        }

        if (requested.Any(request => request.CharacterId == Guid.Empty))
        {
            Reject(
                ItemTransactionErrorCodes.ItemNotFound,
                "A character id is required.");
        }

        var lockStarted = Stopwatch.GetTimestamp();
        LockedCharacterState[] rows;
        try
        {
            // Characters are locked first and item states second, each in ascending
            // character order, so every multi-character mutation locks in one order.
            var accountIds = await dependencies.CharacterLocks.LockActiveForItemMutationAsync(
                Scope,
                requested.Select(request => request.CharacterId).ToArray(),
                cancellationToken);
            rows = (await Connection.QueryAsync<LockedCharacterState>(new CommandDefinition(
                """
                select
                    state.character_id as "CharacterId",
                    state.revision as "Revision",
                    state.carried_weight as "CarriedWeight",
                    state.base_carry_capacity as "BaseCarryCapacity",
                    state.carry_capacity as "CarryCapacity",
                    state.permanent_inventory_container_id as "PermanentInventoryContainerId",
                    state.bank_container_id as "BankContainerId",
                    state.secure_container_id as "SecureContainerId",
                    state.recovery_storage_container_id as "RecoveryStorageContainerId"
                from character_item_states state
                where state.character_id = any(@CharacterIds)
                order by state.character_id
                for update of state;
                """,
                new
                {
                    CharacterIds = requested
                        .Select(request => request.CharacterId)
                        .Where(accountIds.ContainsKey)
                        .ToArray()
                },
                Transaction,
                cancellationToken: cancellationToken))).ToArray();
            foreach (var row in rows)
            {
                row.AccountId = accountIds[row.CharacterId];
            }
        }
        finally
        {
            operationsMetrics?.RecordLockWait(
                "character_state",
                Stopwatch.GetElapsedTime(lockStarted));
        }

        if (rows.Length != requested.Length)
        {
            Reject(
                ItemTransactionErrorCodes.ItemNotFound,
                "One or more character item states were not found.");
        }

        var expectedByCharacter = requested.ToDictionary(request => request.CharacterId);
        foreach (var row in rows)
        {
            Authority.EnsureAccountAuthority(row.AccountId);
            var expected = expectedByCharacter[row.CharacterId].ExpectedRevision;
            if (Actor.Authority != ItemTransactionAuthority.System && expected is null)
            {
                Reject(
                    ItemTransactionErrorCodes.ItemStateConflict,
                    "Account-authorized item operations require an expected character revision.");
            }

            if (expected is not null && row.Revision != expected.Value)
            {
                Reject(
                    ItemTransactionErrorCodes.ItemStateConflict,
                    $"Character item state '{row.CharacterId}' changed before the operation committed.");
            }

            if (Actor.Authority == ItemTransactionAuthority.SimulationWorker
                && Actor.Simulation!.CharacterId != row.CharacterId)
            {
                Reject(
                    ItemTransactionErrorCodes.SimulationSessionInvalid,
                    "The item state does not belong to the active simulation character.");
            }
        }

        if (Actor.RequiresOfflineCharacter)
        {
            var activeCharacterId = await dependencies.SimulationPresence.FindActiveCharacterAsync(
                Scope,
                requested.Select(request => request.CharacterId).ToArray(),
                cancellationToken);
            if (activeCharacterId is not null)
            {
                Reject(
                    ItemTransactionErrorCodes.OfflineAccessRequired,
                    $"Character '{activeCharacterId}' is owned by an active simulation session.");
            }
        }

        return rows.ToDictionary(row => row.CharacterId);
    }

    public async Task<LockedCharacterState> LockCharacterStateForIdempotentEventAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        if (characterId == Guid.Empty)
        {
            Reject(ItemTransactionErrorCodes.ItemNotFound, "A character id is required.");
        }

        var lockStarted = Stopwatch.GetTimestamp();
        LockedCharacterState? row = null;
        try
        {
            // The character is locked before its item state, as in every item mutation.
            var accountId = await dependencies.CharacterLocks.LockActiveForItemMutationAsync(
                Scope,
                characterId,
                cancellationToken);
            if (accountId is not null)
            {
                row = await Connection.QuerySingleOrDefaultAsync<LockedCharacterState>(
                    new CommandDefinition(
                        """
                        select
                            state.character_id as "CharacterId",
                            state.revision as "Revision",
                            state.carried_weight as "CarriedWeight",
                            state.base_carry_capacity as "BaseCarryCapacity",
                            state.carry_capacity as "CarryCapacity",
                            state.permanent_inventory_container_id as "PermanentInventoryContainerId",
                            state.bank_container_id as "BankContainerId",
                            state.secure_container_id as "SecureContainerId",
                            state.recovery_storage_container_id as "RecoveryStorageContainerId"
                        from character_item_states state
                        where state.character_id = @CharacterId
                        for update of state;
                        """,
                        new { CharacterId = characterId },
                        Transaction,
                        cancellationToken: cancellationToken));
                if (row is not null)
                {
                    row.AccountId = accountId.Value;
                }
            }
        }
        finally
        {
            operationsMetrics?.RecordLockWait(
                "character_event",
                Stopwatch.GetElapsedTime(lockStarted));
        }
        if (row is null)
        {
            Reject(ItemTransactionErrorCodes.ItemNotFound, "The character item state was not found.");
        }

        Authority.EnsureAccountAuthority(row.AccountId);
        if (Actor.Authority == ItemTransactionAuthority.SimulationWorker
            && Actor.Simulation!.CharacterId != row.CharacterId)
        {
            Reject(
                ItemTransactionErrorCodes.SimulationSessionInvalid,
                "The item state does not belong to the active simulation character.");
        }

        return row;
    }

    public async Task LockMutationScopeAsync(
        IEnumerable<Guid> itemIds,
        IEnumerable<Guid> containerIds,
        CancellationToken cancellationToken)
    {
        var lockStarted = Stopwatch.GetTimestamp();
        try
        {
            await LockMutationScopeCoreAsync(itemIds, containerIds, cancellationToken);
        }
        finally
        {
            operationsMetrics?.RecordLockWait(
                "mutation_scope",
                Stopwatch.GetElapsedTime(lockStarted));
        }
    }

    private async Task LockMutationScopeCoreAsync(
        IEnumerable<Guid> itemIds,
        IEnumerable<Guid> containerIds,
        CancellationToken cancellationToken)
    {
        var requestedItemIds = itemIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Order()
            .ToArray();
        var requestedContainerIds = containerIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToHashSet();

        var discoveryRows = requestedItemIds.Length == 0
            ? []
            : (await Connection.QueryAsync<LockDiscoveryRow>(new CommandDefinition(
                """
                select
                    item.id as "ItemInstanceId",
                    item.container_id as "ContainerId",
                    case when bag.definition_id is null then null else item.id end as "ItemBagRootId",
                    source_container.bound_bag_item_instance_id as "SourceBagRootId",
                    bag_contents.id as "BagContentsContainerId"
                from item_instances item
                left join bag_definitions bag on bag.definition_id = item.definition_id
                left join item_containers source_container on source_container.id = item.container_id
                left join item_containers bag_contents
                  on bag_contents.bound_bag_item_instance_id = item.id
                 and bag_contents.container_type = 'bag_contents'
                where item.id = any(@ItemIds)
                order by item.id;
                """,
                new { ItemIds = requestedItemIds },
                Transaction,
                cancellationToken: cancellationToken))).ToArray();

        foreach (var sourceContainerId in discoveryRows
                     .Where(row => row.ContainerId is not null)
                     .Select(row => row.ContainerId!.Value))
        {
            requestedContainerIds.Add(sourceContainerId);
        }

        foreach (var bagContentsContainerId in discoveryRows
                     .Where(row => row.BagContentsContainerId is not null)
                     .Select(row => row.BagContentsContainerId!.Value))
        {
            requestedContainerIds.Add(bagContentsContainerId);
        }

        var explicitContainerIds = requestedContainerIds.Order().ToArray();
        var containerBagRoots = explicitContainerIds.Length == 0
            ? []
            : (await Connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select bound_bag_item_instance_id
                from item_containers
                where id = any(@ContainerIds)
                  and bound_bag_item_instance_id is not null
                order by bound_bag_item_instance_id;
                """,
                new { ContainerIds = explicitContainerIds },
                Transaction,
                cancellationToken: cancellationToken))).ToArray();

        var bagRootIds = discoveryRows
            .SelectMany(row => new[] { row.ItemBagRootId, row.SourceBagRootId })
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Concat(containerBagRoots)
            .Distinct()
            .Order()
            .ToArray();

        if (bagRootIds.Length > 0)
        {
            var lockedBagCount = (await Connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select id
                from item_instances
                where id = any(@BagItemIds)
                order by id
                for update;
                """,
                new { BagItemIds = bagRootIds },
                Transaction,
                cancellationToken: cancellationToken))).Count();
            if (lockedBagCount != bagRootIds.Length)
            {
                Reject(
                    ItemTransactionErrorCodes.BagStateChanged,
                    "A required Bag aggregate changed before it could be locked.");
            }
        }

        if (explicitContainerIds.Length > 0)
        {
            await Connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select id
                from item_containers
                where id = any(@ContainerIds)
                order by id
                for update;
                """,
                new { ContainerIds = explicitContainerIds },
                Transaction,
                cancellationToken: cancellationToken));
        }

        var aggregateContainerIds = discoveryRows
            .Where(row => row.ItemBagRootId is not null
                && row.BagContentsContainerId is not null)
            .Select(row => row.BagContentsContainerId!.Value)
            .Distinct()
            .Order()
            .ToArray();
        var aggregateChildItemIds = aggregateContainerIds.Length == 0
            ? []
            : (await Connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select id
                from item_instances
                where container_id = any(@ContainerIds)
                order by id;
                """,
                new { ContainerIds = aggregateContainerIds },
                Transaction,
                cancellationToken: cancellationToken))).ToArray();

        var ordinaryItemIds = requestedItemIds
            .Except(bagRootIds)
            .Concat(aggregateChildItemIds)
            .Distinct()
            .Order()
            .ToArray();
        if (ordinaryItemIds.Length > 0)
        {
            await Connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select id
                from item_instances
                where id = any(@ItemIds)
                order by id
                for update;
                """,
                new { ItemIds = ordinaryItemIds },
                Transaction,
                cancellationToken: cancellationToken));
        }

        var policyItemIds = requestedItemIds
            .Concat(bagRootIds)
            .Concat(aggregateChildItemIds)
            .Distinct()
            .Order()
            .ToArray();
        if (policyItemIds.Length > 0)
        {
            await Connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select id
                from item_instance_policies
                where item_instance_id = any(@ItemIds)
                order by item_instance_id, id
                for update;
                """,
                new { ItemIds = policyItemIds },
                Transaction,
                cancellationToken: cancellationToken));
        }
    }

    public async Task ValidateSimulationWorkerServiceAuthorityAsync(
        string workerId,
        string workerRuntimeId,
        string shardId,
        int heartbeatTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (Actor.Authority != ItemTransactionAuthority.System
            || !ItemActorAuthority.IsValidIdentifier(workerId)
            || !ItemActorAuthority.IsValidIdentifier(workerRuntimeId)
            || !ItemActorAuthority.IsValidIdentifier(shardId)
            || heartbeatTimeoutSeconds <= 0)
        {
            Reject(
                ItemTransactionErrorCodes.AuthorityRequired,
                "Mob corpse creation requires complete simulation worker service authority.");
        }

        var authority = await dependencies.SimulationWorkers.LockLeaseAsync(
            Scope,
            workerId,
            heartbeatTimeoutSeconds,
            cancellationToken);
        if (authority is null
            || !authority.HasFreshLease
            || !string.Equals(
                authority.RuntimeId,
                workerRuntimeId,
                StringComparison.Ordinal))
        {
            Reject(
                ItemTransactionErrorCodes.WorkerRuntimeChanged,
                "The simulation worker runtime no longer owns a fresh registry lease.");
        }

        var assignmentOwner = await dependencies.SimulationWorkers.LockAssignmentAsync(
            Scope,
            workerId,
            shardId,
            cancellationToken);
        if (!string.Equals(assignmentOwner, workerId, StringComparison.Ordinal))
        {
            Reject(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The simulation worker does not own the requested Shard assignment.");
        }
    }

    public async Task<int> SelectDestinationSlotAsync(
        LockedContainer container,
        ResolvedItemDefinition definition,
        int? requestedSlotIndex,
        bool bagHasContents,
        Guid? movingBagItemId,
        bool allowRecoveryStorage,
        CancellationToken cancellationToken,
        Guid? permittedOccupantItemInstanceId = null)
    {
        if (string.Equals(container.ContainerType, "recovery_storage", StringComparison.Ordinal))
        {
            if (!allowRecoveryStorage)
            {
                Reject(
                    ItemTransactionErrorCodes.RecoveryAccessRequired,
                    "Items can enter Recovery Storage only through a system delivery.");
            }

            Reject(
                ItemTransactionErrorCodes.ItemSlotIncompatible,
                "Recovery Storage delivery slots are allocated by the transaction kernel.");
        }

        if (string.Equals(container.ContainerType, "secure_container", StringComparison.Ordinal)
            && !SecureContainerRules.IsEligible(definition.RuntimeDefinition))
        {
            Reject(
                ItemTransactionErrorCodes.SecureContainerItemForbidden,
                "The item definition is not eligible for Secure Container custody.");
        }

        var rows = (await Connection.QueryAsync<ContainerSlotQueryRow>(new CommandDefinition(
            """
            select
                slot.slot_index as "SlotIndex",
                slot.slot_kind as "SlotKind",
                equipment_slot.id as "EquipmentSlotId",
                tag.tag_id as "TagId",
                occupant.id as "OccupantItemInstanceId"
            from item_container_slots slot
            left join equipment_slots equipment_slot
              on @ContainerType = 'corpse_equipment'
             and equipment_slot.sort_order = slot.slot_index
            left join item_container_slot_tags tag
              on tag.container_id = slot.container_id
             and tag.slot_index = slot.slot_index
            left join item_instances occupant
              on occupant.container_id = slot.container_id
             and occupant.container_slot_index = slot.slot_index
            where slot.container_id = @ContainerId
            order by slot.slot_index, tag.tag_id;
            """,
            new { container.ContainerId, container.ContainerType },
            Transaction,
            cancellationToken: cancellationToken))).ToArray();

        var slots = rows
            .GroupBy(row => new
            {
                row.SlotIndex,
                row.SlotKind,
                row.EquipmentSlotId,
                row.OccupantItemInstanceId
            })
            .Select(group => new DestinationSlot(
                group.Key.SlotIndex,
                group.Key.SlotKind,
                group.Where(row => row.TagId is not null).Select(row => row.TagId!).ToArray(),
                group.Key.EquipmentSlotId,
                group.Key.OccupantItemInstanceId))
            .OrderBy(slot => slot.SlotIndex)
            .ToArray();

        bool IsCompatible(DestinationSlot slot)
        {
            if (string.Equals(
                    container.ContainerType,
                    "corpse_equipment",
                    StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(slot.EquipmentSlotId)
                    || !ItemEquipmentRules.IsCompatible(
                        definition.RuntimeDefinition,
                        slot.EquipmentSlotId)))
            {
                return false;
            }

            if (definition.IsBag)
            {
                var destinationKind = ResolveBagDestinationKind(container.ContainerType, slot.SlotKind);
                var wouldCreateCycle = movingBagItemId is not null
                    && container.BoundBagItemInstanceId == movingBagItemId;
                return BagLocationRules.CanPlace(
                    bagHasContents,
                    destinationKind,
                    wouldCreateCycle);
            }

            return ItemSlotRules.Accepts(
                definition.RuntimeDefinition,
                new BagSlotDefinition
                {
                    Index = slot.SlotIndex,
                    Kind = slot.SlotKind,
                    AcceptedTags = slot.AcceptedTags
                });
        }

        if (requestedSlotIndex is not null)
        {
            var requested = slots.SingleOrDefault(slot => slot.SlotIndex == requestedSlotIndex.Value);
            if (requested is null || !IsCompatible(requested))
            {
                Reject(
                    definition.IsBag && bagHasContents
                        ? ItemTransactionErrorCodes.BagNotEmpty
                        : ItemTransactionErrorCodes.ItemSlotIncompatible,
                    "The requested destination slot is incompatible with the item.");
            }

            if (requested.OccupantItemInstanceId is not null
                && requested.OccupantItemInstanceId != permittedOccupantItemInstanceId)
            {
                Reject(
                    ItemTransactionErrorCodes.ItemSlotOccupied,
                    "The requested destination slot is occupied.");
            }

            return requested.SlotIndex;
        }

        var selected = slots.FirstOrDefault(slot =>
            slot.OccupantItemInstanceId is null && IsCompatible(slot));
        if (selected is null)
        {
            if (definition.IsBag && bagHasContents)
            {
                Reject(
                    ItemTransactionErrorCodes.BagNotEmpty,
                    "A non-empty Bag cannot enter an ordinary item slot.");
            }

            Reject(
                ItemTransactionErrorCodes.ItemSlotOccupied,
                "No compatible empty destination slot is available.");
        }

        return selected.SlotIndex;
    }

    public async Task EnsureEquipmentDestinationAsync(
        Guid characterId,
        string equipmentSlotId,
        ResolvedItemDefinition definition,
        bool bagHasContents,
        CancellationToken cancellationToken)
    {
        var slot = await Connection.QuerySingleOrDefaultAsync<EquipmentDestinationRow>(
            new CommandDefinition(
                """
                select
                    equipment.id as "EquipmentSlotId",
                    occupant.id as "OccupantItemInstanceId"
                from equipment_slots equipment
                left join item_instances occupant
                  on occupant.equipped_character_id = @CharacterId
                 and occupant.equipment_slot_id = equipment.id
                where equipment.id = @EquipmentSlotId;
                """,
                new { CharacterId = characterId, EquipmentSlotId = equipmentSlotId },
                Transaction,
                cancellationToken: cancellationToken));

        if (slot is null
            || !ItemEquipmentRules.IsCompatible(definition.RuntimeDefinition, equipmentSlotId)
            || (definition.IsBag
                && !BagLocationRules.CanPlace(
                    bagHasContents,
                    BagDestinationKind.CharacterBagEquipmentSlot,
                    false)))
        {
            Reject(
                ItemTransactionErrorCodes.EquipmentSlotIncompatible,
                "The equipment slot is incompatible with the item definition.");
        }

        if (slot.OccupantItemInstanceId is not null)
        {
            Reject(
                ItemTransactionErrorCodes.EquipmentSlotOccupied,
                "The requested equipment slot is occupied.");
        }
    }

    public async Task<string?> CaptureItemStateJsonAsync(
        Guid itemInstanceId,
        CancellationToken cancellationToken)
    {
        var item = await State.LoadItemAsync(itemInstanceId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var policies = await State.LoadPoliciesAsync(itemInstanceId, cancellationToken);
        var state = new ItemAuditState(
            item.ItemInstanceId,
            item.DefinitionId,
            item.Quantity,
            item.Revision,
            item.ContainerId,
            item.ContainerSlotIndex,
            item.EquippedCharacterId,
            item.EquipmentSlotId,
            policies.Select(policy => new ItemPolicyAuditState(
                policy.PolicyKind,
                policy.Status,
                policy.SourceKind,
                policy.SourceId,
                policy.Revision)).ToArray());
        return JsonSerializer.Serialize(state, AuditJsonOptions);
    }

    public void AddItemAudit(
        string changeKind,
        Guid itemInstanceId,
        string? beforeState,
        string? afterState)
    {
        if (beforeState is null && afterState is null)
        {
            throw new InvalidOperationException("An item audit change requires before or after state.");
        }

        auditChanges.Add(new ItemAuditChange(
            changeKind,
            afterState is null ? null : itemInstanceId,
            beforeState,
            afterState));
    }

    public void AddMetadataAudit(
        string changeKind,
        object? beforeState,
        object? afterState)
    {
        if (beforeState is null && afterState is null)
        {
            throw new InvalidOperationException("A metadata audit change requires before or after state.");
        }

        auditChanges.Add(new ItemAuditChange(
            changeKind,
            null,
            beforeState is null ? null : JsonSerializer.Serialize(beforeState, AuditJsonOptions),
            afterState is null ? null : JsonSerializer.Serialize(afterState, AuditJsonOptions)));
    }

    public void TouchCharacter(Guid characterId)
    {
        touchedCharacterIds.Add(characterId);
    }

    public void TouchContainer(Guid? containerId)
    {
        if (containerId is not null)
        {
            touchedContainerIds.Add(containerId.Value);
        }
    }

    public void IncludeResultItem(Guid itemInstanceId)
    {
        resultItemIds.Add(itemInstanceId);
    }

    public void AddRecoveryDelivery(Guid recoveryDeliveryId)
    {
        recoveryDeliveryIds.Add(recoveryDeliveryId);
    }

    public void SetSecureContainerEntitlementRevision(long revision)
    {
        secureContainerEntitlementRevision = revision;
    }

    public void AllowInvoluntaryDeathHardCapOverflow()
    {
        if (!string.Equals(
                OperationKind,
                ItemOperationKinds.ProcessPlayerDeath,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Only player death may permit involuntary carry overflow.");
        }

        allowInvoluntaryHardCapOverflow = true;
    }

    public async Task<ItemTransactionResult> FinalizeSuccessAsync(
        CancellationToken cancellationToken)
    {
        if (auditChanges.Count == 0)
        {
            throw new InvalidOperationException(
                $"Item operation '{OperationKind}' produced no audit changes.");
        }

        var containerIds = touchedContainerIds.Order().ToArray();
        if (containerIds.Length > 0)
        {
            await Connection.ExecuteAsync(new CommandDefinition(
                """
                update item_containers
                set revision = revision + 1,
                    updated_at = now()
                where id = any(@ContainerIds);
                """,
                new { ContainerIds = containerIds },
                Transaction,
                cancellationToken: cancellationToken));
        }

        var containerResults = containerIds.Length == 0
            ? []
            : (await Connection.QueryAsync<ItemTransactionContainerRevision>(
                new CommandDefinition(
                    """
                    select
                        id as "ContainerId",
                        revision as "Revision"
                    from item_containers
                    where id = any(@ContainerIds)
                    order by id;
                    """,
                    new { ContainerIds = containerIds },
                    Transaction,
                    cancellationToken: cancellationToken))).ToArray();

        var characterResults = new List<ItemTransactionCharacterRevision>();
        foreach (var characterId in touchedCharacterIds.Order())
        {
            var carry = await CalculateCarryStateAsync(characterId, cancellationToken);
            var previousCarry = await Connection.QuerySingleAsync<CharacterCarryResult>(
                new CommandDefinition(
                    """
                    select
                        revision as "Revision",
                        carried_weight as "CarriedWeight",
                        carry_capacity as "CarryCapacity"
                    from character_item_states
                    where character_id = @CharacterId;
                    """,
                    new { CharacterId = characterId },
                    Transaction,
                    cancellationToken: cancellationToken));
            if (!allowInvoluntaryHardCapOverflow
                && !EncumbranceRules.IsAllowedVoluntaryTransition(
                    previousCarry.CarriedWeight,
                    previousCarry.CarryCapacity,
                    carry.CarriedWeight,
                    carry.CarryCapacity))
            {
                Reject(
                    ItemTransactionErrorCodes.CarryWeightLimitExceeded,
                    "The operation would exceed the character carry-weight hard cap.");
            }

            var state = await Connection.QuerySingleAsync<CharacterCarryResult>(
                new CommandDefinition(
                    """
                    update character_item_states
                    set revision = revision + 1,
                        carried_weight = @CarriedWeight,
                        carry_capacity = @CarryCapacity,
                        updated_at = now()
                    where character_id = @CharacterId
                    returning
                        revision as "Revision",
                        carried_weight as "CarriedWeight",
                        carry_capacity as "CarryCapacity";
                    """,
                    new
                    {
                        CharacterId = characterId,
                        carry.CarriedWeight,
                        carry.CarryCapacity
                    },
                    Transaction,
                    cancellationToken: cancellationToken));
            characterResults.Add(new ItemTransactionCharacterRevision(
                characterId,
                state.Revision,
                state.CarriedWeight,
                state.CarryCapacity));
        }

        var resultItems = resultItemIds.Count == 0
            ? []
            : (await Connection.QueryAsync<ItemTransactionItemRevision>(new CommandDefinition(
                """
                select
                    id as "ItemInstanceId",
                    revision as "Revision"
                from item_instances
                where id = any(@ItemIds)
                order by id;
                """,
                new { ItemIds = resultItemIds.Order().ToArray() },
                Transaction,
                cancellationToken: cancellationToken))).ToArray();

        for (var index = 0; index < auditChanges.Count; index++)
        {
            var change = auditChanges[index];
            await Connection.ExecuteAsync(new CommandDefinition(
                """
                insert into item_operation_changes (
                    operation_id,
                    change_index,
                    item_instance_id,
                    change_kind,
                    before_state,
                    after_state)
                values (
                    @OperationId,
                    @ChangeIndex,
                    @ItemInstanceId,
                    @ChangeKind,
                    cast(@BeforeState as jsonb),
                    cast(@AfterState as jsonb));
                """,
                new
                {
                    OperationId,
                    ChangeIndex = index,
                    change.ItemInstanceId,
                    change.ChangeKind,
                    change.BeforeState,
                    change.AfterState
                },
                Transaction,
                cancellationToken: cancellationToken));
        }

        return new ItemTransactionResult(
            OperationId,
            OperationKind,
            true,
            null,
            characterResults,
            containerResults,
            resultItems,
            recoveryDeliveryIds.Order().ToArray(),
            secureContainerEntitlementRevision);
    }

    [DoesNotReturn]
    public static void Reject(string code, string message)
    {
        ItemTransactionRejection.Reject(code, message);
    }

    private async Task<CalculatedCarryState> CalculateCarryStateAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        var baseCapacity = await Connection.QuerySingleAsync<long>(new CommandDefinition(
            """
            select base_carry_capacity
            from character_item_states
            where character_id = @CharacterId;
            """,
            new { CharacterId = characterId },
            Transaction,
            cancellationToken: cancellationToken));
        var bagBonus = await Connection.QuerySingleAsync<long>(new CommandDefinition(
            """
            select coalesce(max(bag.carry_capacity_bonus), 0)
            from item_instances item
            join bag_definitions bag on bag.definition_id = item.definition_id
            where item.equipped_character_id = @CharacterId
              and item.equipment_slot_id = 'bag';
            """,
            new { CharacterId = characterId },
            Transaction,
            cancellationToken: cancellationToken));
        var weights = await Connection.QueryAsync<CarriedItemWeightRow>(new CommandDefinition(
            """
            -- Each branch is an indexed lookup; an OR across joined tables scans every item.
            with carried_containers as (
                select state.permanent_inventory_container_id as container_id
                from character_item_states state
                where state.character_id = @CharacterId
                union
                select state.secure_container_id
                from character_item_states state
                where state.character_id = @CharacterId
                union
                select container.id
                from item_instances bag
                join item_containers container
                  on container.bound_bag_item_instance_id = bag.id
                where bag.equipped_character_id = @CharacterId
                  and bag.equipment_slot_id = 'bag'
                  and container.container_type = 'bag_contents'
                  and container.lifecycle = 'active'
            )
            select
                definition.unit_weight as "UnitWeight",
                item.quantity as "Quantity"
            from carried_containers carried
            join item_instances item on item.container_id = carried.container_id
            join item_definitions definition on definition.id = item.definition_id;
            """,
            new { CharacterId = characterId },
            Transaction,
            cancellationToken: cancellationToken));

        long carriedWeight;
        long carryCapacity;
        try
        {
            carriedWeight = ItemWeightRules.SumWeight(weights.Select(row =>
                ItemWeightRules.CalculateStackWeight(row.UnitWeight, row.Quantity)));
            carryCapacity = checked(baseCapacity + bagBonus);
        }
        catch (OverflowException)
        {
            Reject(
                ItemTransactionErrorCodes.CarryWeightLimitExceeded,
                "The operation would overflow authoritative carry-weight arithmetic.");
            throw;
        }

        return new CalculatedCarryState(carriedWeight, carryCapacity);
    }

    private static BagDestinationKind ResolveBagDestinationKind(
        string containerType,
        string slotKind)
    {
        if (string.Equals(containerType, "permanent_inventory", StringComparison.Ordinal)
            && string.Equals(slotKind, BagSlotKindIds.General, StringComparison.Ordinal))
        {
            return BagDestinationKind.PermanentInventoryGeneralSlot;
        }

        if (string.Equals(containerType, "bank", StringComparison.Ordinal)
            && string.Equals(slotKind, BagSlotKindIds.General, StringComparison.Ordinal))
        {
            return BagDestinationKind.BankGeneralSlot;
        }

        if (string.Equals(containerType, "bag_contents", StringComparison.Ordinal))
        {
            return string.Equals(slotKind, BagSlotKindIds.General, StringComparison.Ordinal)
                ? BagDestinationKind.BagGeneralSlot
                : BagDestinationKind.BagSpecializedSlot;
        }

        if (string.Equals(containerType, "recovery_storage", StringComparison.Ordinal))
        {
            return BagDestinationKind.RecoveryStorageSlot;
        }

        if (string.Equals(containerType, "corpse_inventory", StringComparison.Ordinal)
            || string.Equals(containerType, "corpse_equipment", StringComparison.Ordinal)
            || string.Equals(containerType, "corpse_bag_contents", StringComparison.Ordinal))
        {
            return BagDestinationKind.CorpseStorageSlot;
        }

        return BagDestinationKind.SecureContainerSlot;
    }

    private sealed record ItemAuditChange(
        string ChangeKind,
        Guid? ItemInstanceId,
        string? BeforeState,
        string? AfterState);

    private sealed record ItemAuditState(
        Guid ItemInstanceId,
        string DefinitionId,
        int Quantity,
        long Revision,
        Guid? ContainerId,
        int? ContainerSlotIndex,
        Guid? EquippedCharacterId,
        string? EquipmentSlotId,
        IReadOnlyList<ItemPolicyAuditState> Policies);

    private sealed record ItemPolicyAuditState(
        string PolicyKind,
        string Status,
        string SourceKind,
        string SourceId,
        long Revision);

    private sealed class LockDiscoveryRow
    {
        public Guid ItemInstanceId { get; set; }

        public Guid? ContainerId { get; set; }

        public Guid? ItemBagRootId { get; set; }

        public Guid? SourceBagRootId { get; set; }

        public Guid? BagContentsContainerId { get; set; }
    }

    private sealed class ContainerSlotQueryRow
    {
        public int SlotIndex { get; set; }

        public string SlotKind { get; set; } = string.Empty;

        public string? EquipmentSlotId { get; set; }

        public string? TagId { get; set; }

        public Guid? OccupantItemInstanceId { get; set; }
    }

    private sealed record DestinationSlot(
        int SlotIndex,
        string SlotKind,
        string[] AcceptedTags,
        string? EquipmentSlotId,
        Guid? OccupantItemInstanceId);

    private sealed class EquipmentDestinationRow
    {
        public string EquipmentSlotId { get; set; } = string.Empty;

        public Guid? OccupantItemInstanceId { get; set; }
    }

    private sealed class CharacterCarryResult
    {
        public long Revision { get; set; }

        public long CarriedWeight { get; set; }

        public long CarryCapacity { get; set; }
    }

    private sealed class CarriedItemWeightRow
    {
        public long UnitWeight { get; set; }

        public int Quantity { get; set; }
    }

    private sealed record CalculatedCarryState(
        long CarriedWeight,
        long CarryCapacity);
}
