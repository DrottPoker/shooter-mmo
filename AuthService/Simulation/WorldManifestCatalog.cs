using ShooterMmo.Shared.Worlds;
using ShooterMmo.WorldData.Worlds;

namespace AuthService.Simulation;

public sealed class WorldManifestCatalog
{
    private WorldManifestCatalog(IReadOnlyCollection<WorldManifestDocument> manifests)
    {
        Manifests = manifests;
    }

    public IReadOnlyCollection<WorldManifestDocument> Manifests { get; }

    public static WorldManifestCatalog FromConfiguration(IConfiguration configuration)
    {
        var configuredPath = configuration["WorldData:WorldsPath"];
        var manifests = WorldManifestFileStore.LoadAll(
            string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine("WorldData", "Worlds")
                : configuredPath.Trim());
        return new WorldManifestCatalog(manifests);
    }

    public static WorldManifestCatalog FromManifests(
        IReadOnlyCollection<WorldManifestDocument> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        var snapshot = manifests.ToArray();
        foreach (var manifest in snapshot)
        {
            WorldManifestValidator.Validate(manifest);
        }

        EnsureUnique(
            snapshot.Select(manifest => manifest.worldId),
            "WorldId");
        return new WorldManifestCatalog(snapshot);
    }

    private static void EnsureUnique(IEnumerable<string> values, string label)
    {
        var duplicate = values
            .GroupBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"World manifest catalog contains duplicate {label} '{duplicate}'.");
        }
    }
}
