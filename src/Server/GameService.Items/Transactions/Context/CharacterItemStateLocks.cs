using System.Diagnostics;
using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Placement.Contracts;
using static Mmorpg.GameService.Items.ItemTransactionRejection;

namespace Mmorpg.GameService.Items.Transactions;

// Character and character item state locks with their revision, ownership, and
// simulation character checks. Stateless; lock waits are recorded as character_state
// and character_event.
internal sealed class CharacterItemStateLocks(
    ICharacterLocks characterLocks,
    ISimulationPresence simulationPresence,
    ItemOperationsMetrics? operationsMetrics)
{
    private readonly ICharacterLocks characterLocks = characterLocks;

    private readonly ISimulationPresence simulationPresence = simulationPresence;

    private readonly ItemOperationsMetrics? operationsMetrics = operationsMetrics;

    // Locks the active characters through the Characters contract, then their item
    // states, each in ascending id order (S26), and checks offline access (S27).
    public async Task<IReadOnlyDictionary<Guid, LockedCharacterState>> LockAsync(
        ItemTransactionSession session,
        ItemActorAuthority authority,
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
            var accountIds = await characterLocks.LockActiveForItemMutationAsync(
                session.Scope,
                requested.Select(request => request.CharacterId).ToArray(),
                cancellationToken);
            rows = (await session.Connection.QueryAsync<LockedCharacterState>(new CommandDefinition(
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
                session.Transaction,
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
            authority.EnsureAccountAuthority(row.AccountId);
            var expected = expectedByCharacter[row.CharacterId].ExpectedRevision;
            if (session.Actor.Authority != ItemTransactionAuthority.System && expected is null)
            {
                Reject(
                    ItemTransactionErrorCodes.ItemStateConflict,
                    session.Actor.Authority == ItemTransactionAuthority.SimulationWorker
                        ? "Simulation-authorized item operations require an expected character revision."
                        : "Account-authorized item operations require an expected character revision.");
            }

            if (expected is not null && row.Revision != expected.Value)
            {
                Reject(
                    ItemTransactionErrorCodes.ItemStateConflict,
                    $"Character item state '{row.CharacterId}' changed before the operation committed.");
            }

            if (session.Actor.Authority == ItemTransactionAuthority.SimulationWorker
                && session.Actor.Simulation!.CharacterId != row.CharacterId)
            {
                Reject(
                    ItemTransactionErrorCodes.SimulationSessionInvalid,
                    "The item state does not belong to the active simulation character.");
            }
        }

        if (session.Actor.RequiresOfflineCharacter)
        {
            var activeCharacterId = await simulationPresence.FindActiveCharacterAsync(
                session.Scope,
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

    // Locks one character and its item state for an idempotent event such as death or a
    // corpse interaction (S28).
    public async Task<LockedCharacterState> LockForEventAsync(
        ItemTransactionSession session,
        ItemActorAuthority authority,
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
            var accountId = await characterLocks.LockActiveForItemMutationAsync(
                session.Scope,
                characterId,
                cancellationToken);
            if (accountId is not null)
            {
                row = await session.Connection.QuerySingleOrDefaultAsync<LockedCharacterState>(
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
                        session.Transaction,
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

        authority.EnsureAccountAuthority(row.AccountId);
        if (session.Actor.Authority == ItemTransactionAuthority.SimulationWorker
            && session.Actor.Simulation!.CharacterId != row.CharacterId)
        {
            Reject(
                ItemTransactionErrorCodes.SimulationSessionInvalid,
                "The item state does not belong to the active simulation character.");
        }

        return row;
    }
}
