using Mmorpg.WorldData.Items;

namespace Mmorpg.Shared.Tests;

public sealed class VendorPricingTests
{
    private static readonly VendorProfileDefinition Vendor = new()
    {
        Id = "vendor.test",
        DisplayName = "Test Vendor",
        Offers = [new VendorOfferEntry { DefinitionId = "material.ore" }],
        PurchasePriceBasisPoints = 10000,
        SalePriceBasisPoints = 4000,
        SaleCategories = ["material"]
    };

    [Theory]
    [InlineData(10, 1, 10)]
    [InlineData(10, 7, 70)]
    [InlineData(1, 1000, 1000)]
    public void PurchasePriceIsValueTimesQuantityAtFullRate(long value, int quantity, long expected)
    {
        Assert.True(VendorPricing.TryGetPurchasePrice(Item(value), Vendor, quantity, out var price));
        Assert.Equal(expected, price);
    }

    [Theory]
    [InlineData(10, 1, 4)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 3, 1)]
    [InlineData(1, 100, 40)]
    [InlineData(25, 3, 30)]
    public void SalePriceRoundsTheTotalDown(long value, int quantity, long expected)
    {
        Assert.True(VendorPricing.TryGetSalePrice(Item(value), Vendor, quantity, out var price));
        Assert.Equal(expected, price);
    }

    [Fact]
    public void PurchasePriceRoundsTheTotalUp()
    {
        var vendor = Copy(Vendor);
        vendor.PurchasePriceBasisPoints = 12500;

        Assert.True(VendorPricing.TryGetPurchasePrice(Item(1), vendor, 3, out var price));

        Assert.Equal(4, price);
    }

    [Fact]
    public void PriceAboveTheMaximumBalanceIsRejected()
    {
        var vendor = Copy(Vendor);
        vendor.PurchasePriceBasisPoints = VendorPricing.MaximumPriceBasisPoints;

        Assert.False(VendorPricing.TryGetPurchasePrice(
            Item(ItemCatalogFormat.MaximumItemValue),
            vendor,
            ItemCatalogFormat.MaximumQuantity,
            out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ItemCatalogFormat.MaximumQuantity + 1)]
    public void QuantityOutsideTheItemRangeIsRejected(int quantity)
    {
        Assert.False(VendorPricing.TryGetPurchasePrice(Item(10), Vendor, quantity, out _));
        Assert.False(VendorPricing.TryGetSalePrice(Item(10), Vendor, quantity, out _));
    }

    [Fact]
    public void SaleIsAcceptedOnlyForValuedItemsOfASaleCategory()
    {
        Assert.True(VendorPricing.AcceptsSale(Vendor, Item(10)));
        Assert.False(VendorPricing.AcceptsSale(Vendor, Item(0)));
        var weapon = Item(10);
        weapon.Category = "weapon";
        Assert.False(VendorPricing.AcceptsSale(Vendor, weapon));
    }

    [Fact]
    public void OffersMatchesDefinitionIdsExactly()
    {
        Assert.True(VendorPricing.Offers(Vendor, "material.ore"));
        Assert.False(VendorPricing.Offers(Vendor, "material.ore2"));
    }

    private static ItemDefinition Item(long value)
    {
        return new ItemDefinition
        {
            Id = "material.ore",
            DisplayName = "Ore",
            Category = "material",
            Value = value,
            MaximumStackSize = 100
        };
    }

    private static VendorProfileDefinition Copy(VendorProfileDefinition vendor)
    {
        return new VendorProfileDefinition
        {
            Id = vendor.Id,
            DisplayName = vendor.DisplayName,
            Offers = vendor.Offers,
            PurchasePriceBasisPoints = vendor.PurchasePriceBasisPoints,
            SalePriceBasisPoints = vendor.SalePriceBasisPoints,
            SaleCategories = vendor.SaleCategories
        };
    }
}
