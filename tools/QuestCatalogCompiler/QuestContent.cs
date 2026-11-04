using Mmorpg.Tools.ContentPipeline;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.Tools.QuestCatalogCompiler;

// content/quests/catalog.json: the quest settings and the retired quest ids.
public sealed class QuestCatalogHeaderDocument
{
    public int FormatVersion;
    public string? CatalogId;
    public int MaximumActiveQuests;
    public string[]? RetiredQuestIds;
}

// Assembles quest authoring from catalog.json and one file per quest definition.
public static class QuestContent
{
    public static QuestCatalogAuthoringDocument LoadCatalog(string questsDirectory)
    {
        var header = StrictContentJson.DeserializeFile<QuestCatalogHeaderDocument>(
            ContentLayout.CatalogPath(questsDirectory),
            "Quest catalog");
        return new QuestCatalogAuthoringDocument
        {
            FormatVersion = header.FormatVersion,
            CatalogId = header.CatalogId,
            MaximumActiveQuests = header.MaximumActiveQuests,
            RetiredQuestIds = header.RetiredQuestIds,
            Quests = ContentLayout.ListDefinitionFiles(questsDirectory)
                .Select(LoadDefinition)
                .ToArray()
        };
    }

    private static QuestAuthoringEntry LoadDefinition(ContentDefinitionFile file)
    {
        var quest = StrictContentJson.DeserializeFile<QuestAuthoringEntry>(file.Path, "Quest definition");
        if (!string.Equals(quest.Id, file.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Quest definition file '{file.Path}' must contain id '{file.Id}'.");
        }

        return quest;
    }
}
