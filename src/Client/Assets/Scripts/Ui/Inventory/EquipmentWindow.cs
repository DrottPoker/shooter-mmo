using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mmorpg.Client.Items;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The Equipment window: the character in the middle on a pedestal, the equipment slots in a
    // column on each side with their names beside them, and the hand, tool, and ammunition
    // slots in a row below. EquipmentPaperDollLayout decides where each slot goes.
    internal sealed class EquipmentWindow
    {
        private const float LabelGap = 6f;
        private const float RowGap = 8f;
        private const float BottomGap = 14f;
        private const float BottomLabelHeight = 22f;

        private readonly InventoryWindows owner;
        private readonly UiTheme theme;
        private readonly TextMeshProUGUI message;
        private readonly RectTransform doll;
        private readonly LayoutElement dollLayout;
        private readonly Dictionary<string, ItemSlotView> slots = new Dictionary<string, ItemSlotView>(StringComparer.Ordinal);
        private readonly Dictionary<string, TextMeshProUGUI> labels = new Dictionary<string, TextMeshProUGUI>(StringComparer.Ordinal);
        private readonly Button headwearToggle;
        private readonly InventoryTooltipTrigger headwearTooltip;
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
            pedestal.rectTransform.anchorMin = new Vector2(0f, 0f);
            pedestal.rectTransform.anchorMax = new Vector2(1f, 0f);
            pedestal.rectTransform.pivot = new Vector2(0.5f, 0f);
            pedestal.rectTransform.sizeDelta = new Vector2(0f, theme.EquipmentCenterWidth * 0.32f);
            pedestal.rectTransform.anchoredPosition = Vector2.zero;
            headwearToggle = UiFactory.IconButton(theme, doll, "HeadwearToggle", theme.HoodToggleIcon, 24f, ToggleHeadwear);
            headwearTooltip = headwearToggle.gameObject.AddComponent<InventoryTooltipTrigger>();
            headwearToggle.gameObject.SetActive(false);
        }

        public UiWindow Window { get; }

        // The area between the slot columns where the character stands.
        public RectTransform PreviewArea { get; }

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
            get { return 2f * CellWidth + theme.EquipmentCenterWidth; }
        }

        private float CellWidth
        {
            get { return theme.SlotSize + LabelGap + theme.EquipmentLabelWidth; }
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
                + " / " + shown.Length.ToString(CultureInfo.InvariantCulture) + " equipped");
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
                labels[slotId].text = InventorySlotPresentation.EquipmentSlotName(state.Catalog, slotId);
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

            foreach (var label in labels.Values)
            {
                label.gameObject.SetActive(false);
            }

            var slotSize = theme.SlotSize;
            var rows = Math.Max(arrangement.Left.Count, arrangement.Right.Count);
            var columnsHeight = rows > 0 ? rows * slotSize + (rows - 1) * RowGap : 0f;
            for (var index = 0; index < arrangement.Left.Count; index++)
            {
                PlaceSideSlot(arrangement.Left[index], 0f, index * (slotSize + RowGap));
            }

            var rightX = InnerWidth - CellWidth;
            for (var index = 0; index < arrangement.Right.Count; index++)
            {
                PlaceSideSlot(arrangement.Right[index], rightX, index * (slotSize + RowGap));
            }

            UiFactory.Place(PreviewArea, new Vector2(CellWidth, 0f), new Vector2(theme.EquipmentCenterWidth, Mathf.Max(columnsHeight, slotSize)));

            var top = columnsHeight + BottomGap;
            var cellWidth = InnerWidth / EquipmentPaperDollLayout.BottomColumns;
            foreach (var row in arrangement.BottomRows)
            {
                for (var index = 0; index < row.Count; index++)
                {
                    var x = index * cellWidth + (cellWidth - slotSize) / 2f;
                    var view = SlotView(row[index]);
                    UiFactory.Place((RectTransform)view.transform, new Vector2(x, top), new Vector2(slotSize, slotSize));
                    var label = Label(row[index]);
                    label.alignment = TextAlignmentOptions.Top;
                    UiFactory.Place(label.rectTransform, new Vector2(index * cellWidth, top + slotSize + 3f), new Vector2(cellWidth, BottomLabelHeight));
                }

                top += slotSize + BottomLabelHeight + RowGap;
            }

            var height = arrangement.BottomRows.Count > 0 ? top - RowGap : columnsHeight;
            dollLayout.preferredHeight = height;
            dollLayout.minHeight = height;
            headwearToggle.transform.SetAsLastSibling();
        }

        private void PlaceSideSlot(string slotId, float x, float y)
        {
            var view = SlotView(slotId);
            UiFactory.Place((RectTransform)view.transform, new Vector2(x, y), new Vector2(theme.SlotSize, theme.SlotSize));
            var label = Label(slotId);
            label.alignment = TextAlignmentOptions.MidlineLeft;
            UiFactory.Place(label.rectTransform, new Vector2(x + theme.SlotSize + LabelGap, y), new Vector2(theme.EquipmentLabelWidth, theme.SlotSize));
            if (string.Equals(slotId, "head", StringComparison.Ordinal))
            {
                UiFactory.Place(
                    (RectTransform)headwearToggle.transform,
                    new Vector2(x + CellWidth - 26f, y + (theme.SlotSize - 24f) / 2f),
                    new Vector2(24f, 24f));
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

        private TextMeshProUGUI Label(string slotId)
        {
            if (!labels.TryGetValue(slotId, out var label))
            {
                label = UiFactory.BodyText(theme, doll, "Label " + slotId, theme.LabelSize, theme.TextColor, TextAlignmentOptions.MidlineLeft);
                label.overflowMode = TextOverflowModes.Ellipsis;
                labels.Add(slotId, label);
            }

            label.gameObject.SetActive(true);
            return label;
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
