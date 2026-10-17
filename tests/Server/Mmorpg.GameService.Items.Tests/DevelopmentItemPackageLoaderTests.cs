using Mmorpg.GameService.Items.Development;
using Mmorpg.Testing;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Tests;

public sealed class DevelopmentItemPackageLoaderTests
{
    private const string MinimalPackage = """
        { "id": "minimal", "displayName": "Minimal", "description": "One stack.", "expectedLoadPercent": null,
          "grants": [ { "definitionId": "fixture.ore", "quantity": 1, "destination": "inventory", "slotIndex": null, "equipmentSlotId": null } ] }
        """;

    // The game content packages are checked against the compiled game catalog, so a content
    // or balance change that breaks a package fails here instead of in the Unity tool.
    [Fact]
    public async Task ContentPackagesFitTheCompiledItemCatalog()
    {
        var packages = DevelopmentItemPackageLoader.Load(
            ContentLayout.DevelopmentItemPackagesPath(RepositoryPaths.Content));
        var catalog = await ItemCatalogRuntimeLoader.LoadAsync(
            ContentLayout.GeneratedItemCatalogPath(RepositoryPaths.Generated),
            CancellationToken.None);

        AssertPackagesFit(packages, catalog);
    }

    [Fact]
    public async Task FixturePackagesFitTheFixtureCatalog()
    {
        var packages = DevelopmentItemPackageLoader.Load(
            ContentLayout.DevelopmentItemPackagesPath(RepositoryPaths.ItemCatalogFixtureContent));
        var catalog = await ItemCatalogRuntimeLoader.LoadAsync(
            RepositoryPaths.ItemCatalogFixtureRuntimePath,
            CancellationToken.None);

        AssertPackagesFit(packages, catalog);
    }

    [Theory]
    [InlineData("\"destination\": \"inventory\"", "\"destination\": \"floor\"", "destination is unsupported")]
    [InlineData("\"destination\": \"inventory\"", "\"destination\": \"bag\"", "no earlier grant equips")]
    [InlineData("\"quantity\": 1", "\"quantity\": 0", "positive quantity")]
    [InlineData("\"id\": \"minimal\"", "\"id\": \"Minimal\"", "lowercase identifier")]
    [InlineData("\"expectedLoadPercent\": null", "\"expectedLoadPercent\": 0", "expectedLoadPercent")]
    public void InvalidPackageIsRejected(string original, string replacement, string expectedError)
    {
        var json = Document(MinimalPackage.Replace(original, replacement, StringComparison.Ordinal));

        var error = Assert.Throws<InvalidOperationException>(() => DevelopmentItemPackageLoader.Parse(json));

        Assert.Contains(expectedError, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CorpseSeedPackageMustExist()
    {
        var json = Document(MinimalPackage).Replace(
            "\"corpseSeedPackageId\": \"minimal\"",
            "\"corpseSeedPackageId\": \"missing\"",
            StringComparison.Ordinal);

        var error = Assert.Throws<InvalidOperationException>(() => DevelopmentItemPackageLoader.Parse(json));

        Assert.Contains("corpseSeedPackageId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownPropertiesAreRejected()
    {
        var json = Document(MinimalPackage.Replace(
            "\"displayName\"",
            "\"weight\": 5, \"displayName\"",
            StringComparison.Ordinal));

        Assert.Throws<InvalidOperationException>(() => DevelopmentItemPackageLoader.Parse(json));
    }

    private static string Document(string package)
    {
        return "{ \"formatVersion\": 1, \"corpseSeedPackageId\": \"minimal\", \"packages\": [ "
            + package
            + " ] }";
    }

    // Uses the same planner as the package tool, with the catalog's base carry capacity.
    private static void AssertPackagesFit(
        DevelopmentItemPackageDocument packages,
        ItemCatalogRuntimeDocument catalog)
    {
        foreach (var package in packages.Packages)
        {
            var problems = DevelopmentItemPackagePlanner.FindProblems(
                package,
                catalog,
                catalog.Character.BaseCarryCapacity);
            Assert.True(
                problems.Count == 0,
                $"Package '{package.Id}' does not fit: {string.Join("; ", problems)}.");
        }
    }
}
