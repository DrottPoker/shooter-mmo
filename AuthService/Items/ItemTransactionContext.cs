using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using ShooterMmo.WorldData.Items;

namespace AuthService.Items;

internal sealed class ItemTransactionContext(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    Guid operationId,
    string operationKind,
    ItemTransactionActor actor)
{
    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HashSet<Guid> touchedCharacterIds = [];
    private readonly HashSet<Guid> touchedContainerIds = [];
    private readonly HashSet<Guid> resultItemIds = [];
    private readonly List<ItemAuditChange> auditChanges = [];
    private readonly List<Guid> recoveryDeliveryIds = [];
    private long? secureContainerEntitlementRevision;
    private bool allowInvoluntaryHardCapOverflow;

    public NpgsqlConnection Connection { get; } = connection;

    public NpgsqlTransaction Transaction { get; } = transaction;

    public Guid OperationId { get; } = operationId;

    public string OperationKind { get; } = operationKind;

    public ItemTransactionActor Actor { get; } = actor;

    public void EnsureActorIsValid()
    {
        if (Actor.Authority == ItemTransactionAuthority.Account)
        {
            if (Actor.AccountId is null || Actor.AccountId == Guid.Empty)
            {
                Reject(
                    ItemTransactionErrorCodes.AuthorityRequired,
                    "An account-authorized item operation requires an account id.");
            }

            if (Actor.Simulation is not null)
            {
                Reject(
                    ItemTransactionErrorCodes.AuthorityRequired,
                    "An account-authorized item operation cannot supply simulation authority.");
            }

            return;
        }

        if (Actor.Authority == ItemTransactionAuthority.System)
        {
            if (Actor.AccountId is not null
                || Actor.RequiresOfflineCharacter
                || Actor.Simulation is not null)
            {
                Reject(
                    ItemTransactionErrorCodes.AuthorityRequired,
                    "A system item operation cannot impersonate account or simulation authority.");
            }

            return;
        }

        if (Actor.Authority == ItemTransactionAuthority.SimulationWorker)
        {
            var simulation = Actor.Simulation;
            if (Actor.AccountId is null
                || Actor.AccountId == Guid.Empty
                || Actor.RequiresOfflineCharacter
                || simulation is null
                || simulation.SimulationSessionId == Guid.Empty
                || simulation.CharacterId == Guid.Empty
                || !IsValidIdentifier(simulation.WorkerId)
                || !IsValidIdentifier(simulation.WorkerRuntimeId)
                || !IsValidIdentifier(simulation.ShardId)
                || string.IsNullOrWhiteSpace(simulation.SessionTokenHash)
                || simulation.LiveAccess is null)
            {
                Reject(
                    ItemTransactionErrorCodes.AuthorityRequired,
                    "A simulation-authorized item operation requires complete live authority.");
            }

            return;
        }

        Reject(
            ItemTransactionErrorCodes.AuthorityRequired,
            "The item operation authority is invalid.");
    }

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

        var characterAccountId = await Connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                select account_id
                from characters
                where id = @CharacterId
                  and deleted_at is null
                for no key update;
                """,
                new { CharacterId = simulation.CharacterId },
                Transaction,
                cancellationToken: cancellationToken));
        if (characterAccountId is null || characterAccountId != Actor.AccountId)
        {
            Reject(
                ItemTransactionErrorCodes.ItemNotOwned,
                "The simulation account does not own the requested character.");
        }

        var session = await Connection.QuerySingleOrDefaultAsync<LiveSimulationSessionRow>(
            new CommandDefinition(
                """
                select
                    simulation_session.account_id as "AccountId",
                    simulation_session.character_id as "CharacterId",
                    simulation_session.shard_id as "ShardId",
                    simulation_session.simulation_worker_id as "WorkerId",
                    simulation_session.worker_runtime_id as "WorkerRuntimeId",
                    simulation_session.session_token_hash as "SessionTokenHash",
                    simulation_session.released_at is null
                        and simulation_session.expires_at > now()
                        and (
                            simulation_session.account_session_id is null
                            or exists (
                                select 1
                                from account_sessions account_session
                                where account_session.id = simulation_session.account_session_id
                                  and account_session.revoked_at is null
                                  and account_session.expires_at > now())) as "IsActive"
                from character_simulation_sessions simulation_session
                where simulation_session.id = @SimulationSessionId
                for update of simulation_session;
                """,
                new { simulation.SimulationSessionId },
                Transaction,
                cancellationToken: cancellationToken));
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
            || !HashesMatch(session.SessionTokenHash, simulation.SessionTokenHash))
        {
            Reject(
                ItemTransactionErrorCodes.SimulationSessionInvalid,
                "The simulation session credentials or Shard binding are invalid.");
        }

        var worker = await Connection.QuerySingleOrDefaultAsync<LiveSimulationWorkerRow>(
            new CommandDefinition(
                """
                select
                    worker.runtime_id as "RuntimeId",
                    worker.is_online as "IsOnline"
                from simulation_workers worker
                where worker.id = @WorkerId;
                """,
                new { WorkerId = simulation.WorkerId },
                Transaction,
                cancellationToken: cancellationToken));
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

        var assignmentId = await Connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                """
                select assignment.id
                from simulation_assignments assignment
                where assignment.worker_id = @WorkerId
                  and assignment.shard_id = @ShardId
                  and assignment.released_at is null
                for share of assignment;
                """,
                new
                {
                    WorkerId = simulation.WorkerId,
                    ShardId = simulation.ShardId
                },
                Transaction,
                cancellationToken: cancellationToken));
        if (string.IsNullOrWhiteSpace(assignmentId))
        {
            Reject(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The simulation worker no longer owns the requested Shard assignment.");
        }
    }

    public void EnsureSystemAuthority()
    {
        if (Actor.Authority != ItemTransactionAuthority.System)
        {
            Reject(
                ItemTransactionErrorCodes.AuthorityRequired,
                "This item operation requires system authority.");
        }
    }

    public void EnsureAccountAuthority(Guid accountId)
    {
        if ((Actor.Authority is ItemTransactionAuthority.Account
                or ItemTransactionAuthority.SimulationWorker)
            && Actor.AccountId != accountId)
        {
            Reject(
                ItemTransactionErrorCodes.ItemNotOwned,
                "The account does not own the requested item state.");
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

        var rows = (await Connection.QueryAsync<LockedCharacterState>(new CommandDefinition(
            """
            select
                state.character_id as "CharacterId",
                character.account_id as "AccountId",
                state.revision as "Revision",
                state.carried_weight as "CarriedWeight",
                state.base_carry_capacity as "BaseCarryCapacity",
                state.carry_capacity as "CarryCapacity",
                state.permanent_inventory_container_id as "PermanentInventoryContainerId",
                state.bank_container_id as "BankContainerId",
                state.secure_container_id as "SecureContainerId",
                state.recovery_storage_container_id as "RecoveryStorageContainerId"
            from character_item_states state
            join characters character on character.id = state.character_id
            where state.character_id = any(@CharacterIds)
              and character.deleted_at is null
            order by state.character_id
            for no key update of character
            for update of state;
            """,
            new { CharacterIds = requested.Select(request => request.CharacterId).ToArray() },
            Transaction,
            cancellationToken: cancellationToken))).ToArray();

        if (rows.Length != requested.Length)
        {
            Reject(
                ItemTransactionErrorCodes.ItemNotFound,
                "One or more character item states were not found.");
        }

        var expectedByCharacter = requested.ToDictionary(request => request.CharacterId);
        foreach (var row in rows)
        {
            EnsureAccountAuthority(row.AccountId);
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
            var activeCharacterId = await Connection.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    """
                    select character_id
                    from character_simulation_sessions
                    where character_id = any(@CharacterIds)
                      and released_at is null
                      and expires_at > now()
                    order by character_id
                    limit 1;
                    """,
                    new { CharacterIds = requested.Select(request => request.CharacterId).ToArray() },
                    Transaction,
                    cancellationToken: cancellationToken));
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

        var row = await Connection.QuerySingleOrDefaultAsync<LockedCharacterState>(
            new CommandDefinition(
                """
                select
                    state.character_id as "CharacterId",
                    character.account_id as "AccountId",
                    state.revision as "Revision",
                    state.carried_weight as "CarriedWeight",
                    state.base_carry_capacity as "BaseCarryCapacity",
                    state.carry_capacity as "CarryCapacity",
                    state.permanent_inventory_container_id as "PermanentInventoryContainerId",
                    state.bank_container_id as "BankContainerId",
                    state.secure_container_id as "SecureContainerId",
                    state.recovery_storage_container_id as "RecoveryStorageContainerId"
                from character_item_states state
                join characters character on character.id = state.character_id
                where state.character_id = @CharacterId
                  and character.deleted_at is null
                for no key update of character
                for update of state;
                """,
                new { CharacterId = characterId },
                Transaction,
                cancellationToken: cancellationToken));
        if (row is null)
        {
            Reject(ItemTransactionErrorCodes.ItemNotFound, "The character item state was not found.");
        }

        EnsureAccountAuthority(row.AccountId);
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

    public async Task<LockedItem> LoadOwnedItemAsync(
        Guid itemInstanceId,
        Guid characterId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var item = await LoadItemAsync(itemInstanceId, cancellationToken);
        if (item is null)
        {
            Reject(ItemTransactionErrorCodes.ItemNotFound, "The item instance was not found.");
        }

        if (item.OwningCharacterId != characterId)
        {
            Reject(ItemTransactionErrorCodes.ItemNotOwned, "The character does not own the item instance.");
        }

        if (item.Revision != expectedRevision)
        {
            Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                $"Item '{itemInstanceId}' changed before the operation committed.");
        }

        EnsureLiveContainerAccess(item.SourceContainerType);

        return item;
    }

    public Task<LockedItem?> LoadItemAsync(
        Guid itemInstanceId,
        CancellationToken cancellationToken)
    {
        return Connection.QuerySingleOrDefaultAsync<LockedItem>(new CommandDefinition(
            """
            select
                item.id as "ItemInstanceId",
                item.definition_id as "DefinitionId",
                item.quantity as "Quantity",
                item.revision as "Revision",
                item.container_id as "ContainerId",
                item.container_slot_index as "ContainerSlotIndex",
                item.equipped_character_id as "EquippedCharacterId",
                item.equipment_slot_id as "EquipmentSlotId",
                source_container.container_type as "SourceContainerType",
                source_container.lifecycle as "SourceContainerLifecycle",
                source_container.bound_bag_item_instance_id as "SourceBagItemInstanceId",
                coalesce(
                    item.equipped_character_id,
                    source_container.owner_character_id,
                    source_bag.equipped_character_id) as "OwningCharacterId",
                (bag.definition_id is not null) as "IsBag",
                bag.carry_capacity_bonus as "BagCarryCapacityBonus",
                bag_container.id as "BagContentsContainerId",
                bag_container.lifecycle as "BagContentsLifecycle",
                bag_container.revision as "BagContentsRevision"
            from item_instances item
            left join item_containers source_container on source_container.id = item.container_id
            left join item_instances source_bag
              on source_bag.id = source_container.bound_bag_item_instance_id
            left join bag_definitions bag on bag.definition_id = item.definition_id
            left join item_containers bag_container
              on bag_container.bound_bag_item_instance_id = item.id
             and bag_container.container_type = 'bag_contents'
            where item.id = @ItemInstanceId;
            """,
            new { ItemInstanceId = itemInstanceId },
            Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<LockedContainer> LoadOwnedContainerAsync(
        Guid containerId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        var container = await LoadContainerAsync(containerId, cancellationToken);

        if (container.OwningCharacterId != characterId)
        {
            Reject(ItemTransactionErrorCodes.ItemNotOwned, "The character does not own the item container.");
        }

        EnsureLiveContainerAccess(container.ContainerType);

        return container;
    }

    public async Task<LockedContainer> LoadContainerAsync(
        Guid containerId,
        CancellationToken cancellationToken)
    {
        var container = await Connection.QuerySingleOrDefaultAsync<LockedContainer>(
            new CommandDefinition(
                """
                select
                    container.id as "ContainerId",
                    container.container_type as "ContainerType",
                    container.owner_character_id as "DirectOwnerCharacterId",
                    container.bound_bag_item_instance_id as "BoundBagItemInstanceId",
                    coalesce(
                        container.owner_character_id,
                        bound_bag.equipped_character_id) as "OwningCharacterId",
                    container.slot_capacity as "SlotCapacity",
                    container.revision as "Revision",
                    container.lifecycle as "Lifecycle"
                from item_containers container
                left join item_instances bound_bag
                  on bound_bag.id = container.bound_bag_item_instance_id
                where container.id = @ContainerId;
                """,
                new { ContainerId = containerId },
                Transaction,
                cancellationToken: cancellationToken));

        if (container is null || !string.Equals(container.Lifecycle, "active", StringComparison.Ordinal))
        {
            Reject(ItemTransactionErrorCodes.ItemNotFound, "The item container was not found.");
        }

        return container;
    }

    public void EnsureInsuranceNpcAccess()
    {
        if (Actor.Authority != ItemTransactionAuthority.SimulationWorker
            || !Actor.Simulation!.LiveAccess.InsuranceNpc)
        {
            Reject(
                ItemTransactionErrorCodes.AuthorityRequired,
                "This operation requires a validated insurance NPC interaction.");
        }
    }

    public void EnsureQuestNpcAccess()
    {
        if (Actor.Authority != ItemTransactionAuthority.SimulationWorker
            || !Actor.Simulation!.LiveAccess.QuestNpc)
        {
            Reject(
                ItemTransactionErrorCodes.AuthorityRequired,
                "This operation requires a validated quest NPC interaction.");
        }
    }

    private void EnsureLiveContainerAccess(string? containerType)
    {
        if (Actor.Authority != ItemTransactionAuthority.SimulationWorker
            || string.IsNullOrWhiteSpace(containerType))
        {
            return;
        }

        if (string.Equals(containerType, "bank", StringComparison.Ordinal)
            && !Actor.Simulation!.LiveAccess.Bank)
        {
            Reject(
                ItemTransactionErrorCodes.BankAccessRequired,
                "The character is outside validated bank access.");
        }

        if (string.Equals(containerType, "recovery_storage", StringComparison.Ordinal)
            && !Actor.Simulation!.LiveAccess.RecoveryStorage)
        {
            Reject(
                ItemTransactionErrorCodes.RecoveryAccessRequired,
                "The character is outside validated Recovery Storage access.");
        }
    }

    private static bool IsValidIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool HashesMatch(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    public async Task<ResolvedItemDefinition> LoadDefinitionAsync(
        string definitionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definitionId))
        {
            Reject(ItemTransactionErrorCodes.ItemNotFound, "An item definition id is required.");
        }

        using var grid = await Connection.QueryMultipleAsync(new CommandDefinition(
            """
            select
                definition.id as "Id",
                definition.display_name as "DisplayName",
                definition.category_id as "Category",
                definition.unit_weight as "UnitWeight",
                definition.maximum_stack_size as "MaximumStackSize",
                definition.player_destroyable as "PlayerDestroyable",
                definition.structural_fingerprint as "StructuralFingerprint",
                coalesce(location.is_allowed, false) as "SecureContainerEligible",
                bag.carry_capacity_bonus as "BagCarryCapacityBonus"
            from item_definitions definition
            left join item_definition_location_rules location
              on location.definition_id = definition.id
             and location.location_kind = 'secure_container'
            left join bag_definitions bag on bag.definition_id = definition.id
            where definition.id = @DefinitionId
              and definition.is_active;

            select tag_id
            from item_definition_tags
            where definition_id = @DefinitionId
            order by tag_id;

            select equipment_slot_id
            from item_definition_equipment_slots
            where definition_id = @DefinitionId
            order by equipment_slot_id;

            select policy_kind
            from item_definition_default_policies
            where definition_id = @DefinitionId
            order by policy_kind;
            """,
            new { DefinitionId = definitionId },
            Transaction,
            cancellationToken: cancellationToken));

        var row = await grid.ReadSingleOrDefaultAsync<DefinitionRow>();
        if (row is null)
        {
            Reject(ItemTransactionErrorCodes.ItemNotFound, "The item definition was not found.");
        }

        var tags = (await grid.ReadAsync<string>()).ToArray();
        var equipmentSlots = (await grid.ReadAsync<string>()).ToArray();
        var defaultPolicies = (await grid.ReadAsync<string>()).ToArray();
        var definition = new ItemDefinition
        {
            Id = row.Id,
            DisplayName = row.DisplayName,
            Category = row.Category,
            Tags = tags,
            UnitWeight = row.UnitWeight,
            MaximumStackSize = row.MaximumStackSize,
            EquipmentSlots = equipmentSlots,
            PlayerDestroyable = row.PlayerDestroyable,
            LocationEligibility = new ItemLocationEligibility
            {
                SecureContainer = row.SecureContainerEligible
            },
            DefaultPolicies = defaultPolicies,
            StructuralFingerprint = row.StructuralFingerprint
        };

        return new ResolvedItemDefinition(
            definition,
            row.BagCarryCapacityBonus is not null);
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

    public Task<bool> BagHasContentsAsync(
        LockedItem item,
        CancellationToken cancellationToken)
    {
        if (!item.IsBag || item.BagContentsContainerId is null)
        {
            return Task.FromResult(false);
        }

        return Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists (
                select 1
                from item_instances
                where container_id = @ContainerId);
            """,
            new { ContainerId = item.BagContentsContainerId.Value },
            Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Guid>> LoadBagAggregateItemIdsAsync(
        Guid bagItemInstanceId,
        CancellationToken cancellationToken)
    {
        return (await Connection.QueryAsync<Guid>(new CommandDefinition(
            """
            select item.id
            from item_instances item
            where item.id = @BagItemInstanceId
               or item.container_id in (
                    select container.id
                    from item_containers container
                    where container.bound_bag_item_instance_id = @BagItemInstanceId
                      and container.container_type = 'bag_contents')
            order by item.id;
            """,
            new { BagItemInstanceId = bagItemInstanceId },
            Transaction,
            cancellationToken: cancellationToken))).ToArray();
    }

    public Task<IReadOnlyList<LockedItemPolicy>> LoadPoliciesAsync(
        Guid itemInstanceId,
        CancellationToken cancellationToken)
    {
        return LoadPoliciesCoreAsync(itemInstanceId, cancellationToken);
    }

    public async Task<string?> CaptureItemStateJsonAsync(
        Guid itemInstanceId,
        CancellationToken cancellationToken)
    {
        var item = await LoadItemAsync(itemInstanceId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var policies = await LoadPoliciesCoreAsync(itemInstanceId, cancellationToken);
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
            if (!EncumbranceRules.IsWithinHardCap(carry.CarriedWeight, carry.CarryCapacity)
                && !allowInvoluntaryHardCapOverflow
                && !IsNonWorseningExistingOverflow(previousCarry, carry))
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

    private static bool IsNonWorseningExistingOverflow(
        CharacterCarryResult previousCarry,
        CalculatedCarryState nextCarry)
    {
        return !EncumbranceRules.IsWithinHardCap(
                previousCarry.CarriedWeight,
                previousCarry.CarryCapacity)
            && nextCarry.CarriedWeight <= previousCarry.CarriedWeight
            && ((BigInteger)nextCarry.CarriedWeight * previousCarry.CarryCapacity)
                <= ((BigInteger)previousCarry.CarriedWeight * nextCarry.CarryCapacity);
    }

    [DoesNotReturn]
    public static void Reject(string code, string message)
    {
        throw new ItemTransactionRejectedException(code, message);
    }

    public static string CalculatePolicyFingerprint(IEnumerable<LockedItemPolicy> policies)
    {
        var canonical = string.Join(
            '\n',
            policies
                .OrderBy(policy => policy.PolicyKind, StringComparer.Ordinal)
                .ThenBy(policy => policy.Status, StringComparer.Ordinal)
                .ThenBy(policy => policy.SourceKind, StringComparer.Ordinal)
                .ThenBy(policy => policy.SourceId, StringComparer.Ordinal)
                .Select(policy => string.Join(
                    '|',
                    policy.PolicyKind,
                    policy.Status,
                    policy.SourceKind,
                    policy.SourceId)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task<IReadOnlyList<LockedItemPolicy>> LoadPoliciesCoreAsync(
        Guid itemInstanceId,
        CancellationToken cancellationToken)
    {
        return (await Connection.QueryAsync<LockedItemPolicy>(new CommandDefinition(
            """
            select
                id as "PolicyId",
                policy_kind as "PolicyKind",
                status as "Status",
                source_kind as "SourceKind",
                source_id as "SourceId",
                revision as "Revision"
            from item_instance_policies
            where item_instance_id = @ItemInstanceId
            order by policy_kind, source_kind, source_id, id;
            """,
            new { ItemInstanceId = itemInstanceId },
            Transaction,
            cancellationToken: cancellationToken))).ToArray();
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
            select
                definition.unit_weight as "UnitWeight",
                item.quantity as "Quantity"
            from item_instances item
            join item_definitions definition on definition.id = item.definition_id
            left join item_containers container on container.id = item.container_id
            left join item_instances bound_bag
              on bound_bag.id = container.bound_bag_item_instance_id
            left join character_item_states state on state.character_id = @CharacterId
            where item.container_id in (
                    state.permanent_inventory_container_id,
                    state.secure_container_id)
               or (
                    container.container_type = 'bag_contents'
                    and container.lifecycle = 'active'
                    and bound_bag.equipped_character_id = @CharacterId
                    and bound_bag.equipment_slot_id = 'bag')
            order by item.id;
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

    private sealed class DefinitionRow
    {
        public string Id { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public long UnitWeight { get; set; }

        public int MaximumStackSize { get; set; }

        public bool PlayerDestroyable { get; set; }

        public string StructuralFingerprint { get; set; } = string.Empty;

        public bool SecureContainerEligible { get; set; }

        public long? BagCarryCapacityBonus { get; set; }
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

    private sealed class LiveSimulationSessionRow
    {
        public Guid AccountId { get; set; }

        public Guid CharacterId { get; set; }

        public string ShardId { get; set; } = string.Empty;

        public string WorkerId { get; set; } = string.Empty;

        public string WorkerRuntimeId { get; set; } = string.Empty;

        public string SessionTokenHash { get; set; } = string.Empty;

        public bool IsActive { get; set; }
    }

    private sealed class LiveSimulationWorkerRow
    {
        public string? RuntimeId { get; set; }

        public bool IsOnline { get; set; }
    }
}

internal sealed record CharacterLockRequest(
    Guid CharacterId,
    long? ExpectedRevision);

internal sealed class LockedCharacterState
{
    public Guid CharacterId { get; set; }

    public Guid AccountId { get; set; }

    public long Revision { get; set; }

    public long CarriedWeight { get; set; }

    public long BaseCarryCapacity { get; set; }

    public long CarryCapacity { get; set; }

    public Guid PermanentInventoryContainerId { get; set; }

    public Guid BankContainerId { get; set; }

    public Guid SecureContainerId { get; set; }

    public Guid RecoveryStorageContainerId { get; set; }
}

internal sealed class LockedContainer
{
    public Guid ContainerId { get; set; }

    public string ContainerType { get; set; } = string.Empty;

    public Guid? DirectOwnerCharacterId { get; set; }

    public Guid? BoundBagItemInstanceId { get; set; }

    public Guid? OwningCharacterId { get; set; }

    public int? SlotCapacity { get; set; }

    public long Revision { get; set; }

    public string Lifecycle { get; set; } = string.Empty;
}

internal sealed class LockedItem
{
    public Guid ItemInstanceId { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public long Revision { get; set; }

    public Guid? ContainerId { get; set; }

    public int? ContainerSlotIndex { get; set; }

    public Guid? EquippedCharacterId { get; set; }

    public string? EquipmentSlotId { get; set; }

    public string? SourceContainerType { get; set; }

    public string? SourceContainerLifecycle { get; set; }

    public Guid? SourceBagItemInstanceId { get; set; }

    public Guid? OwningCharacterId { get; set; }

    public bool IsBag { get; set; }

    public long? BagCarryCapacityBonus { get; set; }

    public Guid? BagContentsContainerId { get; set; }

    public string? BagContentsLifecycle { get; set; }

    public long? BagContentsRevision { get; set; }
}

internal sealed class LockedItemPolicy
{
    public Guid PolicyId { get; set; }

    public string PolicyKind { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string SourceKind { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public long Revision { get; set; }
}

internal sealed record ResolvedItemDefinition(
    ItemDefinition RuntimeDefinition,
    bool IsBag);
