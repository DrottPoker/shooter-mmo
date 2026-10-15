using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Items;

// The current item catalog is mirrored first, because the item state backfill needs it.
public sealed class ItemsDatabaseSeeder(
    ItemCatalogSeeder itemCatalogSeeder,
    CharacterItemStateBootstrapper characterItemStateBootstrapper) : IDatabaseSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        await itemCatalogSeeder.SeedAsync(cancellationToken);
        await characterItemStateBootstrapper.BackfillActiveCharactersAsync(cancellationToken);
    }
}
