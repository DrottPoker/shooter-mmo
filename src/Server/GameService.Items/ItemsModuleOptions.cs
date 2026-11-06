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

    // Whether the worker quest route runs the Admin menu's quest tools, which only the
    // Development environment allows.
    public bool DevelopmentQuestTools { get; init; }
}
