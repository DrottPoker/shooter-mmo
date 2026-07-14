namespace AuthService.Config;

public sealed record SimulationTopologyConfig(
    IReadOnlyCollection<WorldDefinitionBootstrapConfig> Worlds,
    IReadOnlyCollection<FleetBootstrapConfig> Fleets,
    IReadOnlyCollection<SimulationNodeBootstrapConfig> Nodes,
    IReadOnlyCollection<ShardBootstrapConfig> Shards)
{
    public static SimulationTopologyConfig FromConfiguration(
        IConfiguration configuration,
        ICollection<string> errors)
    {
        var section = configuration.GetSection("Simulation:Topology");
        var worlds = section.GetSection("Worlds")
            .GetChildren()
            .Select(item => new WorldDefinitionBootstrapConfig(
                item["Id"]?.Trim() ?? string.Empty,
                item["DisplayName"]?.Trim() ?? string.Empty))
            .ToArray();
        var fleets = section.GetSection("Fleets")
            .GetChildren()
            .Select(item => new FleetBootstrapConfig(
                item["Id"]?.Trim() ?? string.Empty,
                item["DisplayName"]?.Trim() ?? string.Empty,
                item["RegionCode"]?.Trim() ?? string.Empty))
            .ToArray();
        var nodes = section.GetSection("Nodes")
            .GetChildren()
            .Select(item => new SimulationNodeBootstrapConfig(
                item["Id"]?.Trim() ?? string.Empty,
                item["FleetId"]?.Trim() ?? string.Empty,
                item["DisplayName"]?.Trim() ?? string.Empty))
            .ToArray();
        var shards = section.GetSection("Shards")
            .GetChildren()
            .Select(item => new ShardBootstrapConfig(
                item["Id"]?.Trim() ?? string.Empty,
                item["WorldId"]?.Trim() ?? string.Empty,
                item["FleetId"]?.Trim() ?? string.Empty,
                item["DisplayName"]?.Trim() ?? string.Empty,
                item["RuleSet"]?.Trim() ?? string.Empty))
            .ToArray();

        ValidateRequiredCollection(worlds, "Worlds", errors);
        ValidateRequiredCollection(fleets, "Fleets", errors);
        ValidateRequiredCollection(nodes, "Nodes", errors);
        ValidateRequiredCollection(shards, "Shards", errors);

        ValidateUniqueIds(worlds.Select(item => item.Id), "Worlds", errors);
        ValidateUniqueIds(fleets.Select(item => item.Id), "Fleets", errors);
        ValidateUniqueIds(nodes.Select(item => item.Id), "Nodes", errors);
        ValidateUniqueIds(shards.Select(item => item.Id), "Shards", errors);

        foreach (var world in worlds)
        {
            ValidateIdentifier(world.Id, $"Worlds:{world.Id}:Id", errors);
            ValidateDisplayName(world.DisplayName, $"Worlds:{world.Id}:DisplayName", errors);
        }

        foreach (var fleet in fleets)
        {
            ValidateIdentifier(fleet.Id, $"Fleets:{fleet.Id}:Id", errors);
            ValidateDisplayName(fleet.DisplayName, $"Fleets:{fleet.Id}:DisplayName", errors);
            if (string.IsNullOrWhiteSpace(fleet.RegionCode)
                || fleet.RegionCode.Length > 16
                || fleet.RegionCode.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character != '-'))
            {
                errors.Add(
                    $"Simulation:Topology:Fleets:{fleet.Id}:RegionCode must be a valid region code.");
            }
        }

        var fleetIds = fleets.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var worldIds = worlds.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            ValidateIdentifier(node.Id, $"Nodes:{node.Id}:Id", errors);
            ValidateDisplayName(node.DisplayName, $"Nodes:{node.Id}:DisplayName", errors);
            if (!fleetIds.Contains(node.FleetId))
            {
                errors.Add(
                    $"Simulation:Topology:Nodes:{node.Id}:FleetId references an unknown fleet.");
            }
        }

        foreach (var shard in shards)
        {
            ValidateIdentifier(shard.Id, $"Shards:{shard.Id}:Id", errors);
            ValidateDisplayName(shard.DisplayName, $"Shards:{shard.Id}:DisplayName", errors);
            if (!worldIds.Contains(shard.WorldId))
            {
                errors.Add(
                    $"Simulation:Topology:Shards:{shard.Id}:WorldId references an unknown world.");
            }

            if (!fleetIds.Contains(shard.FleetId))
            {
                errors.Add(
                    $"Simulation:Topology:Shards:{shard.Id}:FleetId references an unknown fleet.");
            }

            if (string.IsNullOrWhiteSpace(shard.RuleSet) || shard.RuleSet.Length > 128)
            {
                errors.Add(
                    $"Simulation:Topology:Shards:{shard.Id}:RuleSet is required and must not exceed 128 characters.");
            }
        }

        return new SimulationTopologyConfig(worlds, fleets, nodes, shards);
    }

    private static void ValidateRequiredCollection<T>(
        IReadOnlyCollection<T> values,
        string name,
        ICollection<string> errors)
    {
        if (values.Count == 0)
        {
            errors.Add($"Simulation:Topology:{name} must contain at least one entry.");
        }
    }

    private static void ValidateUniqueIds(
        IEnumerable<string> identifiers,
        string name,
        ICollection<string> errors)
    {
        var duplicates = identifiers
            .GroupBy(identifier => identifier, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            errors.Add(
                $"Simulation:Topology:{name} contains duplicate ids: {string.Join(", ", duplicates)}.");
        }
    }

    private static void ValidateIdentifier(
        string value,
        string key,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            errors.Add($"Simulation:Topology:{key} must be a valid identifier.");
        }
    }

    private static void ValidateDisplayName(
        string value,
        string key,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            errors.Add(
                $"Simulation:Topology:{key} is required and must not exceed 128 characters.");
        }
    }
}

public sealed record WorldDefinitionBootstrapConfig(string Id, string DisplayName);

public sealed record FleetBootstrapConfig(string Id, string DisplayName, string RegionCode);

public sealed record SimulationNodeBootstrapConfig(string Id, string FleetId, string DisplayName);

public sealed record ShardBootstrapConfig(
    string Id,
    string WorldId,
    string FleetId,
    string DisplayName,
    string RuleSet);
