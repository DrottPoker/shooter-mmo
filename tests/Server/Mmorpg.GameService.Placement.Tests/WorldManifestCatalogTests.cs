using Mmorpg.WorldData.Worlds;

namespace Mmorpg.GameService.Placement.Tests;

public sealed class WorldManifestCatalogTests
{
    [Fact]
    public void ManifestCatalogAllowsWorldsToShareAReusableClientScene()
    {
        var first = CreateManifest();
        var second = CreateManifest();
        second.worldId = "second-world";

        var catalog = Mmorpg.GameService.Placement.WorldManifestCatalog.FromManifests(
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
            spawn = new WorldSpawnDocument()
        };
    }
}
