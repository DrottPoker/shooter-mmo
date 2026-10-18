using System;
using System.Collections.Generic;
using System.Globalization;
using Mmorpg.Client.Items;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The Vendor context: offers from the bundled catalog with their prices, and a drop
    // area that sells a carried item after a confirmation. Trades are allowed only while the
    // vendor interaction stays open, and every trade carries the total shown here.
    public sealed partial class TemporaryInventoryPanel
    {
        private readonly Dictionary<string, string> vendorQuantityTexts =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private string vendorCapabilityId = string.Empty;
        private Guid pendingSaleItemId;
        private string pendingSaleQuantityText = "1";

        private void OnInventoryCapabilityOpened(RealtimeWorldActorCapability capability)
        {
            var next = capability.Kind switch
            {
                RealtimeWorldActorCapabilityKind.Bank => InventoryContextKind.Bank,
                RealtimeWorldActorCapabilityKind.RecoveryStorage => InventoryContextKind.RecoveryStorage,
                RealtimeWorldActorCapabilityKind.Vendor => InventoryContextKind.Vendor,
                _ => InventoryContextKind.None
            };
            if (next == InventoryContextKind.None)
            {
                return;
            }

            if (next == InventoryContextKind.Vendor)
            {
                vendorCapabilityId = capability.Id;
                pendingSaleItemId = Guid.Empty;
            }

            if (!isOpen || mode != InventoryPanelMode.Complete)
            {
                Show(InventoryPanelMode.Complete);
            }

            SetContext(next);
        }

        // Trade results and closures arrive through the interaction state.
        private void OnWorldInteractionStateChanged()
        {
            if (context == InventoryContextKind.Vendor)
            {
                Rebuild();
            }
        }

        private RealtimeWorldActorCapability ActiveVendorCapability()
        {
            var active = worldInteractionController?.State.ActiveInteraction;
            if (active == null)
            {
                return null;
            }

            RealtimeWorldActorCapability fallback = null;
            foreach (var capability in active.Capabilities)
            {
                if (capability.Kind != RealtimeWorldActorCapabilityKind.Vendor || !capability.IsAvailable)
                {
                    continue;
                }

                if (string.Equals(capability.Id, vendorCapabilityId, StringComparison.Ordinal))
                {
                    return capability;
                }

                fallback ??= capability;
            }

            return fallback;
        }

        private void DrawVendor(Transform parent, InventoryClientState state)
        {
            var capability = ActiveVendorCapability();
            if (capability == null)
            {
                CreateMessage(
                    parent,
                    "Talk to a vendor, such as Mira in the city, and choose Trade.",
                    TemporaryUiKit.TextSecondary);
                return;
            }

            if (!VendorClientRules.TryResolve(state.Catalog, capability.ProfileId, out var vendor, out var error))
            {
                CreateMessage(parent, error, TemporaryUiKit.DangerColor);
                return;
            }

            var snapshot = state.FullSnapshot;
            if (snapshot == null)
            {
                CreateMessage(parent, StatusText(state), TemporaryUiKit.TextPrimary);
                return;
            }

            var idle = state.CanMutate
                && worldInteractionController.State.PendingOperationId == Guid.Empty;
            CreateSubheader(parent, vendor.DisplayName, "You have " + VendorClientRules.FormatAmount(state.Catalog, snapshot.Currency));
            foreach (var offer in VendorClientRules.ListOffers(state.Catalog, vendor))
            {
                DrawVendorOffer(parent, state, capability, vendor, offer, idle);
            }

            CreateSubheader(
                parent,
                "Sell",
                "Pays " + RateText(vendor.SalePriceBasisPoints) + " of an item's value");
            DrawVendorSale(parent, state, capability, vendor, idle);

            var interaction = worldInteractionController.State;
            if (!string.IsNullOrWhiteSpace(interaction.LastMessage))
            {
                CreateMessage(
                    parent,
                    interaction.LastMessage,
                    string.IsNullOrEmpty(interaction.LastErrorCode)
                        ? TemporaryUiKit.SuccessColor
                        : TemporaryUiKit.DangerColor);
            }
        }

        private void DrawVendorOffer(
            Transform parent,
            InventoryClientState state,
            RealtimeWorldActorCapability capability,
            VendorProfileDefinition vendor,
            VendorOffer offer,
            bool idle)
        {
            if (!vendorQuantityTexts.TryGetValue(offer.DefinitionId, out var quantityText))
            {
                quantityText = "1";
            }

            var row = TemporaryUiKit.CreateRow(parent, 36f, 6f);
            var label = TemporaryUiKit.CreateLabel(
                row,
                "<b>" + state.Catalog.GetDisplayName(offer.DefinitionId) + "</b>    <color="
                    + TemporaryUiKit.ColorTag(TemporaryUiKit.TextMuted) + ">"
                    + VendorClientRules.FormatAmount(state.Catalog, offer.UnitPrice) + " each</color>",
                14,
                FontStyle.Normal,
                TemporaryUiKit.TextPrimary,
                36f);
            label.GetComponent<LayoutElement>().flexibleWidth = 1f;
            var input = TemporaryUiKit.CreateIntegerField(row, quantityText, 64f);
            var button = TemporaryUiKit.CreateButton(
                row,
                BuyLabel(state, vendor, offer.DefinitionId, quantityText),
                () => BuyOffer(capability, vendor, offer),
                idle,
                TemporaryButtonTone.Normal,
                170f);
            var buttonText = button.GetComponentInChildren<Text>();
            // The button names the total the purchase will carry, so it follows the field
            // without a rebuild that would interrupt typing.
            input.onValueChanged.AddListener(value =>
            {
                vendorQuantityTexts[offer.DefinitionId] = value;
                buttonText.text = BuyLabel(state, vendor, offer.DefinitionId, value);
            });
        }

        private static string BuyLabel(
            InventoryClientState state,
            VendorProfileDefinition vendor,
            string definitionId,
            string quantityText)
        {
            return int.TryParse(quantityText, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity)
                && state.Catalog.TryGetDefinition(definitionId, out var definition)
                && quantity >= 1
                && VendorPricing.TryGetPurchasePrice(definition, vendor, quantity, out var price)
                    ? "Buy for " + VendorClientRules.FormatAmount(state.Catalog, price)
                    : "Buy";
        }

        private void BuyOffer(
            RealtimeWorldActorCapability capability,
            VendorProfileDefinition vendor,
            VendorOffer offer)
        {
            var state = controller.State;
            vendorQuantityTexts.TryGetValue(offer.DefinitionId, out var quantityText);
            if (!int.TryParse(quantityText ?? "1", NumberStyles.None, CultureInfo.InvariantCulture, out var quantity))
            {
                RejectDrop("Enter a whole quantity.");
                return;
            }

            if (!VendorClientRules.CanBuy(
                    state.Catalog,
                    vendor,
                    state.FullSnapshot,
                    offer.DefinitionId,
                    quantity,
                    out var price,
                    out var reason))
            {
                RejectDrop(reason);
                return;
            }

            var sent = worldInteractionController.TryPurchase(
                capability.Id,
                offer.DefinitionId,
                quantity,
                price,
                state.KnownItemStateRevision,
                out var error);
            ExecuteTrade(
                sent,
                error,
                "Buying " + quantity.ToString(CultureInfo.InvariantCulture) + " "
                    + state.Catalog.GetDisplayName(offer.DefinitionId) + "...");
        }

        private void DrawVendorSale(
            Transform parent,
            InventoryClientState state,
            RealtimeWorldActorCapability capability,
            VendorProfileDefinition vendor,
            bool idle)
        {
            InventoryItem pendingItem = null;
            InventoryItemLocation pendingLocation = null;
            if (pendingSaleItemId != Guid.Empty
                && !state.TryFindItem(pendingSaleItemId, out pendingItem, out pendingLocation))
            {
                pendingSaleItemId = Guid.Empty;
                pendingItem = null;
            }

            var grid = CreateGrid(parent, StorageColumns, 1, SlotSpacing, TextAnchor.UpperLeft);
            CreateSlot(grid, new SlotSpec
            {
                Key = "vendor:sell",
                Item = pendingItem,
                CornerLabel = "Sell",
                EmptyLabel = "Sell",
                Interactable = false,
                DropValidator = (InventoryDragPayload candidate, out string reason) =>
                    CanDropOnVendor(state, vendor, candidate, out reason),
                OnDrop = OnVendorDrop,
                Tooltip = "<b>Sell</b>\nDrop a carried item here to see what "
                    + vendor.DisplayName + " pays for it."
            });

            if (pendingItem == null)
            {
                CreateMessage(
                    parent,
                    "Drag an item from your inventory, Bag, or Secure Container here to sell it.",
                    TemporaryUiKit.TextMuted);
                return;
            }

            var row = TemporaryUiKit.CreateRow(parent, 36f, 6f);
            var input = TemporaryUiKit.CreateIntegerField(row, pendingSaleQuantityText, 64f);
            var confirm = TemporaryUiKit.CreateButton(
                row,
                SellLabel(state, vendor, pendingItem, pendingLocation, pendingSaleQuantityText),
                () => SellPending(capability, vendor),
                idle,
                TemporaryButtonTone.Active);
            var confirmText = confirm.GetComponentInChildren<Text>();
            var item = pendingItem;
            var location = pendingLocation;
            input.onValueChanged.AddListener(value =>
            {
                pendingSaleQuantityText = value;
                confirmText.text = SellLabel(state, vendor, item, location, value);
            });
            TemporaryUiKit.CreateButton(
                row,
                "Cancel",
                () =>
                {
                    pendingSaleItemId = Guid.Empty;
                    localStatus = "Sale cancelled.";
                    Rebuild();
                },
                true,
                TemporaryButtonTone.Quiet,
                90f);
        }

        private static string SellLabel(
            InventoryClientState state,
            VendorProfileDefinition vendor,
            InventoryItem item,
            InventoryItemLocation location,
            string quantityText)
        {
            var name = state.Catalog.GetDisplayName(item.DefinitionId);
            if (!int.TryParse(quantityText, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity))
            {
                return "Sell " + name;
            }

            return VendorClientRules.CanSell(
                    state.Catalog,
                    vendor,
                    state.FullSnapshot,
                    item,
                    location,
                    quantity,
                    out var price,
                    out _)
                ? "Sell " + quantity.ToString(CultureInfo.InvariantCulture) + " " + name
                    + " for " + VendorClientRules.FormatAmount(state.Catalog, price)
                : "Sell " + name;
        }

        private bool CanDropOnVendor(
            InventoryClientState state,
            VendorProfileDefinition vendor,
            InventoryDragPayload payload,
            out string reason)
        {
            reason = string.Empty;
            if (!state.CanMutate)
            {
                reason = "Inventory mutation is unavailable while state is loading or busy.";
                return false;
            }

            if (payload.Kind != InventoryDragPayloadKind.Item
                || !state.TryFindItem(payload.ItemInstanceId, out var item, out var location))
            {
                reason = "Only a carried item can be sold.";
                return false;
            }

            return VendorClientRules.CanSell(
                state.Catalog,
                vendor,
                state.FullSnapshot,
                item,
                location,
                item.Quantity,
                out _,
                out reason);
        }

        private void OnVendorDrop(InventoryDragPayload payload)
        {
            if (!controller.State.TryFindItem(payload.ItemInstanceId, out var item, out _))
            {
                RejectDrop("The item is no longer available.");
                return;
            }

            pendingSaleItemId = item.ItemInstanceId;
            pendingSaleQuantityText = item.Quantity.ToString(CultureInfo.InvariantCulture);
            localStatus = "Choose how many to sell, then confirm.";
            Rebuild();
        }

        private void SellPending(
            RealtimeWorldActorCapability capability,
            VendorProfileDefinition vendor)
        {
            var state = controller.State;
            if (!state.TryFindItem(pendingSaleItemId, out var item, out var location))
            {
                pendingSaleItemId = Guid.Empty;
                RejectDrop("The item is no longer available.");
                return;
            }

            if (!int.TryParse(pendingSaleQuantityText, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity))
            {
                RejectDrop("Enter a whole quantity.");
                return;
            }

            if (!VendorClientRules.CanSell(
                    state.Catalog,
                    vendor,
                    state.FullSnapshot,
                    item,
                    location,
                    quantity,
                    out var price,
                    out var reason))
            {
                RejectDrop(reason);
                return;
            }

            var sent = worldInteractionController.TrySell(
                capability.Id,
                item.ItemInstanceId,
                item.Revision,
                quantity,
                price,
                state.KnownItemStateRevision,
                out var error);
            if (sent)
            {
                pendingSaleItemId = Guid.Empty;
            }

            ExecuteTrade(
                sent,
                error,
                "Selling " + quantity.ToString(CultureInfo.InvariantCulture) + " "
                    + state.Catalog.GetDisplayName(item.DefinitionId) + "...");
        }

        private void ExecuteTrade(bool sent, string error, string success)
        {
            localStatus = sent ? success : error;
            Rebuild();
        }

        private static string RateText(int basisPoints)
        {
            return (basisPoints / 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";
        }
    }
}
