using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Shared.Tests;

// Item values, tool kinds, the economy settings, and vendor profiles in the item catalog,
// checked against the test-owned fixture catalog so balance changes never break them.
public sealed class VendorContentTests
{
    [Fact]
    public void FixtureCatalogCompilesValuesToolKindsEconomyAndVendors()
    {
        var catalog = ItemCatalogTestData.CompileFixture();

        Assert.Equal("Fixture Coins", catalog.Economy.CurrencyDisplayName);
        Assert.Equal(0, catalog.Economy.StartingBalance);
        Assert.Equal(["pickaxe"], catalog.ToolKinds.Select(kind => kind.Id));
        Assert.Equal(["pickaxe"], Definition(catalog, "fixture.pickaxe").ToolKinds);
        Assert.Equal(10, Definition(catalog, "fixture.ore").Value);
        var vendor = Assert.Single(catalog.Vendors);
        Assert.Equal("vendor.fixture_store", vendor.Id);
        Assert.Equal(
            ["fixture.arrow", "fixture.potion", "fixture.pickaxe", "fixture.pack"],
            vendor.Offers.Select(offer => offer.DefinitionId));
    }

    [Fact]
    public void ValueChangeKeepsStructuralFingerprintAndChangesRevision()
    {
        var authoring = ItemCatalogTestData.LoadFixtureAuthoring();
        var original = ItemCatalogCompiler.Compile(authoring);
        FindDefinition(authoring, "fixture.ore").Value++;
        FindDefinition(authoring, "fixture.ore").ToolKinds = [];

        var changed = ItemCatalogCompiler.Compile(authoring);

        Assert.NotEqual(original.Revision, changed.Revision);
        Assert.Equal(
            Definition(original, "fixture.ore").StructuralFingerprint,
            Definition(changed, "fixture.ore").StructuralFingerprint);
    }

    [Fact]
    public void ToolKindChangeKeepsStructuralFingerprint()
    {
        var authoring = ItemCatalogTestData.LoadFixtureAuthoring();
        var original = ItemCatalogCompiler.Compile(authoring);
        FindDefinition(authoring, "fixture.pickaxe").ToolKinds = [];

        var changed = ItemCatalogCompiler.Compile(authoring);

        Assert.NotEqual(original.Revision, changed.Revision);
        Assert.Equal(
            Definition(original, "fixture.pickaxe").StructuralFingerprint,
            Definition(changed, "fixture.pickaxe").StructuralFingerprint);
    }

    [Fact]
    public void MissingValueFailsValidation()
    {
        var authoring = ItemCatalogTestData.LoadFixtureAuthoring();
        FindDefinition(authoring, "fixture.ore").Value = -1;

        AssertInvalid(authoring, ".value is required");
    }

    [Fact]
    public void ToolKindsRequireKnownKindAndToolSlot()
    {
        var unknown = ItemCatalogTestData.LoadFixtureAuthoring();
        FindDefinition(unknown, "fixture.pickaxe").ToolKinds = ["shovel"];
        AssertInvalid(unknown, "references unknown tool kind 'shovel'");

        var withoutToolSlot = ItemCatalogTestData.LoadFixtureAuthoring();
        FindDefinition(withoutToolSlot, "fixture.ore").ToolKinds = ["pickaxe"];
        AssertInvalid(withoutToolSlot, "requires compatibility with the 'tool' equipment slot");
    }

    [Fact]
    public void MissingEconomyFailsValidation()
    {
        var authoring = ItemCatalogTestData.LoadFixtureAuthoring();
        authoring.Economy = null!;

        AssertInvalid(authoring, "economy is required");
    }

    [Fact]
    public void NegativeStartingBalanceFailsValidation()
    {
        var authoring = ItemCatalogTestData.LoadFixtureAuthoring();
        authoring.Economy.StartingBalance = -1;

        AssertInvalid(authoring, "economy.startingBalance");
    }

    [Theory]
    [InlineData("fixture.letter", "cannot offer a quest item")]
    [InlineData("fixture.letter", "with a positive value")]
    [InlineData("fixture.letter", "with default policies")]
    [InlineData("fixture.missing", "unknown definition 'fixture.missing'")]
    public void VendorOffersMustBePurchasable(string definitionId, string expectedError)
    {
        var authoring = ItemCatalogTestData.LoadFixtureAuthoring();
        authoring.Vendors[0].Offers =
        [
            .. authoring.Vendors[0].Offers,
            new VendorOfferEntry { DefinitionId = definitionId }
        ];

        AssertInvalid(authoring, expectedError);
    }

    [Fact]
    public void DuplicateVendorOfferFailsValidation()
    {
        var authoring = ItemCatalogTestData.LoadFixtureAuthoring();
        authoring.Vendors[0].Offers =
        [
            .. authoring.Vendors[0].Offers,
            new VendorOfferEntry { DefinitionId = "fixture.arrow" }
        ];

        AssertInvalid(authoring, "duplicate definition 'fixture.arrow'");
    }

    [Fact]
    public void VendorCannotBuyQuestItems()
    {
        var authoring = ItemCatalogTestData.LoadFixtureAuthoring();
        authoring.Vendors[0].SaleCategories = [.. authoring.Vendors[0].SaleCategories, "quest_item"];

        AssertInvalid(authoring, "cannot contain 'quest_item'");
    }

    [Fact]
    public void SaleRateAtOrAbovePurchaseRateFailsAcrossVendors()
    {
        var authoring = ItemCatalogTestData.LoadFixtureAuthoring();
        authoring.Vendors =
        [
            authoring.Vendors[0],
            new VendorProfileAuthoringEntry
            {
                Id = "vendor.generous_buyer",
                DisplayName = "Generous Buyer",
                Offers = [],
                PurchasePriceBasisPoints = 20000,
                SalePriceBasisPoints = 10000,
                SaleCategories = ["material"]
            }
        ];

        AssertInvalid(authoring, "can never create currency");
    }

    [Fact]
    public void VendorWithoutOffersOrSaleCategoriesFailsValidation()
    {
        var authoring = ItemCatalogTestData.LoadFixtureAuthoring();
        authoring.Vendors[0].Offers = [];
        authoring.Vendors[0].SaleCategories = [];

        AssertInvalid(authoring, "must offer items or buy at least one category");
    }

    [Fact]
    public void RealCatalogVendorMatchesTheProductDecisions()
    {
        var catalog = ItemCatalogTestData.Compile();
        var vendor = catalog.Vendors.Single(value => value.Id == "vendor.city_general_store");

        Assert.Equal("Gold", catalog.Economy.CurrencyDisplayName);
        Assert.Equal(100, catalog.Economy.StartingBalance);
        Assert.Equal(VendorPricing.BasisPointsScale, vendor.PurchasePriceBasisPoints);
        Assert.Equal(4000, vendor.SalePriceBasisPoints);
        Assert.DoesNotContain(ItemCategoryIds.QuestItem, vendor.SaleCategories);
        Assert.Contains("pickaxe", Definition(catalog, "tool.starter_pickaxe").ToolKinds);
    }

    private static void AssertInvalid(ItemCatalogAuthoringDocument authoring, string expectedError)
    {
        var exception = Assert.Throws<ItemCatalogValidationException>(
            () => ItemCatalogCompiler.Compile(authoring));
        Assert.Contains(exception.Errors, error => error.Contains(expectedError, StringComparison.Ordinal));
    }

    private static ItemDefinitionAuthoringEntry FindDefinition(
        ItemCatalogAuthoringDocument authoring,
        string definitionId)
    {
        return authoring.Definitions.Single(definition => definition.Id == definitionId);
    }

    private static ItemDefinition Definition(ItemCatalogRuntimeDocument catalog, string definitionId)
    {
        return catalog.Definitions.Single(definition => definition.Id == definitionId);
    }
}
