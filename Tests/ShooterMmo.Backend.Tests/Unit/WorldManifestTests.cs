using ShooterMmo.Shared.Worlds;
using ShooterMmo.WorldData.Worlds;

namespace ShooterMmo.Backend.Tests.Unit;

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
        Assert.Equal(-14f, worldOne.bounds.minimumX);
        Assert.Equal("DevelopmentWorld2", worldTwo.clientSceneName);
        Assert.Equal(-254f, worldTwo.bounds.minimumX);
        Assert.Equal(-16f, worldTwo.spawn.z);
        Assert.Equal(3, worldTwo.servicePoints.Length);
    }

    [Fact]
    public void ManifestRejectsUnsupportedServicePointKind()
    {
        var manifest = CreateManifest();
        manifest.servicePoints =
        [
            new WorldServicePointDocument
            {
                id = "service",
                kind = "corpse",
                radius = 3f
            }
        ];

        var exception = Assert.Throws<WorldManifestValidationException>(
            () => WorldManifestValidator.Validate(manifest));

        Assert.Contains("kind must be", exception.Message);
    }

    [Fact]
    public void ManifestRejectsDuplicateServicePointIds()
    {
        var manifest = CreateManifest();
        manifest.servicePoints =
        [
            new WorldServicePointDocument
            {
                id = "service",
                kind = "bank",
                radius = 3f
            },
            new WorldServicePointDocument
            {
                id = "service",
                kind = "insurance_npc",
                radius = 3f
            }
        ];

        var exception = Assert.Throws<WorldManifestValidationException>(
            () => WorldManifestValidator.Validate(manifest));

        Assert.Contains("ids must be unique", exception.Message);
    }

    [Fact]
    public void FileStoreRejectsWorldIdPathTraversal()
    {
        Assert.Throws<InvalidDataException>(() =>
            WorldManifestFileStore.Load(WorldDataRootPath(), "../development-world-1"));
    }

    [Fact]
    public void ManifestCatalogAllowsWorldsToShareAReusableClientScene()
    {
        var first = CreateManifest();
        var second = CreateManifest();
        second.worldId = "second-world";

        var catalog = AuthService.Simulation.WorldManifestCatalog.FromManifests(
            [first, second]);

        Assert.Equal(2, catalog.Manifests.Count);
        Assert.All(
            catalog.Manifests,
            manifest => Assert.Equal("TestWorld", manifest.clientSceneName));
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
            spawn = new WorldSpawnDocument(),
            servicePoints = Array.Empty<WorldServicePointDocument>()
        };
    }

    private static string WorldDataRootPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ShooterMmo.slnx")))
            {
                return Path.Combine(directory.FullName, "WorldData", "Worlds");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Shooter MMO repository root.");
    }
}
