using Mmorpg.GameService.Items.Development;
using Mmorpg.Testing;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Tests;

public sealed class DevelopmentItemPackagePlannerTests
{
    [Fact]
    public async Task FittingPackageHasNoProblems()
    {
        var catalog = await LoadFixtureCatalogAsync();
        var package = Package(
            Equip("fixture.bow", 1, "main_hand"),
            Equip("fixture.arrow", 50, "ammunition"),
            Grant("fixture.ore", 10, DevelopmentItemPackageDestinations.Inventory));

        Assert.Empty(DevelopmentItemPackagePlanner.FindProblems(package, catalog, 200));
    }

    [Fact]
    public async Task StaleExpectedLoadIsFound()
    {
        var catalog = await LoadFixtureCatalogAsync();
        var package = Package(Grant("fixture.ore", 10, DevelopmentItemPackageDestinations.Inventory)) with
        {
            ExpectedLoadPercent = 50
        };

        var problem = Assert.Single(DevelopmentItemPackagePlanner.FindProblems(package, catalog, 200));

        Assert.Contains("reaches 60/200 instead of exactly 50 percent", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EquippedBagBonusAndEquippedWeightAreApplied()
    {
        var catalog = await LoadFixtureCatalogAsync();
        var bonus = catalog.Definitions.Single(definition => definition.Id == "fixture.pack").Bag.CarryCapacityBonus;
        var package = Package(
            Equip("fixture.pack", 1, ItemEquipmentSlotIds.Bag),
            Equip("fixture.tunic", 1, "body"),
            Grant("fixture.ore", 10, DevelopmentItemPackageDestinations.Bag),
            Grant("fixture.ore", 10, DevelopmentItemPackageDestinations.Bank)) with
        {
            ExpectedLoadPercent = 50
        };

        var problem = Assert.Single(DevelopmentItemPackagePlanner.FindProblems(package, catalog, 200));

        Assert.Contains($"reaches 60/{200 + bonus} ", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogProblemsAreFoundBeforeTheLoadCheck()
    {
        var catalog = await LoadFixtureCatalogAsync();
        var package = Package(
            Grant("fixture.missing", 1, DevelopmentItemPackageDestinations.Inventory),
            Grant("fixture.potion", 11, DevelopmentItemPackageDestinations.Inventory),
            Equip("fixture.ore", 1, "main_hand"),
            Equip("fixture.arrow", 51, "ammunition"),
            Equip("fixture.bow", 1, "main_hand"),
            Equip("fixture.shield", 1, "off_hand"),
            Equip("fixture.sword", 1, "main_hand")) with
        {
            ExpectedLoadPercent = 50
        };

        var problems = DevelopmentItemPackagePlanner.FindProblems(package, catalog, 200);

        Assert.Collection(
            problems,
            problem => Assert.Contains("'fixture.missing' is not an active item definition", problem, StringComparison.Ordinal),
            problem => Assert.Contains("11 x 'fixture.potion' exceeds its stack size 10", problem, StringComparison.Ordinal),
            problem => Assert.Contains("'fixture.ore' cannot be equipped into 'main_hand'", problem, StringComparison.Ordinal),
            problem => Assert.Contains("'ammunition' holds at most 50 x 'fixture.arrow'", problem, StringComparison.Ordinal),
            problem => Assert.Contains("'fixture.shield' in 'off_hand' conflicts with the item equipped in 'main_hand'", problem, StringComparison.Ordinal),
            problem => Assert.Contains("'fixture.sword' in 'main_hand' conflicts with the item equipped in 'main_hand'", problem, StringComparison.Ordinal));
    }

    private static Task<ItemCatalogRuntimeDocument> LoadFixtureCatalogAsync()
    {
        return ItemCatalogRuntimeLoader.LoadAsync(
            RepositoryPaths.ItemCatalogFixtureRuntimePath,
            CancellationToken.None);
    }

    private static DevelopmentItemPackage Package(params DevelopmentItemPackageGrant[] grants)
    {
        return new DevelopmentItemPackage("test", "Test", "A planner test package.", null, grants);
    }

    private static DevelopmentItemPackageGrant Grant(string definitionId, int quantity, string destination)
    {
        return new DevelopmentItemPackageGrant(definitionId, quantity, destination, null, null);
    }

    private static DevelopmentItemPackageGrant Equip(string definitionId, int quantity, string equipmentSlotId)
    {
        return new DevelopmentItemPackageGrant(
            definitionId,
            quantity,
            DevelopmentItemPackageDestinations.Equipment,
            null,
            equipmentSlotId);
    }
}
