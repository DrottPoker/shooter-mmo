using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
    public sealed class VendorOffer
    {
        public VendorOffer(ItemDefinition definition, long unitPrice)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            UnitPrice = unitPrice;
        }

        public ItemDefinition Definition { get; }

        public string DefinitionId
        {
            get { return Definition.Id; }
        }

        public long UnitPrice { get; }

        // One purchase creates at most one full stack.
        public int MaximumQuantity
        {
            get { return Definition.MaximumStackSize; }
        }
    }

    // Vendor offers and prices from the bundled catalog, priced with the same rule
    // GameService charges with. These checks are advisory: GameService recomputes every price
    // and rejects a difference with vendor_price_changed.
    public static class VendorClientRules
    {
        public const string DefaultCurrencyName = "Gold";

        public static bool TryResolve(
            ClientItemCatalog catalog,
            string vendorProfileId,
            out VendorProfileDefinition vendor,
            out string error)
        {
            vendor = null;
            error = string.Empty;
            if (catalog == null)
            {
                error = "The item catalog is unavailable.";
                return false;
            }

            vendor = Array.Find(
                catalog.Gameplay.Vendors ?? Array.Empty<VendorProfileDefinition>(),
                candidate => candidate != null
                    && string.Equals(candidate.Id, vendorProfileId, StringComparison.Ordinal));
            if (vendor == null)
            {
                error = "This vendor is not in the bundled item catalog. Update the client.";
                return false;
            }

            return true;
        }

        public static IReadOnlyList<VendorOffer> ListOffers(
            ClientItemCatalog catalog,
            VendorProfileDefinition vendor)
        {
            if (catalog == null || vendor == null)
            {
                return Array.Empty<VendorOffer>();
            }

            var offers = new List<VendorOffer>();
            foreach (var entry in vendor.Offers ?? Array.Empty<VendorOfferEntry>())
            {
                if (entry != null
                    && catalog.TryGetDefinition(entry.DefinitionId, out var definition)
                    && VendorPricing.TryGetPurchasePrice(definition, vendor, 1, out var unitPrice))
                {
                    offers.Add(new VendorOffer(definition, unitPrice));
                }
            }

            return offers;
        }

        public static string CurrencyName(ClientItemCatalog catalog)
        {
            var name = catalog?.Gameplay.Economy?.CurrencyDisplayName;
            return string.IsNullOrWhiteSpace(name) ? DefaultCurrencyName : name;
        }

        public static string FormatAmount(ClientItemCatalog catalog, long amount)
        {
            return amount.ToString("N0", CultureInfo.InvariantCulture) + " " + CurrencyName(catalog);
        }

        public static bool CanBuy(
            ClientItemCatalog catalog,
            VendorProfileDefinition vendor,
            CharacterInventorySnapshot snapshot,
            string definitionId,
            int quantity,
            out long price,
            out string reason)
        {
            price = 0;
            reason = string.Empty;
            if (catalog == null || vendor == null || snapshot == null)
            {
                reason = "The vendor or the inventory is not loaded yet.";
                return false;
            }

            if (!VendorPricing.Offers(vendor, definitionId)
                || !catalog.TryGetDefinition(definitionId, out var definition))
            {
                reason = "The vendor does not sell this item.";
                return false;
            }

            if (quantity < 1 || quantity > definition.MaximumStackSize)
            {
                reason = "Choose a quantity from 1 to "
                    + definition.MaximumStackSize.ToString(CultureInfo.InvariantCulture) + ".";
                return false;
            }

            if (!VendorPricing.TryGetPurchasePrice(definition, vendor, quantity, out price))
            {
                reason = "The price is out of range.";
                return false;
            }

            if (price > snapshot.Currency)
            {
                reason = "You need " + FormatAmount(catalog, price) + ".";
                return false;
            }

            if (!FitsHardCap(snapshot, definition, quantity))
            {
                reason = "That would be too heavy to carry.";
                return false;
            }

            return true;
        }

        public static bool CanSell(
            ClientItemCatalog catalog,
            VendorProfileDefinition vendor,
            CharacterInventorySnapshot snapshot,
            InventoryItem item,
            InventoryItemLocation location,
            int quantity,
            out long price,
            out string reason)
        {
            price = 0;
            reason = string.Empty;
            if (catalog == null || vendor == null || snapshot == null || item == null || location == null)
            {
                reason = "The vendor or the inventory is not loaded yet.";
                return false;
            }

            if (!IsCarried(snapshot, item, location))
            {
                reason = "Only unequipped items you carry can be sold.";
                return false;
            }

            if (!catalog.TryGetDefinition(item.DefinitionId, out var definition)
                || !VendorPricing.AcceptsSale(vendor, definition)
                || string.Equals(definition.Category, ItemCategoryIds.QuestItem, StringComparison.Ordinal))
            {
                reason = "The vendor does not buy this item.";
                return false;
            }

            if (!ItemPolicyRules.Evaluate(definition, ToPolicyStates(item)).CanSellToVendor)
            {
                reason = "Protected and insured items cannot be sold.";
                return false;
            }

            if (quantity < 1 || quantity > item.Quantity)
            {
                reason = "Choose a quantity from 1 to "
                    + item.Quantity.ToString(CultureInfo.InvariantCulture) + ".";
                return false;
            }

            if (!VendorPricing.TryGetSalePrice(definition, vendor, quantity, out price))
            {
                reason = "The price is out of range.";
                return false;
            }

            if (price == 0)
            {
                reason = "The vendor pays nothing for this quantity. Sell more at once.";
                return false;
            }

            if (price > VendorPricing.MaximumCurrencyBalance - snapshot.Currency)
            {
                reason = "Your balance cannot hold more " + CurrencyName(catalog) + ".";
                return false;
            }

            return true;
        }

        // Items in the inventory, the equipped Bag, or the Secure Container. An equipped item,
        // a banked item, and the equipped Bag itself are not carried for a sale.
        private static bool IsCarried(
            CharacterInventorySnapshot snapshot,
            InventoryItem item,
            InventoryItemLocation location)
        {
            if (location.Kind != InventoryItemLocationKind.Container)
            {
                return false;
            }

            if (string.Equals(location.ContainerType, ItemContainerTypeIds.BagContents, StringComparison.Ordinal))
            {
                return snapshot.EquippedBag != null
                    && snapshot.EquippedBag.Contents.ContainerId == location.ContainerId;
            }

            return string.Equals(location.ContainerType, ItemContainerTypeIds.PermanentInventory, StringComparison.Ordinal)
                || string.Equals(location.ContainerType, ItemContainerTypeIds.SecureContainer, StringComparison.Ordinal);
        }

        private static IEnumerable<ItemPolicyState> ToPolicyStates(InventoryItem item)
        {
            return item.Policies.Select(policy => new ItemPolicyState(
                policy.Kind,
                policy.Status,
                policy.ProtectionSource,
                string.Empty));
        }

        private static bool FitsHardCap(
            CharacterInventorySnapshot snapshot,
            ItemDefinition definition,
            int quantity)
        {
            try
            {
                var addedWeight = ItemWeightRules.CalculateStackWeight(definition.UnitWeight, quantity);
                return EncumbranceRules.IsAllowedVoluntaryTransition(
                    snapshot.CarriedWeight,
                    snapshot.CarryCapacity,
                    checked(snapshot.CarriedWeight + addedWeight),
                    snapshot.CarryCapacity);
            }
            catch (OverflowException)
            {
                return false;
            }
        }
    }
}
