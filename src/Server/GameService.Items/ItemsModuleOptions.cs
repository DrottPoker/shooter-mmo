namespace Mmorpg.GameService.Items;

public sealed record ItemsModuleOptions(
    ItemCatalogSource Catalog,
    ItemOperationsOptions Operations,
    NpcItemLifecycleOptions NpcLifecycle,
    StackStressFixtureOptions StackStressFixtures);
