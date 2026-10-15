using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Placement;

public sealed class SimulationSessionAuthority(PlacementModuleOptions options)
    : ISimulationSessionAuthority
{
    public async Task<LiveSimulationSession?> LockForItemMutationAsync(
        PostgresTransactionScope scope,
        Guid simulationSessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var row = await scope.Connection.QuerySingleOrDefaultAsync<LiveSimulationSessionRow>(
            new CommandDefinition(
                $"""
                select
                    simulation_session.account_id as "AccountId",
                    simulation_session.character_id as "CharacterId",
                    simulation_session.shard_id as "ShardId",
                    simulation_session.simulation_worker_id as "WorkerId",
                    simulation_session.worker_runtime_id as "WorkerRuntimeId",
                    simulation_session.session_token_hash as "SessionTokenHash",
                    simulation_session.released_at is null
                        and simulation_session.expires_at > now()
                        and {AccountsSql.IsActiveOrUnboundSession("simulation_session.account_session_id")} as "IsActive"
                from character_simulation_sessions simulation_session
                where simulation_session.id = @SimulationSessionId
                for update of simulation_session;
                """,
                new { SimulationSessionId = simulationSessionId },
                scope.Transaction,
                cancellationToken: cancellationToken));
        return row is null
            ? null
            : new LiveSimulationSession(
                row.AccountId,
                row.CharacterId,
                row.ShardId,
                row.WorkerId,
                row.WorkerRuntimeId,
                row.SessionTokenHash,
                row.IsActive);
    }

    public async Task<SimulationSessionReadAuthority?> GetReadAuthorityAsync(
        PostgresTransactionScope scope,
        Guid simulationSessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var row = await scope.Connection.QuerySingleOrDefaultAsync<ReadAuthorityRow>(
            new CommandDefinition(
                $"""
                select
                    session.account_id as "AccountId",
                    session.character_id as "CharacterId",
                    session.shard_id as "ShardId",
                    session.simulation_worker_id as "WorkerId",
                    session.worker_runtime_id as "WorkerRuntimeId",
                    session.session_token_hash as "SessionTokenHash",
                    session.released_at is null
                        and session.expires_at > now()
                        and {AccountsSql.IsActiveOrUnboundSession("session.account_session_id")} as "SessionIsActive",
                    worker.is_online
                        and worker.last_heartbeat_at is not null
                        and worker.last_heartbeat_at > now()
                            - make_interval(secs => @HeartbeatTimeoutSeconds)
                        and worker.runtime_id = session.worker_runtime_id
                        as "WorkerHasFreshLease",
                    exists (
                        select 1
                        from simulation_assignments assignment
                        where assignment.worker_id = session.simulation_worker_id
                          and assignment.shard_id = session.shard_id
                          and assignment.released_at is null) as "WorkerHasAssignment"
                from character_simulation_sessions session
                join simulation_workers worker
                  on worker.id = session.simulation_worker_id
                where session.id = @SimulationSessionId;
                """,
                new
                {
                    SimulationSessionId = simulationSessionId,
                    HeartbeatTimeoutSeconds = options.WorkerHeartbeatTimeout.TotalSeconds
                },
                scope.Transaction,
                cancellationToken: cancellationToken));
        return row is null
            ? null
            : new SimulationSessionReadAuthority(
                row.AccountId,
                row.CharacterId,
                row.ShardId,
                row.WorkerId,
                row.WorkerRuntimeId,
                row.SessionTokenHash,
                row.SessionIsActive,
                row.WorkerHasFreshLease,
                row.WorkerHasAssignment);
    }

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

    private sealed class ReadAuthorityRow
    {
        public Guid AccountId { get; set; }

        public Guid CharacterId { get; set; }

        public string ShardId { get; set; } = string.Empty;

        public string WorkerId { get; set; } = string.Empty;

        public string WorkerRuntimeId { get; set; } = string.Empty;

        public string SessionTokenHash { get; set; } = string.Empty;

        public bool SessionIsActive { get; set; }

        public bool WorkerHasFreshLease { get; set; }

        public bool WorkerHasAssignment { get; set; }
    }
}
