using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The Inventory window: a search field, the carry meter, Permanent Inventory in rows of five
    // without a heading, the equipped Bag's slots below in the same columns, and the balance at
    // the bottom. The window grows with the Bag.
    internal sealed class CharacterInventoryWindow
    {
        public const int Columns = 5;

        private readonly InventoryWindows owner;
        private readonly UiTheme theme;
        private readonly TMP_InputField search;
        private readonly TextMeshProUGUI message;
        private readonly RectTransform carry;
        private readonly TextMeshProUGUI weightText;
        private readonly TextMeshProUGUI loadText;
        private readonly RectTransform meterTrack;
        private readonly Image meterFill;
        private readonly RectTransform meterMarker;
        private readonly RectTransform permanentSection;
        private readonly ItemSlotGrid permanentGrid;
        private readonly RectTransform bagSection;
        private readonly TextMeshProUGUI bagTitle;
        private readonly TextMeshProUGUI bagDetail;
        private readonly ItemSlotGrid bagGrid;
        private readonly TextMeshProUGUI noBag;
        private readonly TextMeshProUGUI balance;

        public CharacterInventoryWindow(InventoryWindows owner, RectTransform parent)
        {
            this.owner = owner;
            theme = owner.Theme;
            var gridWidth = ItemSlotGrid.Width(theme, Columns);
            Window = UiWindow.Create(
                theme,
                parent,
                InventoryWindows.InventoryWindowName,
                theme.InventoryIcon,
                owner.Text("ui/inventory/inventory-title"),
                gridWidth + 2f * theme.WindowPadding);
            var body = Window.Body;

            search = UiFactory.Field(
                theme,
                body,
                "Search",
                string.Empty,
                owner.Text("ui/inventory/search-placeholder"),
                theme.SearchIcon,
                TMP_InputField.ContentType.Standard);
            search.onValueChanged.AddListener(owner.SetSearch);

            message = UiFactory.BodyText(theme, body, "Message", theme.BodySize, theme.TextColor, TextAlignmentOptions.TopLeft);
            message.textWrappingMode = TextWrappingModes.Normal;

            carry = UiFactory.Column(body, "Carry", 6f).GetComponent<RectTransform>();
            var numbers = UiFactory.Row(carry, "Numbers", theme.BodySize + 6f, 8f);
            weightText = UiFactory.BodyText(theme, numbers.transform, "Weight", theme.BodySize + 2f, theme.TextColor, TextAlignmentOptions.MidlineLeft);
            UiFactory.Layout(weightText, flexibleWidth: 1f);
            loadText = UiFactory.BodyText(theme, numbers.transform, "Load", theme.SmallSize + 1f, theme.MutedTextColor, TextAlignmentOptions.MidlineRight);
            meterTrack = UiFactory.Create("Meter", carry);
            UiFactory.Layout(meterTrack, preferredHeight: theme.MeterHeight);
            var track = meterTrack.gameObject.AddComponent<Image>();
            UiFactory.SetSprite(track, theme.MeterTrack, theme.WindowTint);
            if (theme.MeterTrack == null)
            {
                track.color = new Color(0.02f, 0.03f, 0.03f, 1f);
            }

            meterFill = UiFactory.Image(meterTrack, "Fill", theme.MeterFill, theme.MeterNormalColor);
            if (theme.MeterFill == null)
            {
                meterFill.color = theme.MeterNormalColor;
            }

            meterMarker = UiFactory.Image(meterTrack, "FullCapacityMarker", theme.MeterMarker, theme.TitleColor).rectTransform;

            permanentSection = UiFactory.Column(body, "PermanentInventory", 8f).GetComponent<RectTransform>();
            permanentGrid = new ItemSlotGrid(theme, permanentSection, Columns, owner.DragCoordinator, owner.Tooltip, "PermanentGrid");

            bagSection = UiFactory.Column(body, "Bag", 8f).GetComponent<RectTransform>();
            bagTitle = UiFactory.SectionHeading(theme, bagSection, owner.Text("ui/inventory/bag"), theme.BagIcon, out bagDetail);
            bagGrid = new ItemSlotGrid(theme, bagSection, Columns, owner.DragCoordinator, owner.Tooltip, "BagGrid");
            noBag = UiFactory.BodyText(
                theme,
                bagSection,
                "NoBag",
                theme.LabelSize,
                theme.MutedTextColor,
                TextAlignmentOptions.MidlineLeft,
                owner.Text("ui/inventory/no-bag"));
            noBag.textWrappingMode = TextWrappingModes.Normal;

            UiFactory.Spacer(Window.Footer);
            if (theme.CoinIcon != null)
            {
                var coin = UiFactory.Image(Window.Footer, "Coin", theme.CoinIcon, Color.white);
                UiFactory.Layout(coin, theme.BodySize + 6f, theme.BodySize + 6f, 0f);
            }

            balance = UiFactory.BodyText(theme, Window.Footer, "Balance", theme.BodySize + 2f, theme.GoldTextColor, TextAlignmentOptions.MidlineRight);
            UiFactory.Layout(balance, preferredWidth: -1f, flexibleWidth: 0f);
        }

        public UiWindow Window { get; }

        public TMP_InputField Search
        {
            get { return search; }
        }

        public ItemSlotGrid PermanentGrid
        {
            get { return permanentGrid; }
        }

        public ItemSlotGrid BagGrid
        {
            get { return bagGrid; }
        }

        // Whether the player types into the search field, so window keys stay text.
        public bool IsTyping
        {
            get { return search != null && search.isFocused; }
        }

        public static string StatusText(TextCatalog texts, InventoryClientState state)
        {
            return state?.Status switch
            {
                InventoryClientStatus.Loading => texts.Get("ui/inventory/status-loading"),
                InventoryClientStatus.Busy => texts.Get("ui/inventory/status-busy"),
                InventoryClientStatus.UpdateRequired => texts.Get("ui/inventory/status-update-required"),
                InventoryClientStatus.Error => texts.Get("ui/inventory/status-error"),
                InventoryClientStatus.Ready => texts.Get("ui/inventory/status-ready"),
                _ => texts.Get("ui/inventory/status-not-loaded")
            };
        }

        public void Refresh()
        {
            var state = owner.State;
            if (state?.Catalog == null || state.FullSnapshot == null)
            {
                ShowMessageOnly(state?.Error?.ToDisplayMessage()
                    ?? (state?.Catalog == null
                        ? owner.Text("ui/inventory/catalog-unavailable")
                        : StatusText(owner.Texts, state)));
                return;
            }

            var snapshot = state.FullSnapshot;
            message.text = state.Error?.ToDisplayMessage() ?? string.Empty;
            message.gameObject.SetActive(message.text.Length > 0);
            carry.gameObject.SetActive(true);
            permanentSection.gameObject.SetActive(true);
            bagSection.gameObject.SetActive(true);
            balance.text = owner.Text(VendorClientRules.FormatAmount(snapshot.Currency));
            RefreshCarryMeter(CarryLoadPresentation.From(snapshot));

            var permanent = snapshot.PermanentInventory;
            permanentGrid.Bind(permanent.Slots
                .Select(slot => owner.ContainerSlot(permanent, slot, "inventory"))
                .ToArray());

            var bag = snapshot.EquippedBag;
            noBag.gameObject.SetActive(bag == null);
            bagGrid.SetActive(bag != null);
            if (bag == null)
            {
                UiFactory.SetHeading(bagTitle, owner.Text("ui/inventory/bag"));
                bagDetail.text = string.Empty;
                bagGrid.Bind(System.Array.Empty<ItemSlotSpec>());
            }
            else
            {
                UiFactory.SetHeading(bagTitle, state.Catalog.GetDisplayName(bag.Item.DefinitionId));
                bagDetail.text = UsedSlotsText(bag.Contents);
                bagGrid.Bind(bag.Contents.Slots
                    .Select(slot => owner.ContainerSlot(bag.Contents, slot, "bag"))
                    .ToArray());
            }

            Window.FitBody();
        }

        public static string UsedSlotsText(InventoryContainer container)
        {
            return container.Slots.Count(slot => slot.Item != null).ToString(CultureInfo.InvariantCulture)
                + " / " + container.Slots.Count.ToString(CultureInfo.InvariantCulture);
        }

        // The meter spans zero to the hard cap with a marker at full carry capacity: the
        // normal color up to full capacity, the slowed color beyond it, and the hard cap color
        // at the cap.
        private void RefreshCarryMeter(CarryLoadPresentation load)
        {
            var color = load.Level switch
            {
                CarryLoadLevel.Slowed => theme.MeterSlowedColor,
                CarryLoadLevel.AtHardCap => theme.MeterHardCapColor,
                _ => theme.MeterNormalColor
            };
            weightText.text = load.WeightText;
            var colorTag = "<color=" + UiFactory.ColorTag(color) + ">";
            loadText.text = owner.Text(
                "ui/inventory/carry-load",
                MessageArgument.Text("load", colorTag + load.LoadPercentText + "</color>"),
                MessageArgument.Text("speed", colorTag + load.SpeedPercentText + "</color>"));
            meterFill.color = color;
            UiFactory.Stretch(meterFill.rectTransform, Vector2.zero, new Vector2(load.FillFraction, 1f), new Vector2(1f, 1f), new Vector2(-1f, -1f));
            meterFill.enabled = load.FillFraction > 0f;
            var marker = load.FullCapacityMarkerFraction;
            UiFactory.Stretch(meterMarker, new Vector2(marker, 0f), new Vector2(marker, 1f), new Vector2(-1f, -3f), new Vector2(1f, 3f));
        }

        private void ShowMessageOnly(string text)
        {
            message.text = text ?? string.Empty;
            message.gameObject.SetActive(true);
            carry.gameObject.SetActive(false);
            permanentSection.gameObject.SetActive(false);
            bagSection.gameObject.SetActive(false);
            balance.text = string.Empty;
            Window.FitBody();
        }
    }
}
