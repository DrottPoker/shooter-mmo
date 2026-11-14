using Dapper;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Placement;

// Mirrors the World definitions of the World manifests and ensures the configured Fleets,
// Nodes, and Shards. A row that already holds the configured values is not written again.
internal sealed class SimulationTopologySeeder(
    NpgsqlDataSource dataSource,
    PlacementModuleOptions options,
    ILogger<SimulationTopologySeeder> logger) : IDatabaseSeeder
{
    public async Task<IReadOnlyList<string>> PlanAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (!await PostgresCatalog.TableExistsAsync(connection, "shards", cancellationToken))
        {
            return Describe(Differences(TopologyRows.Empty));
        }

        return Describe(Differences(await ReadAsync(connection, null, cancellationToken)));
    }

    public async Task<IReadOnlyList<string>> SeedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var differences = Differences(await ReadAsync(connection, transaction, cancellationToken));
        foreach (var world in differences.Worlds)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into world_definitions (id, display_name)
                values (@Id, @DisplayName)
                on conflict (id) do update
                set display_name = excluded.display_name,
                    updated_at = now();
                """,
                world.Desired,
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var fleet in differences.Fleets)
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
                fleet.Desired,
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var node in differences.Nodes)
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
                node.Desired,
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var shard in differences.Shards)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into shards (
                    id,
                    display_name,
                    fleet_id,
                    rule_set,
                    is_enabled)
                values (
                    @Id,
                    @DisplayName,
                    @FleetId,
                    @RuleSet,
                    true)
                on conflict (id) do update
                set display_name = excluded.display_name,
                    fleet_id = excluded.fleet_id,
                    rule_set = excluded.rule_set,
                    is_enabled = true,
                    updated_at = now();
                """,
                shard.Desired,
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Simulation topology bootstrap ensured {FleetCount} fleets, {NodeCount} nodes, {WorldCount} worlds, and {ShardCount} shards.",
            options.Topology.Fleets.Count,
            options.Topology.Nodes.Count,
            options.Worlds.Manifests.Count,
            options.Topology.Shards.Count);
        return Describe(differences);
    }

    // The configured rows that the database lacks or holds with other values.
    private TopologyDifferences Differences(TopologyRows stored)
    {
        return new TopologyDifferences(
            Changed(
                options.Worlds.Manifests.Select(world => new WorldRow(world.worldId, world.displayName)),
                stored.Worlds,
                world => world.Id),
            Changed(
                options.Topology.Fleets.Select(fleet => new FleetRow(fleet.Id, fleet.DisplayName, fleet.RegionCode)),
                stored.Fleets,
                fleet => fleet.Id),
            Changed(
                options.Topology.Nodes.Select(node => new NodeRow(node.Id, node.FleetId, node.DisplayName)),
                stored.Nodes,
                node => node.Id),
            Changed(
                options.Topology.Shards.Select(shard => new ShardRow(
                    shard.Id,
                    shard.DisplayName,
                    shard.FleetId,
                    shard.RuleSet,
                    true)),
                stored.Shards,
                shard => shard.Id));
    }

    private static IReadOnlyList<Difference<TRow>> Changed<TRow>(
        IEnumerable<TRow> desired,
        IReadOnlyList<TRow> stored,
        Func<TRow, string> id)
        where TRow : notnull
    {
        var storedById = stored.ToDictionary(id, StringComparer.Ordinal);
        return desired
            .Select(row => new Difference<TRow>(row, storedById.ContainsKey(id(row))))
            .Where(difference => !difference.Exists || !Equals(storedById[id(difference.Desired)], difference.Desired))
            .ToArray();
    }

    private static string[] Describe(TopologyDifferences differences)
    {
        return
        [
            .. differences.Worlds.Select(world => Line("World definition", world.Desired.Id, world.Exists)),
            .. differences.Fleets.Select(fleet => Line("Fleet", fleet.Desired.Id, fleet.Exists)),
            .. differences.Nodes.Select(node => Line("Node", node.Desired.Id, node.Exists)),
            .. differences.Shards.Select(shard => Line("Shard", shard.Desired.Id, shard.Exists))
        ];
    }

    private static string Line(string kind, string id, bool exists)
    {
        return $"Topology: {(exists ? "update" : "add")} {kind} {id} as configured.";
    }

    private static async Task<TopologyRows> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        CommandDefinition Command(string sql)
        {
            return new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken);
        }

        return new TopologyRows(
            (await connection.QueryAsync<WorldRow>(Command(
                """select id as "Id", display_name as "DisplayName" from world_definitions;"""))).ToArray(),
            (await connection.QueryAsync<FleetRow>(Command(
                """select id as "Id", display_name as "DisplayName", region_code as "RegionCode" from fleets;"""))).ToArray(),
            (await connection.QueryAsync<NodeRow>(Command(
                """select id as "Id", fleet_id as "FleetId", display_name as "DisplayName" from simulation_nodes;"""))).ToArray(),
            (await connection.QueryAsync<ShardRow>(Command(
                """
                select id as "Id", display_name as "DisplayName", fleet_id as "FleetId",
                       rule_set as "RuleSet", is_enabled as "IsEnabled"
                from shards;
                """))).ToArray());
    }

    private sealed record WorldRow(string Id, string DisplayName);

    private sealed record FleetRow(string Id, string DisplayName, string RegionCode);

    private sealed record NodeRow(string Id, string FleetId, string DisplayName);

    private sealed record ShardRow(string Id, string DisplayName, string FleetId, string RuleSet, bool IsEnabled);

    private sealed record Difference<TRow>(TRow Desired, bool Exists);

    private sealed record TopologyRows(
        IReadOnlyList<WorldRow> Worlds,
        IReadOnlyList<FleetRow> Fleets,
        IReadOnlyList<NodeRow> Nodes,
        IReadOnlyList<ShardRow> Shards)
    {
        public static TopologyRows Empty { get; } = new([], [], [], []);
    }

    private sealed record TopologyDifferences(
        IReadOnlyList<Difference<WorldRow>> Worlds,
        IReadOnlyList<Difference<FleetRow>> Fleets,
        IReadOnlyList<Difference<NodeRow>> Nodes,
        IReadOnlyList<Difference<ShardRow>> Shards);
}
