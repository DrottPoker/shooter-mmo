using Dapper;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.Server.Common.Identity;
using Npgsql;

namespace Mmorpg.GameService.Placement;

internal sealed class SimulationWorkerRegistryService(
    NpgsqlDataSource dataSource,
    PlacementModuleOptions options,
    IShardWorldLocalStateProbe shardWorldLocalStateProbe)
{
    private static readonly TimeSpan MaximumStartedAtClockSkew = TimeSpan.FromMinutes(10);

    private const string WorkerHeartbeatReturningSql = """

        returning id as "WorkerId",
                  runtime_id as "RuntimeId",
                  @FleetId as "FleetId",
                  node_id as "NodeId",
                  @ShardId as "ShardId",
                  @WorldId as "WorldId",
                  @RuleSet as "RuleSet",
                  host as "Host",
                  udp_port as "UdpPort",
                  max_connections as "MaxConnections",
                  active_connections as "ActiveConnections",
                  protocol_version as "ProtocolVersion",
                  simulation_revision as "SimulationRevision",
                  collision_revision as "CollisionRevision",
                  item_catalog_revision as "ItemCatalogRevision",
                  quest_catalog_revision as "QuestCatalogRevision",
                  last_heartbeat_at as "LastHeartbeatAt",
                  last_heartbeat_at + make_interval(secs => @HeartbeatTimeoutSeconds) as "OnlineUntil";
        """;

    public async Task<ServiceResult<SimulationWorkerHeartbeatResponse>> HeartbeatAsync(
        string workerId,
        SimulationWorkerHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        var validationError = Validate(workerId, request);
        if (validationError is not null)
        {
            return ServiceResult<SimulationWorkerHeartbeatResponse>.BadRequest(
                validationError.Value.Code,
                validationError.Value.Message);
        }

        var normalizedWorkerId = workerId.Trim();
        var normalizedFleetId = request.FleetId!.Trim();
        var normalizedNodeId = request.NodeId!.Trim();
        var normalizedShardId = request.ShardId!.Trim();
        var normalizedWorldId = request.WorldId!.Trim();
        var normalizedRuntimeId = request.RuntimeId!.Trim();

        var renewed = await TryRenewAsync(
            normalizedWorkerId,
            normalizedFleetId,
            normalizedNodeId,
            normalizedShardId,
            normalizedWorldId,
            request,
            normalizedRuntimeId,
            cancellationToken);
        if (renewed is not null)
        {
            return ServiceResult<SimulationWorkerHeartbeatResponse>.Ok(renewed);
        }

        // The registration: a first heartbeat, a new runtime, a takeover of the Shard, or
        // another World. It changes who owns the Shard, so it holds the Shard row and the
        // assignment exclusively. It locks the assignment before the workers it changes, against
        // the lock order; it runs once per worker start, and when it deadlocks with an operation
        // of the runtime it replaces, PostgreSQL ends one of them and both sides try again.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var topology = await GetTopologyAsync(
            connection,
            transaction,
            normalizedNodeId,
            normalizedShardId,
            normalizedWorldId,
            cancellationToken);
        if (topology is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<SimulationWorkerHeartbeatResponse>.NotFound(
                "simulation_assignment_target_not_found",
                "The configured simulation node or shard was not found or is disabled.");
        }

        if (!topology.WorldExists)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<SimulationWorkerHeartbeatResponse>.NotFound(
                "simulation_world_not_found",
                $"World {normalizedWorldId} is not registered in the authoritative World database.");
        }

        if (!string.Equals(topology.NodeFleetId, normalizedFleetId, StringComparison.Ordinal)
            || !string.Equals(topology.ShardFleetId, normalizedFleetId, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<SimulationWorkerHeartbeatResponse>.Conflict(
                "simulation_topology_mismatch",
                "The simulation worker, node, and shard must belong to the same fleet.");
        }

        var assignedWorker = await GetAssignedWorkerAsync(
            connection,
            transaction,
            normalizedShardId,
            (int)options.WorkerHeartbeatTimeout.TotalSeconds,
            cancellationToken);
        var worldChanged = !string.Equals(
            topology.CurrentWorldId,
            normalizedWorldId,
            StringComparison.Ordinal);
        if (assignedWorker is not null
            && assignedWorker.IsHealthy
            && (!string.Equals(
                    assignedWorker.WorkerId,
                    normalizedWorkerId,
                    StringComparison.Ordinal)
                || worldChanged))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<SimulationWorkerHeartbeatResponse>.Conflict(
                "shard_assignment_conflict",
                $"Shard {normalizedShardId} already has an active simulation worker assignment.");
        }

        if (worldChanged && topology.CurrentWorldId is not null)
        {
            // Another World needs the Shard without anything that refers to it. FOR UPDATE
            // makes every foreign key insert of a row of the Shard wait, a join ticket, a
            // session, or a corpse, so the counts of the blockers stay exact. It comes right
            // after the assignment lock and before any other change: an item operation locks
            // the assignment before it inserts a corpse, and a ticket redeem that holds the
            // foreign key lock waits for nothing that this transaction holds yet.
            await connection.ExecuteAsync(new CommandDefinition(
                "select 1 from shards where id = @ShardId for update;",
                new { ShardId = normalizedShardId },
                transaction,
                cancellationToken: cancellationToken));
        }

        if (assignedWorker is not null
            && (!string.Equals(
                    assignedWorker.WorkerId,
                    normalizedWorkerId,
                    StringComparison.Ordinal)
                || worldChanged))
        {
            await ReleaseWorkerRuntimeAsync(
                connection,
                transaction,
                assignedWorker.WorkerId,
                assignedWorker.RuntimeId,
                releaseAssignment: true,
                cancellationToken);
        }

        if (worldChanged && topology.CurrentWorldId is not null)
        {
            var blockers = await LoadWorldRebindBlockersAsync(
                connection,
                transaction,
                normalizedShardId,
                cancellationToken);
            if (blockers.HasAny)
            {
                await transaction.RollbackAsync(cancellationToken);
                var blocked = new ShardWorldRebindBlockedException(
                    normalizedShardId,
                    topology.CurrentWorldId,
                    normalizedWorldId,
                    blockers.ActiveAssignments,
                    blockers.PendingJoinTickets,
                    blockers.ActiveSimulationSessions,
                    blockers.OpenCorpses);
                return ServiceResult<SimulationWorkerHeartbeatResponse>.Conflict(
                    "shard_world_rebind_blocked",
                    blocked.Message);
            }
        }

        if (worldChanged)
        {
            await UpdateShardWorldAsync(
                connection,
                transaction,
                normalizedShardId,
                normalizedWorldId,
                cancellationToken);
        }

        var heartbeat = await UpsertWorkerAsync(
            connection,
            transaction,
            normalizedWorkerId,
            normalizedNodeId,
            normalizedShardId,
            normalizedWorldId,
            topology.RuleSet,
            request,
            normalizedRuntimeId,
            cancellationToken);
        if (heartbeat is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<SimulationWorkerHeartbeatResponse>.Conflict(
                "worker_runtime_changed",
                "Another live runtime currently owns this simulation worker identity.");
        }

        await ReleaseSupersededRuntimeAccessAsync(
            connection,
            transaction,
            normalizedWorkerId,
            normalizedRuntimeId,
            cancellationToken);

        await ReplaceAssignmentAsync(
            connection,
            transaction,
            normalizedWorkerId,
            normalizedShardId,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<SimulationWorkerHeartbeatResponse>.Ok(heartbeat);
    }

    public async Task<ServiceResult<SimulationWorkerOfflineResponse>> MarkOfflineAsync(
        string workerId,
        SimulationWorkerOfflineRequest request,
        CancellationToken cancellationToken)
    {
        if (!Identifiers.IsServiceIdentifier(workerId) || !Identifiers.IsServiceIdentifier(request.RuntimeId))
        {
            return ServiceResult<SimulationWorkerOfflineResponse>.BadRequest(
                "invalid_worker_runtime",
                "Simulation worker id and runtime id are required and must be valid identifiers.");
        }

        const string sql = """
            update simulation_workers
            set is_online = false,
                updated_at = now()
            where id = @WorkerId
              and runtime_id = @RuntimeId
            returning id as "WorkerId",
                      runtime_id as "RuntimeId",
                      updated_at as "OfflineAt";
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAssignedShardAsync(
            connection,
            transaction,
            workerId.Trim(),
            request.RuntimeId!.Trim(),
            cancellationToken);
        var offline = await connection.QuerySingleOrDefaultAsync<SimulationWorkerOfflineResponse>(
            new CommandDefinition(
                sql,
                new
                {
                    WorkerId = workerId.Trim(),
                    RuntimeId = request.RuntimeId!.Trim()
                },
                transaction,
                cancellationToken: cancellationToken));
        if (offline is not null)
        {
            await ReleaseWorkerRuntimeAsync(
                connection,
                transaction,
                offline.WorkerId,
                offline.RuntimeId,
                releaseAssignment: true,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<SimulationWorkerOfflineResponse>.Ok(offline);
        }

        await transaction.RollbackAsync(cancellationToken);

        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from simulation_workers where id = @WorkerId);",
            new { WorkerId = workerId.Trim() },
            cancellationToken: cancellationToken));

        return exists
            ? ServiceResult<SimulationWorkerOfflineResponse>.Conflict(
                "worker_runtime_changed",
                "A different runtime currently owns this simulation worker identity.")
            : ServiceResult<SimulationWorkerOfflineResponse>.NotFound(
                "simulation_worker_not_found",
                "Simulation worker was not found.");
    }

    private async Task<SimulationWorkerHeartbeatResponse?> UpsertWorkerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workerId,
        string nodeId,
        string shardId,
        string worldId,
        string ruleSet,
        SimulationWorkerHeartbeatRequest request,
        string runtimeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into simulation_workers (
                id,
                node_id,
                runtime_id,
                started_at,
                host,
                udp_port,
                max_connections,
                active_connections,
                protocol_version,
                simulation_revision,
                collision_revision,
                item_catalog_revision,
                quest_catalog_revision,
                is_online,
                last_heartbeat_at,
                updated_at)
            values (
                @WorkerId,
                @NodeId,
                @RuntimeId,
                @StartedAt,
                @Host,
                @UdpPort,
                @MaxConnections,
                @ActiveConnections,
                @ProtocolVersion,
                @SimulationRevision,
                @CollisionRevision,
                @ItemCatalogRevision,
                @QuestCatalogRevision,
                true,
                now(),
                now())
            on conflict (id) do update
            set node_id = excluded.node_id,
                runtime_id = excluded.runtime_id,
                started_at = case
                    when simulation_workers.runtime_id = excluded.runtime_id
                    then simulation_workers.started_at
                    else excluded.started_at
                end,
                host = excluded.host,
                udp_port = excluded.udp_port,
                max_connections = excluded.max_connections,
                active_connections = excluded.active_connections,
                protocol_version = excluded.protocol_version,
                simulation_revision = excluded.simulation_revision,
                collision_revision = excluded.collision_revision,
                item_catalog_revision = excluded.item_catalog_revision,
                quest_catalog_revision = excluded.quest_catalog_revision,
                is_online = true,
                last_heartbeat_at = now(),
                updated_at = now()
            -- A runtime that is offline or no longer heartbeating can always be replaced.
            -- Reported start times only order two live runtimes, because host clocks differ.
            where simulation_workers.runtime_id is null
               or simulation_workers.runtime_id = excluded.runtime_id
               or simulation_workers.is_online = false
               or simulation_workers.last_heartbeat_at
                    + make_interval(secs => @HeartbeatTimeoutSeconds) <= now()
               or simulation_workers.started_at < excluded.started_at
            """ + WorkerHeartbeatReturningSql;

        return await connection.QuerySingleOrDefaultAsync<SimulationWorkerHeartbeatResponse>(
            new CommandDefinition(
                sql,
                WorkerParameters(workerId, nodeId, shardId, worldId, ruleSet, request, runtimeId),
                transaction,
                cancellationToken: cancellationToken));
    }

    private object WorkerParameters(
        string workerId,
        string nodeId,
        string shardId,
        string worldId,
        string ruleSet,
        SimulationWorkerHeartbeatRequest request,
        string runtimeId)
    {
        return new
        {
            WorkerId = workerId,
            NodeId = nodeId,
            RuntimeId = runtimeId,
            request.StartedAt,
            Host = request.Host!.Trim(),
            request.UdpPort,
            request.MaxConnections,
            request.ActiveConnections,
            request.ProtocolVersion,
            SimulationRevision = request.SimulationRevision!.Trim(),
            CollisionRevision = request.CollisionRevision!.Trim(),
            ItemCatalogRevision = request.ItemCatalogRevision!.Trim(),
            QuestCatalogRevision = request.QuestCatalogRevision!.Trim(),
            FleetId = request.FleetId!.Trim(),
            ShardId = shardId,
            WorldId = worldId,
            RuleSet = ruleSet,
            HeartbeatTimeoutSeconds = (int)options.WorkerHeartbeatTimeout.TotalSeconds
        };
    }

    // The renewal of a heartbeat that changes nothing but the lease: the same runtime of the
    // worker, on the Shard it is assigned to, with the World the Shard is bound to. It runs every
    // few seconds beside the players' operations, so it takes only locks that they never wait
    // for and that never wait for them: the Shard row FOR KEY SHARE, which a foreign key insert
    // takes too, the worker row through its update, and the assignment FOR SHARE, as every
    // item operation does. It follows the lock order, workers before assignments. Null means
    // that something differs, and the registration below decides what.
    private async Task<SimulationWorkerHeartbeatResponse?> TryRenewAsync(
        string workerId,
        string fleetId,
        string nodeId,
        string shardId,
        string worldId,
        SimulationWorkerHeartbeatRequest request,
        string runtimeId,
        CancellationToken cancellationToken)
    {
        const string topologySql = """
            select shard.rule_set
            from simulation_nodes node
            cross join shards shard
            join fleets fleet on fleet.id = shard.fleet_id and fleet.is_enabled
            where node.id = @NodeId
              and node.is_enabled
              and node.fleet_id = @FleetId
              and shard.id = @ShardId
              and shard.is_enabled
              and shard.fleet_id = @FleetId
              and shard.world_id = @WorldId
              and exists (
                  select 1
                  from world_definitions world
                  where world.id = @WorldId)
            for key share of shard;
            """;

        const string renewSql = """
            update simulation_workers
            set node_id = @NodeId,
                host = @Host,
                udp_port = @UdpPort,
                max_connections = @MaxConnections,
                active_connections = @ActiveConnections,
                protocol_version = @ProtocolVersion,
                simulation_revision = @SimulationRevision,
                collision_revision = @CollisionRevision,
                item_catalog_revision = @ItemCatalogRevision,
                quest_catalog_revision = @QuestCatalogRevision,
                last_heartbeat_at = now(),
                updated_at = now()
            where id = @WorkerId
              and runtime_id = @RuntimeId
              and is_online
            """ + WorkerHeartbeatReturningSql;

        const string assignmentSql = """
            select assignment.id
            from simulation_assignments assignment
            where assignment.shard_id = @ShardId
              and assignment.worker_id = @WorkerId
              and assignment.released_at is null
            for share of assignment;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var ruleSet = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            topologySql,
            new { NodeId = nodeId, FleetId = fleetId, ShardId = shardId, WorldId = worldId },
            transaction,
            cancellationToken: cancellationToken));
        if (ruleSet is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var heartbeat = await connection.QuerySingleOrDefaultAsync<SimulationWorkerHeartbeatResponse>(
            new CommandDefinition(
                renewSql,
                WorkerParameters(workerId, nodeId, shardId, worldId, ruleSet, request, runtimeId),
                transaction,
                cancellationToken: cancellationToken));
        if (heartbeat is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var assignmentId = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            assignmentSql,
            new { ShardId = shardId, WorkerId = workerId },
            transaction,
            cancellationToken: cancellationToken));
        if (assignmentId is null)
        {
            // The rollback takes the renewal back; the registration writes the worker anew.
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return heartbeat;
    }

    private static async Task LockAssignedShardAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workerId,
        string runtimeId,
        CancellationToken cancellationToken)
    {
        await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            """
            select shard.id
            from simulation_workers worker
            join simulation_assignments assignment
              on assignment.worker_id = worker.id
             and assignment.released_at is null
            join shards shard on shard.id = assignment.shard_id
            where worker.id = @WorkerId
              and worker.runtime_id = @RuntimeId
            for no key update of shard;
            """,
            new { WorkerId = workerId, RuntimeId = runtimeId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<TopologyRow?> GetTopologyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string nodeId,
        string shardId,
        string worldId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select node.fleet_id as "NodeFleetId",
                   shard.fleet_id as "ShardFleetId",
                   shard.world_id as "CurrentWorldId",
                   shard.rule_set as "RuleSet",
                   exists (
                       select 1
                       from world_definitions world
                       where world.id = @WorldId
                   ) as "WorldExists"
            from simulation_nodes node
            cross join shards shard
            join fleets fleet on fleet.id = shard.fleet_id and fleet.is_enabled
            where node.id = @NodeId
              and node.is_enabled
              and shard.id = @ShardId
              and shard.is_enabled
            for no key update of shard;
            """;

        return await connection.QuerySingleOrDefaultAsync<TopologyRow>(new CommandDefinition(
            sql,
            new { NodeId = nodeId, ShardId = shardId, WorldId = worldId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private async Task<ShardWorldRebindBlockers> LoadWorldRebindBlockersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string shardId,
        CancellationToken cancellationToken)
    {
        var placementBlockers = await connection.QuerySingleAsync<PlacementRebindBlockers>(
            new CommandDefinition(
            """
            select
                (
                    select count(*)
                    from simulation_assignments assignment
                    where assignment.shard_id = @ShardId
                      and assignment.released_at is null
                ) as "ActiveAssignments",
                (
                    select count(*)
                    from simulation_join_tickets
                    where shard_id = @ShardId
                      and consumed_at is null
                      and expires_at > now()
                ) as "PendingJoinTickets",
                (
                    select count(*)
                    from character_simulation_sessions
                    where shard_id = @ShardId
                      and released_at is null
                      and expires_at > now()
                ) as "ActiveSimulationSessions";
            """,
            new { ShardId = shardId },
            transaction,
            cancellationToken: cancellationToken));

        // The caller holds the Shard row FOR UPDATE, which blocks new corpses for the Shard,
        // so the separate count stays exact.
        var openCorpses = await shardWorldLocalStateProbe.CountOpenDurableCorpsesAsync(
            new PostgresTransactionScope(connection, transaction),
            shardId,
            cancellationToken);
        return new ShardWorldRebindBlockers(
            placementBlockers.ActiveAssignments,
            placementBlockers.PendingJoinTickets,
            placementBlockers.ActiveSimulationSessions,
            openCorpses);
    }

    private static Task UpdateShardWorldAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string shardId,
        string worldId,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(new CommandDefinition(
            """
            update shards
            set world_id = @WorldId,
                updated_at = now()
            where id = @ShardId;
            """,
            new { ShardId = shardId, WorldId = worldId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<AssignedWorkerRow?> GetAssignedWorkerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string shardId,
        int heartbeatTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        return await connection.QuerySingleOrDefaultAsync<AssignedWorkerRow>(new CommandDefinition(
            """
            select assignment.worker_id as "WorkerId",
                   worker.runtime_id as "RuntimeId",
                   coalesce(
                       worker.is_online
                       and worker.last_heartbeat_at is not null
                       and worker.last_heartbeat_at
                           + make_interval(secs => @HeartbeatTimeoutSeconds) > now(),
                       false) as "IsHealthy"
            from simulation_assignments assignment
            left join simulation_workers worker on worker.id = assignment.worker_id
            where assignment.shard_id = @ShardId
              and assignment.released_at is null
            for update of assignment;
            """,
            new { ShardId = shardId, HeartbeatTimeoutSeconds = heartbeatTimeoutSeconds },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task ReleaseWorkerRuntimeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workerId,
        string? runtimeId,
        bool releaseAssignment,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(runtimeId))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                update simulation_join_tickets
                set consumed_at = now()
                where simulation_worker_id = @WorkerId
                  and worker_runtime_id = @RuntimeId
                  and consumed_at is null;

                update character_simulation_sessions
                set released_at = now()
                where simulation_worker_id = @WorkerId
                  and worker_runtime_id = @RuntimeId
                  and released_at is null;

                update simulation_workers
                set is_online = false,
                    updated_at = now()
                where id = @WorkerId
                  and runtime_id = @RuntimeId;
                """,
                new { WorkerId = workerId, RuntimeId = runtimeId },
                transaction,
                cancellationToken: cancellationToken));
        }

        if (releaseAssignment)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                update simulation_assignments
                set released_at = now()
                where worker_id = @WorkerId
                  and released_at is null;
                """,
                new { WorkerId = workerId },
                transaction,
                cancellationToken: cancellationToken));
        }
    }

    private static async Task ReleaseSupersededRuntimeAccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workerId,
        string currentRuntimeId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update simulation_join_tickets
            set consumed_at = now()
            where simulation_worker_id = @WorkerId
              and worker_runtime_id is distinct from @CurrentRuntimeId
              and consumed_at is null;

            update character_simulation_sessions
            set released_at = now()
            where simulation_worker_id = @WorkerId
              and worker_runtime_id is distinct from @CurrentRuntimeId
              and released_at is null;
            """,
            new { WorkerId = workerId, CurrentRuntimeId = currentRuntimeId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task ReplaceAssignmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workerId,
        string shardId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update simulation_assignments
            set released_at = now()
            where worker_id = @WorkerId
              and shard_id <> @ShardId
              and released_at is null;
            """,
            new { WorkerId = workerId, ShardId = shardId },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into simulation_assignments (id, worker_id, shard_id)
            select @AssignmentId, @WorkerId, @ShardId
            where not exists (
                select 1
                from simulation_assignments
                where worker_id = @WorkerId
                  and shard_id = @ShardId
                  and released_at is null);
            """,
            new
            {
                AssignmentId = $"assignment-{Guid.NewGuid():N}",
                WorkerId = workerId,
                ShardId = shardId
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static (string Code, string Message)? Validate(
        string workerId,
        SimulationWorkerHeartbeatRequest request)
    {
        if (!Identifiers.IsServiceIdentifier(workerId)
            || !Identifiers.IsServiceIdentifier(request.FleetId)
            || !Identifiers.IsServiceIdentifier(request.NodeId)
            || !Identifiers.IsServiceIdentifier(request.ShardId)
            || !Identifiers.IsServiceIdentifier(request.WorldId)
            || !Identifiers.IsServiceIdentifier(request.RuntimeId))
        {
            return (
                "invalid_simulation_worker_identity",
                "Fleet, node, shard, World, worker, and runtime ids must be valid identifiers.");
        }

        if (request.StartedAt == default || request.StartedAt.Kind != DateTimeKind.Utc)
        {
            return ("invalid_worker_started_at", "Simulation worker start time must be UTC.");
        }

        // A start time far in the future would let one misconfigured host fence every other.
        if (request.StartedAt > DateTime.UtcNow.Add(MaximumStartedAtClockSkew))
        {
            return ("invalid_worker_started_at", "Simulation worker start time is in the future.");
        }

        if (string.IsNullOrWhiteSpace(request.Host)
            || request.Host.Length > 253
            || request.Host.Any(char.IsWhiteSpace)
            || request.Host.Contains('/', StringComparison.Ordinal)
            || request.Host.Contains('\\', StringComparison.Ordinal))
        {
            return ("invalid_worker_host", "Simulation worker host is invalid.");
        }

        if (request.UdpPort is <= 0 or > 65535)
        {
            return ("invalid_worker_port", "Simulation worker UDP port is invalid.");
        }

        if (request.MaxConnections is <= 0 or > 10_000
            || request.ActiveConnections < 0
            || request.ActiveConnections > request.MaxConnections)
        {
            return (
                "invalid_worker_capacity",
                "Simulation worker connection capacity is invalid.");
        }

        if (request.ProtocolVersion is <= 0 or > ushort.MaxValue)
        {
            return ("invalid_protocol_version", "Realtime protocol version is invalid.");
        }

        if (!IsValidRevision(request.SimulationRevision)
            || !IsValidRevision(request.CollisionRevision)
            || !IsValidRevision(request.ItemCatalogRevision)
            || !IsValidRevision(request.QuestCatalogRevision))
        {
            return ("invalid_worker_revision", "Simulation worker revision metadata is invalid.");
        }

        return null;
    }

    private static bool IsValidRevision(string? revision)
    {
        return !string.IsNullOrWhiteSpace(revision)
            && revision.Trim().Length <= 128
            && !revision.Any(char.IsWhiteSpace);
    }

    private sealed record TopologyRow(
        string NodeFleetId,
        string ShardFleetId,
        string? CurrentWorldId,
        string RuleSet,
        bool WorldExists);

    private sealed record AssignedWorkerRow(string WorkerId, string? RuntimeId, bool IsHealthy);

    private sealed record PlacementRebindBlockers(
        long ActiveAssignments,
        long PendingJoinTickets,
        long ActiveSimulationSessions);

    private sealed record ShardWorldRebindBlockers(
        long ActiveAssignments,
        long PendingJoinTickets,
        long ActiveSimulationSessions,
        long OpenCorpses)
    {
        public bool HasAny => ActiveAssignments > 0
            || PendingJoinTickets > 0
            || ActiveSimulationSessions > 0
            || OpenCorpses > 0;
    }
}
