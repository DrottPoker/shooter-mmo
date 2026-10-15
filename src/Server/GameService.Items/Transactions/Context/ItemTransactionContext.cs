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
        Journal = new ItemChangeJournal(session, State);
        Placement = new ItemSlotPlacement(session);
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

    public ItemChangeJournal Journal { get; }

    public ItemSlotPlacement Placement { get; }

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

    [DoesNotReturn]
    public static void Reject(string code, string message)
    {
        ItemTransactionRejection.Reject(code, message);
    }

    private sealed class LockDiscoveryRow
    {
        public Guid ItemInstanceId { get; set; }

        public Guid? ContainerId { get; set; }

        public Guid? ItemBagRootId { get; set; }

        public Guid? SourceBagRootId { get; set; }

        public Guid? BagContentsContainerId { get; set; }
    }
}
