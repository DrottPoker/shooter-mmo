using System;

namespace Mmorpg.WorldData.Items
{
    // The one vendor price rule. Unity shows prices with it and GameService charges them
    // with it, so a displayed price and a charged price can never differ for the same catalog.
    public static class VendorPricing
    {
        public const int BasisPointsScale = 10000;
        public const int MaximumPriceBasisPoints = 1_000_000;

        // GameService stores the character balance with the same limit.
        public const long MaximumCurrencyBalance = 1_000_000_000_000L;

        // The total the player pays for a quantity of an offer, rounded up.
        public static bool TryGetPurchasePrice(
            ItemDefinition definition,
            VendorProfileDefinition vendor,
            int quantity,
            out long price)
        {
            RequireArguments(definition, vendor);
            return TryApplyRate(
                definition.Value,
                quantity,
                vendor.PurchasePriceBasisPoints,
                true,
                out price);
        }

        // The total the player receives for selling a quantity, rounded down.
        public static bool TryGetSalePrice(
            ItemDefinition definition,
            VendorProfileDefinition vendor,
            int quantity,
            out long price)
        {
            RequireArguments(definition, vendor);
            return TryApplyRate(
                definition.Value,
                quantity,
                vendor.SalePriceBasisPoints,
                false,
                out price);
        }

        public static bool Offers(VendorProfileDefinition vendor, string definitionId)
        {
            if (vendor == null)
            {
                throw new ArgumentNullException(nameof(vendor));
            }

            foreach (var offer in vendor.Offers ?? Array.Empty<VendorOfferEntry>())
            {
                if (offer != null
                    && string.Equals(offer.DefinitionId, definitionId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // Whether the vendor buys this definition from players at all. Item policies, such as
        // protection or insurance, are checked separately on the item instance.
        public static bool AcceptsSale(VendorProfileDefinition vendor, ItemDefinition definition)
        {
            RequireArguments(definition, vendor);
            if (definition.Value <= 0)
            {
                return false;
            }

            foreach (var category in vendor.SaleCategories ?? Array.Empty<string>())
            {
                if (string.Equals(category, definition.Category, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryApplyRate(
            long value,
            int quantity,
            int basisPoints,
            bool roundUp,
            out long price)
        {
            price = 0;
            if (value < 0
                || value > ItemCatalogFormat.MaximumItemValue
                || quantity <= 0
                || quantity > ItemCatalogFormat.MaximumQuantity
                || basisPoints < 0
                || basisPoints > MaximumPriceBasisPoints)
            {
                return false;
            }

            // Decimal keeps the exact product of the bounded inputs, and the scale is a power
            // of ten, so the division is exact before rounding.
            var exact = (decimal)value * quantity * basisPoints / BasisPointsScale;
            var rounded = roundUp ? Math.Ceiling(exact) : Math.Floor(exact);
            if (rounded > MaximumCurrencyBalance)
            {
                return false;
            }

            price = (long)rounded;
            return true;
        }

        private static void RequireArguments(ItemDefinition definition, VendorProfileDefinition vendor)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (vendor == null)
            {
                throw new ArgumentNullException(nameof(vendor));
            }
        }
    }
}
