using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items;

// Content that names items of the catalog and checks them against the item catalog before
// Items mirrors it, so a catalog that would break that content stops the start. Items owns it
// and its seeder calls it; the quest code implements it.
internal interface IItemCatalogReferences
{
    // Throws when the catalog does not hold a named item in the form the content needs.
    void Validate(ItemCatalogRuntimeDocument catalog);
}
