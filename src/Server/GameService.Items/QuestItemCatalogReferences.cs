using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.GameService.Items;

// The quests that GameService runs name items of the catalog, so a quest whose items the catalog
// does not hold in the form the quest needs stops the start.
internal sealed class QuestItemCatalogReferences(IQuestDefinitions questDefinitions) : IItemCatalogReferences
{
    private readonly IQuestDefinitions questDefinitions = questDefinitions;

    public void Validate(ItemCatalogRuntimeDocument catalog)
    {
        QuestItemReferences.Validate(questDefinitions.Quests, catalog);
    }
}
