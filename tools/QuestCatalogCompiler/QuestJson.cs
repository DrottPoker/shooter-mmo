using Mmorpg.Tools.ContentPipeline;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.Tools.QuestCatalogCompiler;

public static class QuestJson
{
    public static QuestAuthoringEntry DeserializeDefinition(string json)
    {
        return StrictContentJson.Deserialize<QuestAuthoringEntry>(json, "Quest definition");
    }

    public static QuestCatalogRuntimeDocument DeserializeRuntime(string json)
    {
        return StrictContentJson.Deserialize<QuestCatalogRuntimeDocument>(json, "Quest catalog runtime");
    }

    public static string SerializeRuntime(QuestCatalogRuntimeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return StrictContentJson.Serialize(document);
    }
}
