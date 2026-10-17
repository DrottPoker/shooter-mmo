using Mmorpg.GameService.Items.Development;
using Mmorpg.GameService.Items.StackStress;

namespace Mmorpg.GameService.Items;

public sealed record ItemsModuleOptions(
    ItemCatalogSource Catalog,
    ItemOperationsOptions Operations,
    NpcItemLifecycleOptions NpcLifecycle,
    StackStressFixtureOptions StackStressFixtures)
{
    // The packages of the development item tool; the tool refuses to run outside Development.
    public DevelopmentItemPackageSource DevelopmentItemPackages { get; init; } =
        new(Path.Combine(AppContext.BaseDirectory, "Content", "Development", "item-packages.json"));
}
