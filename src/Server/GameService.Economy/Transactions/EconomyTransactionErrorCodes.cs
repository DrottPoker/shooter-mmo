namespace Mmorpg.GameService.Economy;

// The error codes of the vendor and gathering commands. Their values are client and worker
// protocol; the codes that these commands share with other commands, such as
// vendor_unavailable from the vendor profile load, currency_insufficient, and inventory_full,
// are Items' codes.
internal static class EconomyTransactionErrorCodes
{
    public const string VendorAccessRequired = "vendor_access_required";
    public const string VendorOfferUnavailable = "vendor_offer_unavailable";
    public const string VendorPriceChanged = "vendor_price_changed";
    public const string VendorWillNotBuy = "vendor_will_not_buy";
    public const string GatheringToolRequired = "gathering_tool_required";
    public const string GatheringYieldInvalid = "gathering_yield_invalid";
}
