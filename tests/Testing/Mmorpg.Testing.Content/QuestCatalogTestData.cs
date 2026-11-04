using Mmorpg.Testing;
using Mmorpg.Tools.QuestCatalogCompiler;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.Testing.Content;

// The real quest content, compiled against the real item catalog and every checked-in World.
public static class QuestCatalogTestData
{
    public static string QuestsDirectory => ContentLayout.QuestsDirectory(RepositoryPaths.Content);

    public static QuestCatalogAuthoringDocument LoadAuthoring()
    {
        return QuestContent.LoadCatalog(QuestsDirectory);
    }

    public static QuestCatalogRuntimeDocument Compile()
    {
        var worlds = WorldActorTestData.CheckedInWorldIds()
            .Select(WorldActorTestData.CompileWorld)
            .ToArray();
        return QuestCatalogCompiler.Compile(LoadAuthoring(), ItemCatalogTestData.Compile(), worlds);
    }

    public static string RuntimeJson => File.ReadAllText(
        ContentLayout.GeneratedQuestCatalogPath(RepositoryPaths.Generated));
}
