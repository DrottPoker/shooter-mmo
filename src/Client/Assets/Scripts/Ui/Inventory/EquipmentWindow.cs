using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The Equipment window: the character in the middle on a pedestal, the equipment slots in a
    // column on each side, as far from the window's edge on both sides, and the hand, tool, and
    // ammunition slots close together in a row below. A slot shows no name; its tooltip names
    // it. EquipmentPaperDollLayout decides where each slot goes.
    internal sealed class EquipmentWindow
    {
        // Between two slots in a column or a row.
        private const float SlotGap = 8f;

        // Between a slot column and the character.
        private const float ColumnGap = 12f;

        // Between the slot columns and the row below.
        private const float BottomGap = 12f;

        // The pedestal is as wide as the character's picture, so the feet stand on its top.
        private const float PedestalWidth = 150f;

        private const float HeadwearToggleSize = 24f;

        private readonly InventoryWindows owner;
        private readonly UiTheme theme;
        private readonly TextMeshProUGUI message;
        private readonly RectTransform doll;
        private readonly LayoutElement dollLayout;
        private readonly Dictionary<string, ItemSlotView> slots = new Dictionary<string, ItemSlotView>(StringComparer.Ordinal);
        private readonly Button headwearToggle;
        private readonly InventoryTooltipTrigger headwearTooltip;
        private readonly CharacterPreviewDrag previewDrag;
        private string arrangementKey = string.Empty;

        public EquipmentWindow(InventoryWindows owner, RectTransform parent)
        {
            this.owner = owner;
            theme = owner.Theme;
            Window = UiWindow.Create(
                theme,
                parent,
                InventoryWindows.EquipmentWindowName,
                theme.EquipmentIcon,
                "Equipment",
                InnerWidth + 2f * theme.WindowPadding);
            message = UiFactory.BodyText(theme, Window.Body, "Message", theme.BodySize, theme.TextColor, TextAlignmentOptions.TopLeft);
            message.textWrappingMode = TextWrappingModes.Normal;
            doll = UiFactory.Create("PaperDoll", Window.Body);
            dollLayout = UiFactory.Layout(doll, InnerWidth);
            PreviewArea = UiFactory.Create("CharacterPreview", doll);
            var pedestal = UiFactory.Image(PreviewArea, "Pedestal", theme.Pedestal, Color.white);
            pedestal.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            pedestal.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            pedestal.rectTransform.pivot = new Vector2(0.5f, 0f);
            pedestal.rectTransform.sizeDelta = new Vector2(PedestalWidth, PedestalWidth * 0.32f);
            pedestal.rectTransform.anchoredPosition = Vector2.zero;

            // The character stands on the pedestal, as tall as the slot columns and as wide as
            // its picture's shape allows.
            PreviewImage = UiFactory.Create("Figure", PreviewArea).gameObject.AddComponent<RawImage>();
            UiFactory.Fill(PreviewImage.rectTransform);
            var fitter = PreviewImage.gameObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = (float)CharacterPreview.TextureWidth / CharacterPreview.TextureHeight;
            previewDrag = PreviewImage.gameObject.AddComponent<CharacterPreviewDrag>();
            PreviewImage.gameObject.SetActive(false);
            headwearToggle = UiFactory.IconButton(theme, doll, "HeadwearToggle", theme.HoodToggleIcon, HeadwearToggleSize, ToggleHeadwear);
            headwearTooltip = headwearToggle.gameObject.AddComponent<InventoryTooltipTrigger>();
            headwearToggle.gameObject.SetActive(false);
        }

        public UiWindow Window { get; }

        // The area between the slot columns where the character stands.
        public RectTransform PreviewArea { get; }

        // The picture of the character preview, shown once a preview is given.
        public RawImage PreviewImage { get; }

        // Shows the preview's picture on the pedestal, which a drag across turns, or hides it for
        // none.
        public void ShowPreview(CharacterPreview preview)
        {
            var shown = preview != null && preview.Texture != null;
            PreviewImage.texture = shown ? preview.Texture : null;
            previewDrag.Preview = shown ? preview : null;
            PreviewImage.gameObject.SetActive(shown);
        }

        public IReadOnlyDictionary<string, ItemSlotView> Slots
        {
            get { return slots; }
        }

        public Button HeadwearToggle
        {
            get { return headwearToggle; }
        }

        private float InnerWidth
        {
            get { return 2f * (theme.SlotSize + ColumnGap) + theme.EquipmentCenterWidth; }
        }

        public void Refresh()
        {
            var state = owner.State;
            Window.SetDetail(string.Empty);
            if (state?.Catalog == null || state.FullSnapshot == null)
            {
                message.gameObject.SetActive(true);
                message.text = state?.Error?.ToDisplayMessage() ?? CharacterInventoryWindow.StatusText(state);
                doll.gameObject.SetActive(false);
                Window.FitBody();
                return;
            }

            var snapshot = state.FullSnapshot;
            message.gameObject.SetActive(state.Error != null);
            message.text = state.Error?.ToDisplayMessage() ?? string.Empty;
            doll.gameObject.SetActive(true);
            var equipment = new Dictionary<string, InventoryEquipmentSlot>(StringComparer.Ordinal);
            foreach (var equipmentSlot in snapshot.Equipment)
            {
                if (equipmentSlot != null && !equipment.ContainsKey(equipmentSlot.SlotId))
                {
                    equipment.Add(equipmentSlot.SlotId, equipmentSlot);
                }
            }

            var arrangement = EquipmentPaperDollLayout.Arrange(equipment.Keys);
            // The count covers the shown slots, so the hidden Back slot is left out.
            var shown = arrangement.All.ToArray();
            Window.SetDetail(
                shown.Count(slotId => equipment[slotId].Item != null).ToString(CultureInfo.InvariantCulture)
                + " / " + shown.Length.ToString(CultureInfo.InvariantCulture));
            var key = string.Join(",", arrangement.Left) + "|" + string.Join(",", arrangement.Right) + "|"
                + string.Join(";", arrangement.BottomRows.Select(row => string.Join(",", row)));
            if (!string.Equals(key, arrangementKey, StringComparison.Ordinal))
            {
                Arrange(arrangement);
                arrangementKey = key;
            }

            var blocked = InventorySlotPresentation.FindBlockedEquipmentSlots(state.Catalog, snapshot.Equipment);
            foreach (var slotId in arrangement.All)
            {
                slots[slotId].Bind(owner.EquipmentSlot(equipment[slotId], blocked));
            }

            RefreshHeadwearToggle(state);
            Window.FitBody();
        }

        // The equipped arrows follow every shot in place.
        public void UpdateLiveAmmunition()
        {
            var state = owner.State;
            var ammunition = state?.FullSnapshot?.Equipment.FirstOrDefault(InventoryWindows.IsAmmunitionSlot);
            if (ammunition?.Item == null || !slots.TryGetValue(ammunition.SlotId, out var view))
            {
                return;
            }

            var item = ammunition.Item;
            var live = owner.LiveAmmunitionQuantity(item);
            view.SetQuantity(InventorySlotPresentation.LiveQuantityText(item.Quantity, live));
            view.SetTooltip(InventorySlotPresentation.BuildItemTooltip(
                state.Catalog,
                item,
                InventorySlotPresentation.LiveQuantityDescription(
                    "Equipped in " + InventorySlotPresentation.EquipmentSlotName(state.Catalog, ammunition.SlotId),
                    item.Quantity,
                    live)));
        }

        private void Arrange(PaperDollArrangement arrangement)
        {
            foreach (var view in slots.Values)
            {
                view.gameObject.SetActive(false);
            }

            var slotSize = theme.SlotSize;
            var rows = Math.Max(arrangement.Left.Count, arrangement.Right.Count);
            var columnsHeight = rows > 0 ? rows * slotSize + (rows - 1) * SlotGap : 0f;
            for (var index = 0; index < arrangement.Left.Count; index++)
            {
                PlaceSideSlot(arrangement.Left[index], 0f, index * (slotSize + SlotGap));
            }

            var rightX = InnerWidth - slotSize;
            for (var index = 0; index < arrangement.Right.Count; index++)
            {
                PlaceSideSlot(arrangement.Right[index], rightX, index * (slotSize + SlotGap));
            }

            UiFactory.Place(
                PreviewArea,
                new Vector2(slotSize + ColumnGap, 0f),
                new Vector2(theme.EquipmentCenterWidth, Mathf.Max(columnsHeight, slotSize)));

            // Each row below stands in the middle, its slots as close as those of a column.
            var top = columnsHeight + BottomGap;
            foreach (var row in arrangement.BottomRows)
            {
                var rowWidth = row.Count * slotSize + (row.Count - 1) * SlotGap;
                var left = (InnerWidth - rowWidth) / 2f;
                for (var index = 0; index < row.Count; index++)
                {
                    var view = SlotView(row[index]);
                    UiFactory.Place(
                        (RectTransform)view.transform,
                        new Vector2(left + index * (slotSize + SlotGap), top),
                        new Vector2(slotSize, slotSize));
                }

                top += slotSize + SlotGap;
            }

            var height = arrangement.BottomRows.Count > 0 ? top - SlotGap : columnsHeight;
            dollLayout.preferredHeight = height;
            dollLayout.minHeight = height;
            headwearToggle.transform.SetAsLastSibling();
        }

        // The hood button stands beside the head slot, toward the character.
        private void PlaceSideSlot(string slotId, float x, float y)
        {
            var view = SlotView(slotId);
            UiFactory.Place((RectTransform)view.transform, new Vector2(x, y), new Vector2(theme.SlotSize, theme.SlotSize));
            if (string.Equals(slotId, "head", StringComparison.Ordinal))
            {
                var toggleX = x < InnerWidth / 2f ? x + theme.SlotSize + ColumnGap : x - ColumnGap - HeadwearToggleSize;
                UiFactory.Place(
                    (RectTransform)headwearToggle.transform,
                    new Vector2(toggleX, y + (theme.SlotSize - HeadwearToggleSize) / 2f),
                    new Vector2(HeadwearToggleSize, HeadwearToggleSize));
            }
        }

        private ItemSlotView SlotView(string slotId)
        {
            if (!slots.TryGetValue(slotId, out var view))
            {
                view = ItemSlotView.Create(theme, doll, owner.DragCoordinator, owner.Tooltip);
                slots.Add(slotId, view);
            }

            view.gameObject.SetActive(true);
            return view;
        }

        // Lowers or raises worn headwear that can be pushed back, such as a hood; the server
        // stores it and every client shows it.
        private void RefreshHeadwearToggle(InventoryClientState state)
        {
            var bodyLook = owner.BodyLook;
            var headwear = InventorySlotPresentation.LowerableHeadwear(state.FullSnapshot.Equipment, state.Catalog);
            var shown = bodyLook != null && bodyLook.Received && headwear != null && slots.ContainsKey("head");
            headwearToggle.gameObject.SetActive(shown);
            if (!shown)
            {
                return;
            }

            var name = state.Catalog.GetDisplayName(headwear.DefinitionId);
            headwearTooltip.Configure(
                owner.Tooltip,
                (bodyLook.HeadwearLowered ? "<b>Raise " : "<b>Lower ") + name + "</b>");
            headwearToggle.targetGraphic.color = bodyLook.HeadwearLowered ? theme.AccentColor : theme.TitleColor;
        }

        private void ToggleHeadwear()
        {
            var bodyLook = owner.BodyLook;
            if (bodyLook != null && bodyLook.Received)
            {
                bodyLook.RequestHeadwear(!bodyLook.HeadwearLowered);
            }
        }
    }
}
