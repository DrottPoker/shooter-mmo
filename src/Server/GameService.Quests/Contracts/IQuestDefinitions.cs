using Mmorpg.WorldData.Quests;

namespace Mmorpg.GameService.Quests.Contracts;

// The compiled quest catalog that GameService runs, loaded and validated once at startup.
// The definitions are shared and must never be changed.
public interface IQuestDefinitions
{
    string CatalogId { get; }

    string Revision { get; }

    int MaximumActiveQuests { get; }

    // Every quest in id order.
    IReadOnlyList<QuestDefinition> Quests { get; }

    // The quest with this id, or null when the catalog does not hold it.
    QuestDefinition? Find(string questId);
}
