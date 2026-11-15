using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.Server.Common.Identity;
using Npgsql;
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
    // simulation worker actor, checked before the operation claim (S22 to S25). A session whose
    // account session ended runs only a command that may run after that end, the work of the
    // character's body; any other command is refused with the code of the end, last, so every
    // failure that ends the session wins over it.
    //
    // The checks keep their order, and statements share a round trip only where no check decides
    // between them: the character lock travels with the timeouts in the kernel's prelude, the
    // session lock runs alone because the character check decides whether it may run, and the
    // worker read travels with the assignment lock. A non-worker actor leaves the prelude to the
    // kernel.
    public async Task ValidateLiveSessionAsync(
        ItemTransactionSession transactionSession,
        PostgresBatch prelude,
        Guid? actorCharacterId,
        bool mayRunAfterAccountSessionEnd,
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

        var lockedCharacter = characterLocks.AddLockActiveForItemMutation(prelude, simulation.CharacterId);
        await prelude.ExecuteAsync(cancellationToken);
        var characterAccountId = lockedCharacter.Value;
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
        if (session is null || !session.IsLive)
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

        // The worker read only reads, and the assignment is the acting worker's own, so the lock
        // shares the round trip; a failed worker check still wins over the lock's result or error.
        var workerBatch = new PostgresBatch(transactionSession.Scope);
        var worker = simulationWorkers.AddGetRuntime(workerBatch, simulation.WorkerId);
        var ownsAssignment = simulationWorkers.AddLockAssignmentForShare(
            workerBatch,
            simulation.WorkerId,
            simulation.ShardId);
        try
        {
            await workerBatch.ExecuteAsync(cancellationToken);
        }
        catch (PostgresException) when (worker.HasValue)
        {
            EnsureWorkerRuntime(worker.Value, simulation);
            throw;
        }

        EnsureWorkerRuntime(worker.Value, simulation);
        if (!ownsAssignment.Value)
        {
            Reject(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The simulation worker no longer owns the requested Shard assignment.");
        }

        if (session.AccountSessionEnd is { } end && !mayRunAfterAccountSessionEnd)
        {
            Reject(
                SimulationAccountSessionEndCodes.Of(end),
                SimulationAccountSessionEndCodes.MessageOf(end));
        }
    }

    private static void EnsureWorkerRuntime(
        SimulationWorkerRuntime? worker,
        SimulationItemTransactionAuthority simulation)
    {
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
            || !Identifiers.IsServiceIdentifier(workerId)
            || !Identifiers.IsServiceIdentifier(workerRuntimeId)
            || !Identifiers.IsServiceIdentifier(shardId)
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
