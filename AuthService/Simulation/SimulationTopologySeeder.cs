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
            await connection.ExecuteAsync(new CommandDefinition(
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
                on conflict (id) do update
                set display_name = excluded.display_name,
                    world_id = excluded.world_id,
                    fleet_id = excluded.fleet_id,
                    rule_set = excluded.rule_set,
                    updated_at = now();
                """,
                shard,
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Simulation topology bootstrap ensured {FleetCount} fleets, {NodeCount} nodes, {WorldCount} worlds, and {ShardCount} shards.",
            config.SimulationTopology.Fleets.Count,
            config.SimulationTopology.Nodes.Count,
            config.SimulationTopology.Worlds.Count,
            config.SimulationTopology.Shards.Count);
    }
}
