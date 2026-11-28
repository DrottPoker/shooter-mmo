using Mmorpg.Testing;
using Mmorpg.Testing.Content;
using Mmorpg.Tools.ContentPipeline;
using Mmorpg.Tools.QuestCatalogCompiler;
using Mmorpg.WorldData.Progression;
using ItemCatalogRuntimeJson = Mmorpg.Tools.ItemCatalogCompiler.ItemCatalogJson;

namespace Mmorpg.Shared.Tests;

// The item and quest compilers know only the absolute level limit, because the highest level
// lives in the progression rules. These hold the checked-in content to the checked-in highest
// level, as GameService does at startup.
public sealed class ProgressionContentTests
{
    private static readonly Lazy<int> MaximumLevel = new(() =>
        ProgressionRules.FromDocument(StrictContentJson.DeserializeFile<ProgressionRulesDocument>(
            RepositoryPaths.ProgressionRulesPath,
            "Progression rules")).MaximumCharacterLevel);

    [Fact]
    public void EveryItemRequiresALevelACharacterCanReach()
    {
        var catalog = ItemCatalogRuntimeJson.DeserializeRuntime(ItemCatalogTestData.RuntimeJson);

        Assert.NotEmpty(catalog.Definitions);
        Assert.All(
            catalog.Definitions,
            definition => Assert.InRange(definition.RequiredCharacterLevel, 1, MaximumLevel.Value));
    }

    [Fact]
    public void EveryQuestRequiresALevelACharacterCanReach()
    {
        var catalog = QuestJson.DeserializeRuntime(QuestCatalogTestData.RuntimeJson);

        Assert.NotEmpty(catalog.Quests);
        Assert.All(
            catalog.Quests,
            quest => Assert.InRange(quest.MinimumLevel, 1, MaximumLevel.Value));
    }
}
