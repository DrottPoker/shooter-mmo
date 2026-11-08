using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.GameService.Items;

// The current item catalog is mirrored first, because the item state backfill needs it. The quests
// that GameService runs name items of this catalog, so a quest whose items the catalog does not
// hold in the form the quest needs stops the start.
internal sealed class ItemsDatabaseSeeder(
    ItemCatalogSeeder itemCatalogSeeder,
    CharacterItemStateBootstrapper characterItemStateBootstrapper,
    ItemCatalogSource catalogSource,
    IQuestDefinitions questDefinitions) : IDatabaseSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var catalog = await ItemCatalogRuntimeLoader.LoadAsync(catalogSource.RuntimeCatalogPath, cancellationToken);
        QuestItemReferences.Validate(questDefinitions.Quests, catalog);
        await itemCatalogSeeder.SeedAsync(catalog, cancellationToken);
        await characterItemStateBootstrapper.BackfillActiveCharactersAsync(cancellationToken);
    }
}
