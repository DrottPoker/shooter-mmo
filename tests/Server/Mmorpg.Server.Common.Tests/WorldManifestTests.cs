using Mmorpg.Server.Common.Worlds;
using Mmorpg.Testing;
using Mmorpg.WorldData.Worlds;

namespace Mmorpg.Server.Common.Tests;

public sealed class WorldManifestTests
{
    [Fact]
    public void CheckedInWorldManifestsAreValidAndUniquelyMapped()
    {
        var manifests = WorldManifestFileStore.LoadAll(WorldDataRootPath());

        Assert.Equal(2, manifests.Count);
        var worldOne = Assert.Single(
            manifests,
            manifest => manifest.worldId == "development-world-1");
        var worldTwo = Assert.Single(
            manifests,
            manifest => manifest.worldId == "development-world-2");
        Assert.Equal("DevelopmentWorld1", worldOne.clientSceneName);
        Assert.Equal("DevelopmentWorld2", worldTwo.clientSceneName);
    }

    [Fact]
    public void ManifestRejectsTheRetiredFormatVersion()
    {
        var manifest = CreateManifest();
        manifest.formatVersion = 1;

        var exception = Assert.Throws<WorldManifestValidationException>(
            () => WorldManifestValidator.Validate(manifest));

        Assert.Contains("format version is unsupported", exception.Message);
    }

    [Fact]
    public void ManifestRejectsAWorldBeyondWhatTheSnapshotRecordCarries()
    {
        var edge = CreateManifest();
        edge.bounds.minimumX = -WorldManifestFormat.MaximumHorizontalCoordinate;
        edge.bounds.maximumZ = WorldManifestFormat.MaximumHorizontalCoordinate;
        edge.groundHeight = -WorldManifestFormat.MaximumVerticalCoordinate;
        edge.spawn.y = WorldManifestFormat.MaximumVerticalCoordinate;
        WorldManifestValidator.Validate(edge);

        var wide = CreateManifest();
        wide.bounds.maximumX = WorldManifestFormat.MaximumHorizontalCoordinate + 1f;
        Assert.Contains(
            "World bounds must stay within",
            Assert.Throws<WorldManifestValidationException>(() => WorldManifestValidator.Validate(wide)).Message,
            StringComparison.Ordinal);
        var deep = CreateManifest();
        deep.bounds.minimumZ = -WorldManifestFormat.MaximumHorizontalCoordinate - 1f;
        Assert.Throws<WorldManifestValidationException>(() => WorldManifestValidator.Validate(deep));
        var low = CreateManifest();
        low.groundHeight = -WorldManifestFormat.MaximumVerticalCoordinate - 1f;
        Assert.Contains(
            "GroundHeight",
            Assert.Throws<WorldManifestValidationException>(() => WorldManifestValidator.Validate(low)).Message,
            StringComparison.Ordinal);
        var high = CreateManifest();
        high.spawn.y = WorldManifestFormat.MaximumVerticalCoordinate + 1f;
        Assert.Contains(
            "Spawn.Y",
            Assert.Throws<WorldManifestValidationException>(() => WorldManifestValidator.Validate(high)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FileStoreRejectsRetiredServicePoints()
    {
        var root = Path.Combine(Path.GetTempPath(), "MmorpgWorldManifestTests", Guid.NewGuid().ToString("N"));
        var worldDirectory = Path.Combine(root, "test-world");
        Directory.CreateDirectory(worldDirectory);
        try
        {
            File.WriteAllText(
                Path.Combine(worldDirectory, WorldManifestFormat.FileName),
                """
                {
                  "formatVersion": 2,
                  "worldId": "test-world",
                  "displayName": "Test World",
                  "clientSceneName": "TestWorld",
                  "groundHeight": 0.0,
                  "bounds": { "minimumX": -10.0, "maximumX": 10.0, "minimumZ": -10.0, "maximumZ": 10.0 },
                  "spawn": { "x": 0.0, "y": 0.0, "z": 0.0, "yawDegrees": 0.0 },
                  "servicePoints": []
                }
                """);

            var exception = Assert.Throws<InvalidDataException>(
                () => WorldManifestFileStore.Load(root, "test-world"));

            Assert.Contains("servicePoints", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FileStoreRejectsWorldIdPathTraversal()
    {
        Assert.Throws<InvalidDataException>(() =>
            WorldManifestFileStore.Load(WorldDataRootPath(), "../development-world-1"));
    }

    private static WorldManifestDocument CreateManifest()
    {
        return new WorldManifestDocument
        {
            formatVersion = WorldManifestFormat.Version,
            worldId = "test-world",
            displayName = "Test World",
            clientSceneName = "TestWorld",
            bounds = new WorldBoundsDocument
            {
                minimumX = -10f,
                maximumX = 10f,
                minimumZ = -10f,
                maximumZ = 10f
            },
            spawn = new WorldSpawnDocument()
        };
    }

    private static string WorldDataRootPath()
    {
        return RepositoryPaths.Worlds;
    }
}
