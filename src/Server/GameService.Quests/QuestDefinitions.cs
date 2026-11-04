using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.GameService.Quests;

// The loaded quest catalog with a lookup by quest id.
internal sealed class QuestDefinitions : IQuestDefinitions
{
    private readonly Dictionary<string, QuestDefinition> questsById;

    public QuestDefinitions(QuestCatalogRuntimeDocument runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        CatalogId = runtime.CatalogId;
        Revision = runtime.Revision;
        MaximumActiveQuests = runtime.MaximumActiveQuests;
        Quests = runtime.Quests.OrderBy(quest => quest.Id, StringComparer.Ordinal).ToArray();
        RetiredQuestIds = runtime.RetiredQuestIds.Order(StringComparer.Ordinal).ToArray();
        questsById = Quests.ToDictionary(quest => quest.Id, StringComparer.Ordinal);
    }

    public string CatalogId { get; }

    public string Revision { get; }

    public int MaximumActiveQuests { get; }

    public IReadOnlyList<QuestDefinition> Quests { get; }

    public IReadOnlyList<string> RetiredQuestIds { get; }

    public QuestDefinition? Find(string questId)
    {
        return questId is not null && questsById.TryGetValue(questId, out var quest) ? quest : null;
    }
}
