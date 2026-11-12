using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Items;

// The current item catalog is mirrored first, because the item state backfill needs it. Content
// that names items of this catalog checks it first, so a catalog that would break that content
// stops the start before anything is mirrored.
internal sealed class ItemsDatabaseSeeder(
    ItemCatalogSeeder itemCatalogSeeder,
    CharacterItemStateBootstrapper characterItemStateBootstrapper,
    ItemCatalogSource catalogSource,
    IItemCatalogReferences catalogReferences) : IDatabaseSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var catalog = await ItemCatalogRuntimeLoader.LoadAsync(catalogSource.RuntimeCatalogPath, cancellationToken);
        catalogReferences.Validate(catalog);
        await itemCatalogSeeder.SeedAsync(catalog, cancellationToken);
        await characterItemStateBootstrapper.BackfillActiveCharactersAsync(cancellationToken);
    }
}
