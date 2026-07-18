using AuthService.Config;
using Dapper;
using Npgsql;

namespace AuthService.Simulation;

public sealed class SimulationTopologySeeder(
    NpgsqlDataSource dataSource,
    AuthServiceConfig config,
    ILogger<SimulationTopologySeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var world in config.SimulationTopology.Worlds)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into world_definitions (id, display_name)
                values (@Id, @DisplayName)
                on conflict (id) do update
                set display_name = excluded.display_name,
                    updated_at = now();
                """,
                world,
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var fleet in config.SimulationTopology.Fleets)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into fleets (id, display_name, region_code)
                values (@Id, @DisplayName, @RegionCode)
                on conflict (id) do update
                set display_name = excluded.display_name,
                    region_code = excluded.region_code,
                    updated_at = now();
                """,
                fleet,
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var node in config.SimulationTopology.Nodes)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into simulation_nodes (id, fleet_id, display_name)
                values (@Id, @FleetId, @DisplayName)
                on conflict (id) do update
                set fleet_id = excluded.fleet_id,
                    display_name = excluded.display_name,
                    updated_at = now();
                """,
                node,
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var shard in config.SimulationTopology.Shards)
        {
            await ReconcileShardAsync(
                connection,
                transaction,
                shard,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Simulation topology bootstrap ensured {FleetCount} fleets, {NodeCount} nodes, {WorldCount} worlds, and {ShardCount} shards.",
            config.SimulationTopology.Fleets.Count,
            config.SimulationTopology.Nodes.Count,
            config.SimulationTopology.Worlds.Count,
            config.SimulationTopology.Shards.Count);
    }

    private async Task ReconcileShardAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ShardBootstrapConfig shard,
        CancellationToken cancellationToken)
    {
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into shards (
                id,
                display_name,
                world_id,
                fleet_id,
                rule_set,
                is_enabled)
            values (
                @Id,
                @DisplayName,
                @WorldId,
                @FleetId,
                @RuleSet,
                true)
            on conflict (id) do nothing;
            """,
            shard,
            transaction,
            cancellationToken: cancellationToken));
        if (inserted == 1)
        {
            return;
        }

        var current = await connection.QuerySingleAsync<ShardTopologyRow>(new CommandDefinition(
            """
            select world_id as "WorldId"
            from shards
            where id = @Id
            for update;
            """,
            new { shard.Id },
            transaction,
            cancellationToken: cancellationToken));
        if (!string.Equals(current.WorldId, shard.WorldId, StringComparison.Ordinal))
        {
            var blockers = await LoadWorldRebindBlockersAsync(
                connection,
                transaction,
                shard.Id,
                cancellationToken);
            if (blockers.HasAny)
            {
                throw new ShardWorldRebindBlockedException(
                    shard.Id,
                    current.WorldId,
                    shard.WorldId,
                    blockers.ActiveAssignments,
                    blockers.PendingJoinTickets,
                    blockers.ActiveSimulationSessions,
                    blockers.OpenCorpses);
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            update shards
            set display_name = @DisplayName,
                world_id = @WorldId,
                fleet_id = @FleetId,
                rule_set = @RuleSet,
                is_enabled = true,
                updated_at = now()
            where id = @Id;
            """,
            shard,
            transaction,
            cancellationToken: cancellationToken));

        if (!string.Equals(current.WorldId, shard.WorldId, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Rebound offline shard {ShardId} from world {PreviousWorldId} to world {WorldId}.",
                shard.Id,
                current.WorldId,
                shard.WorldId);
        }
    }

    private static Task<ShardWorldRebindBlockers> LoadWorldRebindBlockersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string shardId,
        CancellationToken cancellationToken)
    {
        return connection.QuerySingleAsync<ShardWorldRebindBlockers>(new CommandDefinition(
            """
            select
                (
                    select count(*)
                    from simulation_assignments assignment
                    join simulation_workers worker
                      on worker.id = assignment.worker_id
                    where assignment.shard_id = @ShardId
                      and assignment.released_at is null
                      and worker.runtime_id is not null
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
                ) as "ActiveSimulationSessions",
                (
                    select count(*)
                    from corpses
                    where shard_id = @ShardId
                      and closed_at is null
                ) as "OpenCorpses";
            """,
            new { ShardId = shardId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private sealed record ShardTopologyRow(string WorldId);

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
