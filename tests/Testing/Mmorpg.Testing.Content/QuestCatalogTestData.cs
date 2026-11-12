using Mmorpg.Testing;
using Mmorpg.Tools.QuestCatalogCompiler;
using Mmorpg.WorldData.Actors;
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
        var actors = WorldActorTestData.CompileCatalog();
        var worlds = WorldActorTestData.CheckedInWorldIds()
            .Select(worldId => WorldActorCompiler.CompileSpawns(actors, WorldActorTestData.LoadWorldSpawns(worldId)))
            .ToArray();
        return QuestCatalogCompiler.Compile(authoring, ItemCatalogTestData.Compile(), actors, worlds);
    }

    public static QuestCatalogAuthoringDocument LoadFixtureAuthoring()
    {
        return QuestContent.LoadCatalog(FixtureQuestsDirectory);
    }

    public static QuestCatalogRuntimeDocument CompileFixture()
    {
        var world = WorldActorTestData.Compile();
        return QuestCatalogCompiler.Compile(
            LoadFixtureAuthoring(),
            ItemCatalogTestData.CompileFixture(),
            world.Catalog,
            [world.Spawns]);
    }

    public static string RuntimeJson => File.ReadAllText(
        ContentLayout.GeneratedQuestCatalogPath(RepositoryPaths.Generated));

    public static string FixtureRuntimeJson => File.ReadAllText(RepositoryPaths.QuestCatalogFixtureRuntimePath);
}
