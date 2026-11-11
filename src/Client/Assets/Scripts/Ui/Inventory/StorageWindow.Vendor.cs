using System;
using System.Collections.Generic;
using System.Globalization;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The vendor tab: the offers from the bundled catalog with their prices, and a Sell slot
    // that offers a carried item after a confirmation. Trades are allowed only while the vendor
    // interaction stays open, and every trade carries the total shown here.
    internal sealed partial class StorageWindow
    {
        private readonly Dictionary<string, string> vendorQuantityTexts =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<OfferRow> offerRows = new List<OfferRow>();
        private string vendorCapabilityId = string.Empty;
        private Guid pendingSaleItemId;
        private string pendingSaleQuantityText = "1";
        private TextMeshProUGUI vendorBalance;
        private TextMeshProUGUI tradeMessage;
        private ItemSlotGrid sellGrid;
        private TMP_InputField sellQuantity;
        private Button sellButton;
        private RectTransform sellRow;

        public Guid PendingSaleItemId
        {
            get { return pendingSaleItemId; }
        }

        public void SelectVendorCapability(string capabilityId)
        {
            vendorCapabilityId = capabilityId ?? string.Empty;
            pendingSaleItemId = Guid.Empty;
        }

        // Puts a carried item into the Sell slot, so the player chooses how many to sell.
        public void OfferForSale(InventoryDragPayload payload)
        {
            var state = owner.State;
            var capability = ActiveVendorCapability();
            if (capability == null
                || !VendorClientRules.TryResolve(state.Catalog, capability.ProfileId, out var vendor, out _))
            {
                owner.Say(ClientText.Of("ui/vendor/talk-to-sell"));
                return;
            }

            if (!owner.Interactions.CanOfferToVendor(vendor, payload, out var reason))
            {
                owner.Say(reason);
                return;
            }

            if (!state.TryFindItem(payload.ItemInstanceId, out var item, out _))
            {
                owner.Say(ClientText.Of("ui/items/item-unavailable"));
                return;
            }

            pendingSaleItemId = item.ItemInstanceId;
            pendingSaleQuantityText = (payload.Quantity ?? item.Quantity).ToString(CultureInfo.InvariantCulture);
            owner.Refresh();
        }

        private RealtimeWorldActorCapability ActiveVendorCapability()
        {
            var active = owner.WorldInteraction?.State.ActiveInteraction;
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

        private void RefreshVendor(InventoryClientState state)
        {
            SetFooter(string.Empty, null);
            var capability = ActiveVendorCapability();
            if (capability == null)
            {
                Window.SetTitle(owner.Text("ui/vendor/trade"));
                Window.SetDetail(string.Empty);
                ShowMessage(owner.Text("ui/vendor/hint"), true);
                return;
            }

            if (!VendorClientRules.TryResolve(state.Catalog, capability.ProfileId, out var vendor, out var error))
            {
                Window.SetTitle(owner.Text("ui/vendor/trade"));
                ShowMessage(owner.Text(error), true);
                return;
            }

            var snapshot = state.FullSnapshot;
            if (snapshot == null)
            {
                ShowMessage(CharacterInventoryWindow.StatusText(owner.Texts, state), true);
                return;
            }

            InventoryItem pendingItem = null;
            InventoryItemLocation pendingLocation = null;
            if (pendingSaleItemId != Guid.Empty
                && !state.TryFindItem(pendingSaleItemId, out pendingItem, out pendingLocation))
            {
                pendingSaleItemId = Guid.Empty;
                pendingItem = null;
            }

            var vendorName = state.Catalog.GetVendorName(vendor.Id);
            Window.SetTitle(vendorName);
            Window.SetDetail(string.Empty);
            ShowMessage(string.Empty, false);
            var offers = VendorClientRules.ListOffers(state.Catalog, vendor);
            var key = "vendor:" + vendor.Id + ":" + offers.Count.ToString(CultureInfo.InvariantCulture);
            EnsureSections(key, () => BuildVendor(offers));

            var idle = InventoryDropPlanner.CanMoveItem(state, null);
            vendorBalance.text = owner.Text(
                "ui/vendor/balance",
                MessageArgument.Text(
                    "amount",
                    "<color=" + UiFactory.ColorTag(theme.GoldTextColor) + ">"
                        + owner.Text(VendorClientRules.FormatAmount(snapshot.Currency)) + "</color>"));
            for (var index = 0; index < offerRows.Count && index < offers.Count; index++)
            {
                BindOffer(offerRows[index], state, capability, vendor, offers[index], idle);
            }

            sections[1].Detail.text = owner.Text(
                "ui/vendor/sale-rate",
                MessageArgument.Text("rate", RateText(vendor.SalePriceBasisPoints)));
            var sellSpec = owner.ItemSpec(pendingItem, "vendor:sell");
            sellSpec.Dimmed = false;
            sellSpec.DropValidator = (InventoryDragPayload candidate, out ClientText reason) =>
                owner.Interactions.CanOfferToVendor(vendor, candidate, out reason);
            sellSpec.OnDrop = OfferForSale;
            var vendorArgument = MessageArgument.Text("vendor", vendorName);
            sellSpec.Tooltip = pendingItem != null
                ? InventorySlotPresentation.BuildItemTooltip(
                    state.Catalog,
                    pendingItem,
                    owner.Text("ui/vendor/offered-to", vendorArgument))
                : "<b>" + owner.Text("ui/vendor/sell") + "</b>\n" + owner.Text("ui/vendor/sell-hint", vendorArgument);
            sellGrid.Bind(new[] { sellSpec });

            sellRow.gameObject.SetActive(pendingItem != null);
            if (pendingItem != null)
            {
                if (!string.Equals(sellQuantity.text, pendingSaleQuantityText, StringComparison.Ordinal))
                {
                    sellQuantity.SetTextWithoutNotify(pendingSaleQuantityText);
                }

                sellButton.interactable = idle;
                UiFactory.SetButtonLabel(sellButton, SellLabel(state, vendor, pendingItem, pendingLocation, pendingSaleQuantityText));
            }

            var interaction = owner.WorldInteraction.State;
            tradeMessage.text = interaction.LastMessage ?? string.Empty;
            tradeMessage.color = string.IsNullOrEmpty(interaction.LastErrorCode) ? theme.AccentColor : theme.DangerColor;
            tradeMessage.gameObject.SetActive(tradeMessage.text.Length > 0);
        }

        private void BuildVendor(IReadOnlyList<VendorOffer> offers)
        {
            offerRows.Clear();
            var buy = AddSection(owner.Text("ui/vendor/buy"), null, false);
            vendorBalance = UiFactory.BodyText(theme, buy.Root, "Balance", theme.LabelSize, theme.TextColor, TextAlignmentOptions.MidlineLeft);
            foreach (var _ in offers)
            {
                offerRows.Add(CreateOfferRow(buy.Root));
            }

            var sell = AddSection(owner.Text("ui/vendor/sell"), null, false);
            sellGrid = new ItemSlotGrid(theme, sell.Root, CharacterInventoryWindow.Columns, owner.DragCoordinator, owner.Tooltip, "SellGrid");
            var row = UiFactory.Row(sell.Root, "SellRow", theme.ButtonHeight, 6f);
            sellRow = row.GetComponent<RectTransform>();
            sellQuantity = UiFactory.Field(theme, row.transform, "SellQuantity", "1", null, null, TMP_InputField.ContentType.IntegerNumber, TextAlignmentOptions.Center);
            UiFactory.Layout(sellQuantity, 64f, theme.FieldHeight, 0f);
            sellQuantity.onValueChanged.AddListener(value =>
            {
                pendingSaleQuantityText = value;
                var state = owner.State;
                var capability = ActiveVendorCapability();
                if (state != null
                    && capability != null
                    && VendorClientRules.TryResolve(state.Catalog, capability.ProfileId, out var vendor, out _)
                    && state.TryFindItem(pendingSaleItemId, out var item, out var location))
                {
                    UiFactory.SetButtonLabel(sellButton, SellLabel(state, vendor, item, location, value));
                }
            });
            sellButton = UiFactory.Button(theme, row.transform, owner.Text("ui/vendor/sell"), SellPending, UiButtonTone.Primary);
            UiFactory.Button(theme, row.transform, owner.Text("ui/common/cancel"), () =>
            {
                pendingSaleItemId = Guid.Empty;
                owner.Refresh();
            }, UiButtonTone.Normal, 80f);
            tradeMessage = UiFactory.BodyText(theme, sell.Root, "TradeMessage", theme.LabelSize, theme.AccentColor, TextAlignmentOptions.MidlineLeft);
            tradeMessage.textWrappingMode = TextWrappingModes.Normal;
        }

        private OfferRow CreateOfferRow(Transform parent)
        {
            var row = UiFactory.Row(parent, "Offer", theme.SlotSize, 8f);
            row.childForceExpandHeight = false;
            var grid = new ItemSlotGrid(theme, row.transform, 1, owner.DragCoordinator, owner.Tooltip, "OfferSlot");
            UiFactory.Layout(grid.Rect, theme.SlotSize, theme.SlotSize, 0f);
            var label = UiFactory.BodyText(theme, row.transform, "Label", theme.LabelSize, theme.TextColor, TextAlignmentOptions.MidlineLeft);
            label.textWrappingMode = TextWrappingModes.Normal;
            UiFactory.Layout(label, flexibleWidth: 1f);
            var field = UiFactory.Field(theme, row.transform, "Quantity", "1", null, null, TMP_InputField.ContentType.IntegerNumber, TextAlignmentOptions.Center);
            UiFactory.Layout(field, 52f, theme.FieldHeight, 0f);
            var offerRow = new OfferRow { Slot = grid, Label = label, Quantity = field };
            offerRow.Buy = UiFactory.Button(theme, row.transform, owner.Text("ui/vendor/buy"), () => BuyOffer(offerRow), UiButtonTone.Normal, 104f);
            return offerRow;
        }

        private void BindOffer(
            OfferRow row,
            InventoryClientState state,
            RealtimeWorldActorCapability capability,
            VendorProfileDefinition vendor,
            VendorOffer offer,
            bool idle)
        {
            row.DefinitionId = offer.DefinitionId;
            row.Capability = capability;
            row.Vendor = vendor;
            var name = state.Catalog.GetDisplayName(offer.DefinitionId);
            var unitPrice = owner.Text(
                "ui/vendor/unit-price",
                MessageArgument.Text("price", owner.Text(VendorClientRules.FormatAmount(offer.UnitPrice))));
            var spec = new ItemSlotSpec
            {
                Key = "vendor:offer:" + offer.DefinitionId,
                Item = new InventoryItem(Guid.Empty, offer.DefinitionId, 1, 0, Array.Empty<InventoryPolicy>()),
                DisplayName = name,
                Icon = state.Catalog.LoadIcon(offer.DefinitionId),
                FrameColor = ItemRarityPresentation.FrameColor(theme, state.Catalog, offer.DefinitionId),
                Tooltip = "<b>" + name + "</b>\n" + unitPrice
            };
            row.Slot.Bind(new[] { spec });
            row.Label.text = name + "\n<size=85%><color=" + UiFactory.ColorTag(theme.MutedTextColor) + ">"
                + unitPrice + "</color></size>";
            if (!vendorQuantityTexts.TryGetValue(offer.DefinitionId, out var quantityText))
            {
                quantityText = "1";
            }

            if (!row.Quantity.isFocused && !string.Equals(row.Quantity.text, quantityText, StringComparison.Ordinal))
            {
                row.Quantity.SetTextWithoutNotify(quantityText);
            }

            row.Quantity.onValueChanged.RemoveAllListeners();
            row.Quantity.onValueChanged.AddListener(value =>
            {
                vendorQuantityTexts[offer.DefinitionId] = value;
                UiFactory.SetButtonLabel(row.Buy, BuyLabel(owner.State, vendor, offer.DefinitionId, value));
            });
            row.Buy.interactable = idle;
            UiFactory.SetButtonLabel(row.Buy, BuyLabel(state, vendor, offer.DefinitionId, quantityText));
        }

        private void BuyOffer(OfferRow row)
        {
            vendorQuantityTexts.TryGetValue(row.DefinitionId, out var quantityText);
            if (!int.TryParse(quantityText ?? "1", NumberStyles.None, CultureInfo.InvariantCulture, out var quantity))
            {
                owner.Say(ClientText.Of("ui/vendor/whole-quantity"));
                return;
            }

            owner.Report(owner.Interactions.Buy(row.Capability, row.Vendor, row.DefinitionId, quantity));
        }

        private void SellPending()
        {
            var state = owner.State;
            var capability = ActiveVendorCapability();
            if (capability == null
                || !VendorClientRules.TryResolve(state.Catalog, capability.ProfileId, out var vendor, out _))
            {
                owner.Say(ClientText.Of("ui/vendor/talk-to-sell"));
                return;
            }

            if (!state.TryFindItem(pendingSaleItemId, out _, out _))
            {
                pendingSaleItemId = Guid.Empty;
                owner.Say(ClientText.Of("ui/items/item-unavailable"));
                owner.Refresh();
                return;
            }

            if (!int.TryParse(pendingSaleQuantityText, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity))
            {
                owner.Say(ClientText.Of("ui/vendor/whole-quantity"));
                return;
            }

            var result = owner.Interactions.Sell(capability, vendor, pendingSaleItemId, quantity);
            if (result.Sent)
            {
                pendingSaleItemId = Guid.Empty;
            }

            owner.Report(result);
        }

        private string BuyLabel(
            InventoryClientState state,
            VendorProfileDefinition vendor,
            string definitionId,
            string quantityText)
        {
            return state != null
                && int.TryParse(quantityText, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity)
                && state.Catalog.TryGetDefinition(definitionId, out var definition)
                && quantity >= 1
                && VendorPricing.TryGetPurchasePrice(definition, vendor, quantity, out var price)
                    ? owner.Text("ui/vendor/buy-for", MessageArgument.Text("price", owner.Text(VendorClientRules.FormatAmount(price))))
                    : owner.Text("ui/vendor/buy");
        }

        private string SellLabel(
            InventoryClientState state,
            VendorProfileDefinition vendor,
            InventoryItem item,
            InventoryItemLocation location,
            string quantityText)
        {
            var name = MessageArgument.Text("item", state.Catalog.GetDisplayName(item.DefinitionId));
            if (!int.TryParse(quantityText, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity))
            {
                return owner.Text("ui/vendor/sell-item", name);
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
                ? owner.Text(
                    "ui/vendor/sell-for",
                    MessageArgument.Text("quantity", quantity.ToString(CultureInfo.InvariantCulture)),
                    MessageArgument.Text("price", owner.Text(VendorClientRules.FormatAmount(price))))
                : owner.Text("ui/vendor/sell-item", name);
        }

        private static string RateText(int basisPoints)
        {
            return (basisPoints / 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";
        }

        private sealed class OfferRow
        {
            public ItemSlotGrid Slot;
            public TextMeshProUGUI Label;
            public TMP_InputField Quantity;
            public Button Buy;
            public string DefinitionId = string.Empty;
            public RealtimeWorldActorCapability Capability;
            public VendorProfileDefinition Vendor;
        }
    }
}
