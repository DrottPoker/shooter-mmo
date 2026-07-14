using AuthService.Config;
using Dapper;
using Npgsql;

namespace AuthService.Simulation;

public sealed class DevelopmentSimulationBotPlacementService(
    NpgsqlDataSource dataSource,
    AuthServiceConfig config)
{
    public async Task<DevelopmentSimulationBotPlacement?> SelectPlacementAsync(
        string shardId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select shard.id as "ShardId",
                   shard.world_id as "WorldId",
                   worker.id as "WorkerId",
                   worker.runtime_id as "RuntimeId",
                   worker.host as "Host",
                   worker.udp_port as "UdpPort",
                   worker.max_connections as "MaxConnections",
                   worker.active_connections as "ReportedActiveConnections",
                   session_usage.active_sessions as "ActiveDatabaseSessions",
                   ticket_usage.pending_tickets as "PendingDatabaseTickets",
                   worker.protocol_version as "ProtocolVersion",
                   worker.simulation_revision as "SimulationRevision",
                   worker.collision_revision as "CollisionRevision"
            from shards shard
            join fleets fleet
              on fleet.id = shard.fleet_id
             and fleet.is_enabled
            join simulation_assignments assignment
              on assignment.shard_id = shard.id
             and assignment.released_at is null
            join simulation_workers worker
              on worker.id = assignment.worker_id
             and worker.is_online
             and worker.last_heartbeat_at is not null
             and worker.last_heartbeat_at
                 + make_interval(secs => @HeartbeatTimeoutSeconds) > now()
            cross join lateral (
                select count(*)::integer as active_sessions
                from character_simulation_sessions simulation_session
                where simulation_session.simulation_worker_id = worker.id
                  and simulation_session.worker_runtime_id = worker.runtime_id
                  and simulation_session.released_at is null
                  and simulation_session.expires_at > now()
            ) session_usage
            cross join lateral (
                select count(*)::integer as pending_tickets
                from simulation_join_tickets pending_ticket
                where pending_ticket.simulation_worker_id = worker.id
                  and pending_ticket.worker_runtime_id = worker.runtime_id
                  and pending_ticket.consumed_at is null
                  and pending_ticket.expires_at > now()
            ) ticket_usage
            where shard.id = @ShardId
              and shard.is_enabled
            order by (
                greatest(worker.active_connections, session_usage.active_sessions)
                    + ticket_usage.pending_tickets
                )::numeric / greatest(worker.max_connections, 1) asc,
                worker.id asc
            limit 1;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<DevelopmentSimulationBotPlacement>(
            new CommandDefinition(
                sql,
                new
                {
                    ShardId = shardId.Trim(),
                    HeartbeatTimeoutSeconds =
                        (int)config.SimulationWorkerHeartbeatTimeout.TotalSeconds
                },
                cancellationToken: cancellationToken));
    }
}
