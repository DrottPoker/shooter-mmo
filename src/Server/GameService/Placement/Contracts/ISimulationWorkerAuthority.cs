using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Placement.Contracts;

// A worker's registered runtime and whether the registry marks it online.
public sealed record SimulationWorkerRuntime(string? RuntimeId, bool IsOnline);

// A worker's registered runtime and whether its registry heartbeat is still fresh.
public sealed record SimulationWorkerLease(string? RuntimeId, bool HasFreshLease);

// A worker's lease together with whether it owns the Shard assignment.
public sealed record SimulationWorkerShardLease(
    string? RuntimeId,
    bool HasFreshLease,
    bool HasAssignment);

// Raw SimulationWorker authority for other modules, always in the caller's transaction.
// It returns stored facts, and the caller decides which check fails first.
public interface ISimulationWorkerAuthority
{
    // Reads the worker's registered runtime, or null when the worker does not exist.
    Task<SimulationWorkerRuntime?> GetRuntimeAsync(
        PostgresTransactionScope scope,
        string workerId,
        CancellationToken cancellationToken);

    // Locks the worker's active assignment to the Shard FOR SHARE. Returns false when
    // the worker does not own an active assignment to the Shard.
    Task<bool> LockAssignmentForShareAsync(
        PostgresTransactionScope scope,
        string workerId,
        string shardId,
        CancellationToken cancellationToken);

    // Reads the worker's lease with the placement heartbeat timeout and whether it owns
    // an active assignment to the Shard, or null when the worker does not exist.
    Task<SimulationWorkerShardLease?> GetShardLeaseAsync(
        PostgresTransactionScope scope,
        string workerId,
        string shardId,
        CancellationToken cancellationToken);

    // Locks the worker row FOR NO KEY UPDATE and judges its lease with the given
    // heartbeat timeout, or returns null when the worker does not exist.
    Task<SimulationWorkerLease?> LockLeaseAsync(
        PostgresTransactionScope scope,
        string workerId,
        int heartbeatTimeoutSeconds,
        CancellationToken cancellationToken);

    // Locks the worker's active assignment to the Shard FOR NO KEY UPDATE and returns
    // the owning worker id, or null when no such assignment exists.
    Task<string?> LockAssignmentAsync(
        PostgresTransactionScope scope,
        string workerId,
        string shardId,
        CancellationToken cancellationToken);
}
