using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Placement.Contracts;
using static Mmorpg.GameService.Items.ItemTransactionRejection;

namespace Mmorpg.GameService.Items.Transactions;

// Simulation worker authority checks against Characters and Placement contracts that
// run in the item transaction. Stateless; the check order, codes, and messages are part
// of the item API.
internal sealed class ItemSimulationAuthority(
    ICharacterLocks characterLocks,
    ISimulationSessionAuthority simulationSessions,
    ISimulationWorkerAuthority simulationWorkers)
{
    private readonly ICharacterLocks characterLocks = characterLocks;

    private readonly ISimulationSessionAuthority simulationSessions = simulationSessions;

    private readonly ISimulationWorkerAuthority simulationWorkers = simulationWorkers;

    // Kernel only: the live session, worker runtime, and Shard assignment of a
    // simulation worker actor, checked before the operation claim (S22 to S25).
    public async Task ValidateLiveSessionAsync(
        ItemTransactionSession transactionSession,
        Guid? actorCharacterId,
        CancellationToken cancellationToken)
    {
        if (transactionSession.Actor.Authority != ItemTransactionAuthority.SimulationWorker)
        {
            return;
        }

        var simulation = transactionSession.Actor.Simulation!;
        if (actorCharacterId is null
            || actorCharacterId == Guid.Empty
            || actorCharacterId != simulation.CharacterId)
        {
            Reject(
                ItemTransactionErrorCodes.SimulationSessionInvalid,
                "The item operation character does not match the simulation session.");
        }

        var characterAccountId = await characterLocks.LockActiveForItemMutationAsync(
            transactionSession.Scope,
            simulation.CharacterId,
            cancellationToken);
        if (characterAccountId is null || characterAccountId != transactionSession.Actor.AccountId)
        {
            Reject(
                ItemTransactionErrorCodes.ItemNotOwned,
                "The simulation account does not own the requested character.");
        }

        var session = await simulationSessions.LockForItemMutationAsync(
            transactionSession.Scope,
            simulation.SimulationSessionId,
            cancellationToken);
        if (session is null || !session.IsActive)
        {
            Reject(
                ItemTransactionErrorCodes.SimulationSessionInvalid,
                "The simulation session is invalid, released, or expired.");
        }

        if (session.AccountId != transactionSession.Actor.AccountId
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

        var worker = await simulationWorkers.GetRuntimeAsync(
            transactionSession.Scope,
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

        var ownsAssignment = await simulationWorkers.LockAssignmentForShareAsync(
            transactionSession.Scope,
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

    // Mob corpse creation: the worker's fresh registry lease and its Shard
    // assignment (S29).
    public async Task ValidateWorkerServiceLeaseAsync(
        ItemTransactionSession transactionSession,
        string workerId,
        string workerRuntimeId,
        string shardId,
        int heartbeatTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (transactionSession.Actor.Authority != ItemTransactionAuthority.System
            || !ItemActorAuthority.IsValidIdentifier(workerId)
            || !ItemActorAuthority.IsValidIdentifier(workerRuntimeId)
            || !ItemActorAuthority.IsValidIdentifier(shardId)
            || heartbeatTimeoutSeconds <= 0)
        {
            Reject(
                ItemTransactionErrorCodes.AuthorityRequired,
                "Mob corpse creation requires complete simulation worker service authority.");
        }

        var authority = await simulationWorkers.LockLeaseAsync(
            transactionSession.Scope,
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

        var assignmentOwner = await simulationWorkers.LockAssignmentAsync(
            transactionSession.Scope,
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
}
