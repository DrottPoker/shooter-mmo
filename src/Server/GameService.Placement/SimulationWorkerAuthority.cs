using Dapper;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Placement;

internal sealed class SimulationWorkerAuthority(PlacementModuleOptions options)
    : ISimulationWorkerAuthority
{
    public async Task<SimulationWorkerRuntime?> GetRuntimeAsync(
        PostgresTransactionScope scope,
        string workerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var row = await scope.Connection.QuerySingleOrDefaultAsync<WorkerRuntimeRow>(
            new CommandDefinition(
                """
                select
                    worker.runtime_id as "RuntimeId",
                    worker.is_online as "IsOnline"
                from simulation_workers worker
                where worker.id = @WorkerId;
                """,
                new { WorkerId = workerId },
                scope.Transaction,
                cancellationToken: cancellationToken));
        return row is null ? null : new SimulationWorkerRuntime(row.RuntimeId, row.IsOnline);
    }

    public async Task<bool> LockAssignmentForShareAsync(
        PostgresTransactionScope scope,
        string workerId,
        string shardId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var assignmentId = await scope.Connection.QuerySingleOrDefaultAsync<string>(
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
                    WorkerId = workerId,
                    ShardId = shardId
                },
                scope.Transaction,
                cancellationToken: cancellationToken));
        return !string.IsNullOrWhiteSpace(assignmentId);
    }

    public async Task<SimulationWorkerShardLease?> GetShardLeaseAsync(
        PostgresTransactionScope scope,
        string workerId,
        string shardId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var row = await scope.Connection.QuerySingleOrDefaultAsync<WorkerShardLeaseRow>(
            new CommandDefinition(
                """
                select
                    worker.runtime_id as "RuntimeId",
                    worker.is_online
                        and worker.last_heartbeat_at is not null
                        and worker.last_heartbeat_at > now() - make_interval(secs => @HeartbeatTimeoutSeconds)
                        as "HasFreshLease",
                    exists (
                        select 1
                        from simulation_assignments assignment
                        where assignment.worker_id = worker.id
                          and assignment.shard_id = @ShardId
                          and assignment.released_at is null) as "HasAssignment"
                from simulation_workers worker
                where worker.id = @WorkerId;
                """,
                new
                {
                    WorkerId = workerId,
                    ShardId = shardId,
                    HeartbeatTimeoutSeconds = options.WorkerHeartbeatTimeout.TotalSeconds
                },
                scope.Transaction,
                cancellationToken: cancellationToken));
        return row is null
            ? null
            : new SimulationWorkerShardLease(row.RuntimeId, row.HasFreshLease, row.HasAssignment);
    }

    public async Task<SimulationWorkerLease?> LockLeaseAsync(
        PostgresTransactionScope scope,
        string workerId,
        int heartbeatTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var row = await scope.Connection.QuerySingleOrDefaultAsync<WorkerLeaseRow>(
            new CommandDefinition(
                """
                select
                    worker.runtime_id as "RuntimeId",
                    worker.is_online
                        and worker.last_heartbeat_at is not null
                        and worker.last_heartbeat_at > now()
                            - make_interval(secs => @HeartbeatTimeoutSeconds)
                        as "HasFreshLease"
                from simulation_workers worker
                where worker.id = @WorkerId
                for no key update of worker;
                """,
                new
                {
                    WorkerId = workerId,
                    HeartbeatTimeoutSeconds = heartbeatTimeoutSeconds
                },
                scope.Transaction,
                cancellationToken: cancellationToken));
        return row is null ? null : new SimulationWorkerLease(row.RuntimeId, row.HasFreshLease);
    }

    public Task<string?> LockAssignmentAsync(
        PostgresTransactionScope scope,
        string workerId,
        string shardId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return scope.Connection.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition(
                """
                select assignment.worker_id
                from simulation_assignments assignment
                where assignment.worker_id = @WorkerId
                  and assignment.shard_id = @ShardId
                  and assignment.released_at is null
                for no key update of assignment;
                """,
                new { WorkerId = workerId, ShardId = shardId },
                scope.Transaction,
                cancellationToken: cancellationToken));
    }

    private sealed class WorkerRuntimeRow
    {
        public string? RuntimeId { get; set; }

        public bool IsOnline { get; set; }
    }

    private sealed class WorkerLeaseRow
    {
        public string? RuntimeId { get; set; }

        public bool HasFreshLease { get; set; }
    }

    private sealed class WorkerShardLeaseRow
    {
        public string? RuntimeId { get; set; }

        public bool HasFreshLease { get; set; }

        public bool HasAssignment { get; set; }
    }
}
