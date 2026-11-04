using Mmorpg.Server.Common.Quests;
using Mmorpg.Testing;
using Mmorpg.WorldData.Content;

namespace Mmorpg.Server.Common.Tests;

public sealed class QuestCatalogFileLoaderTests
{
    [Fact]
    public void TheCheckedInAndFixtureCatalogsLoad()
    {
        var content = QuestCatalogFileLoader.Load(ContentLayout.GeneratedQuestCatalogPath(RepositoryPaths.Generated));
        var fixture = QuestCatalogFileLoader.Load(RepositoryPaths.QuestCatalogFixtureRuntimePath);

        Assert.Equal("core", content.CatalogId);
        Assert.Contains(content.Quests, quest => quest.Id == "quest.wolves_at_the_gate");
        Assert.Equal(
            ["quest.fixture.errand", "quest.fixture.ore_run", "quest.fixture.wolves"],
            fixture.Quests.Select(quest => quest.Id));
    }

    [Fact]
    public void AMissingFileIsRejected()
    {
        Assert.Throws<FileNotFoundException>(
            () => QuestCatalogFileLoader.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json")));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("{", "malformed")]
    [InlineData("""{ "formatVersion": 1, "formatVersion": 1 }""", "duplicate property")]
    [InlineData("""{ "formatVersion": 1, "chapters": [] }""", "malformed")]
    public void BrokenJsonIsRejected(string json, string reason)
    {
        var exception = Assert.Throws<InvalidDataException>(() => QuestCatalogFileLoader.Parse(json));

        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
    }

    // The loader trusts the revision only after computing it again.
    [Fact]
    public void AnEditedCatalogIsRejected()
    {
        var json = File.ReadAllText(RepositoryPaths.QuestCatalogFixtureRuntimePath)
            .Replace("\"title\": \"Fixture Wolves\"", "\"title\": \"Edited Wolves\"", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => QuestCatalogFileLoader.Parse(json));

        Assert.Contains("revision does not match", exception.Message, StringComparison.Ordinal);
    }
}
