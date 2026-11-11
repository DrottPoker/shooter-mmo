using Mmorpg.Testing;
using Mmorpg.Tools.QuestCatalogCompiler;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.Testing.Content;

// The real quest content, compiled against the real item catalog and every checked-in World,
// and the test-owned fixture catalog, compiled against the fixture items and the fixture World.
public static class QuestCatalogTestData
{
    public static string QuestsDirectory => ContentLayout.QuestsDirectory(RepositoryPaths.Content);

    public static string FixtureQuestsDirectory =>
        ContentLayout.QuestsDirectory(RepositoryPaths.QuestCatalogFixtureContent);

    public static QuestCatalogAuthoringDocument LoadAuthoring()
    {
        return QuestContent.LoadCatalog(QuestsDirectory);
    }

    public static QuestCatalogRuntimeDocument Compile()
    {
        return Compile(LoadAuthoring());
    }

    // The checked-in items and Worlds with the quests of a test.
    public static QuestCatalogRuntimeDocument Compile(QuestCatalogAuthoringDocument authoring)
    {
        var worlds = WorldActorTestData.CheckedInWorldIds()
            .Select(WorldActorTestData.CompileWorld)
            .ToArray();
        return QuestCatalogCompiler.Compile(authoring, ItemCatalogTestData.Compile(), worlds);
    }

    public static QuestCatalogAuthoringDocument LoadFixtureAuthoring()
    {
        return QuestContent.LoadCatalog(FixtureQuestsDirectory);
    }

    public static QuestCatalogRuntimeDocument CompileFixture()
    {
        return QuestCatalogCompiler.Compile(
            LoadFixtureAuthoring(),
            ItemCatalogTestData.CompileFixture(),
            [WorldActorTestData.Compile()]);
    }

    public static string RuntimeJson => File.ReadAllText(
        ContentLayout.GeneratedQuestCatalogPath(RepositoryPaths.Generated));

    public static string FixtureRuntimeJson => File.ReadAllText(RepositoryPaths.QuestCatalogFixtureRuntimePath);
}
