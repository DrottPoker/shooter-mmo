using System;
using Mmorpg.Client.Items;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // Widget builders for the temporary inventory panel: modules, slot grids, slots, and
    // the carry meter. Gameplay decisions stay in TemporaryInventoryPanel.cs.
    public sealed partial class TemporaryInventoryPanel
    {
        private static readonly Color SlotEmptyColor = new Color(0.095f, 0.105f, 0.13f, 1f);
        private static readonly Color SlotOccupiedColor = new Color(0.15f, 0.175f, 0.215f, 1f);
        private static readonly Color SlotSelectedColor = new Color(0.20f, 0.36f, 0.56f, 1f);
        private static readonly Color SlotBlockedColor = new Color(0.22f, 0.075f, 0.075f, 1f);
        private static readonly Color SlotBorderColor = new Color(0.25f, 0.29f, 0.35f, 1f);
        private static readonly Color SlotEmptyBorderColor = new Color(0.20f, 0.225f, 0.27f, 1f);
        private static readonly Color QuantityColor = new Color(1f, 0.90f, 0.62f, 1f);
        private const string QuantityLabelName = "Quantity";

        private void DrawCarryMeter(Transform parent, CarryLoadPresentation load)
        {
            var meter = CreateUiObject("CarryMeter", parent);
            var layout = meter.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.padding = new RectOffset(0, 0, 2, 2);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var track = CreateUiObject("Track", meter.transform);
            var trackImage = track.AddComponent<Image>();
            trackImage.color = TemporaryUiKit.InputColor;
            trackImage.raycastTarget = false;
            TemporaryUiKit.AddOutline(trackImage, SlotBorderColor);
            track.AddComponent<LayoutElement>().preferredHeight = 14f;

            var levelColor = LoadLevelColor(load.Level);
            var fill = CreateUiObject("Fill", track.transform).AddComponent<Image>();
            fill.color = levelColor;
            fill.raycastTarget = false;
            Stretch(
                fill.rectTransform,
                Vector2.zero,
                new Vector2(load.FillFraction, 1f),
                Vector2.zero,
                Vector2.zero);

            var marker = CreateUiObject("FullCapacityMarker", track.transform).AddComponent<Image>();
            marker.color = TemporaryUiKit.TextSecondary;
            marker.raycastTarget = false;
            Stretch(
                marker.rectTransform,
                new Vector2(load.FullCapacityMarkerFraction, 0f),
                new Vector2(load.FullCapacityMarkerFraction, 1f),
                new Vector2(-1f, -3f),
                new Vector2(1f, 3f));

            var note = load.Level switch
            {
                CarryLoadLevel.Slowed => "  (slowed)",
                CarryLoadLevel.AtHardCap => "  (at the carry limit)",
                _ => string.Empty
            };
            TemporaryUiKit.CreateLabel(
                meter.transform,
                "Weight " + load.WeightText
                    + "    <color=" + TemporaryUiKit.ColorTag(levelColor) + ">Load "
                    + load.LoadPercentText + note + "</color>"
                    + "    Speed " + load.SpeedPercentText,
                14,
                FontStyle.Normal,
                TemporaryUiKit.TextSecondary,
                20f);
        }

        private static Color LoadLevelColor(CarryLoadLevel level)
        {
            return level switch
            {
                CarryLoadLevel.Slowed => TemporaryUiKit.WarningColor,
                CarryLoadLevel.AtHardCap => TemporaryUiKit.DangerColor,
                _ => TemporaryUiKit.SuccessColor
            };
        }

        // A color per item category stands in for icons until item art exists.
        private static Color CategoryColor(string categoryId)
        {
            return categoryId switch
            {
                "weapon" => new Color(0.86f, 0.40f, 0.33f, 1f),
                "ammunition" => new Color(0.90f, 0.68f, 0.30f, 1f),
                "armor" => new Color(0.46f, 0.63f, 0.86f, 1f),
                "jewelry" => new Color(0.76f, 0.53f, 0.92f, 1f),
                "tool" => new Color(0.72f, 0.56f, 0.38f, 1f),
                "bag" => new Color(0.64f, 0.52f, 0.36f, 1f),
                "material" => new Color(0.58f, 0.61f, 0.66f, 1f),
                "potion" => new Color(0.90f, 0.42f, 0.62f, 1f),
                "food" => new Color(0.52f, 0.80f, 0.44f, 1f),
                "quest_item" => new Color(0.96f, 0.84f, 0.36f, 1f),
                _ => TemporaryUiKit.TextMuted
            };
        }

        private RectTransform CreateModule(
            string objectName,
            Vector2 anchorMin,
            Vector2 anchorMax,
            bool withFooter,
            out RectTransform panelRect,
            out Text header,
            out Text headerDetail,
            out RectTransform footer,
            out RectTransform scrollRectTransform)
        {
            var panel = CreateUiObject(objectName, rootObject.transform);
            panelRect = panel.GetComponent<RectTransform>();
            Stretch(panelRect, anchorMin, anchorMax, Vector2.zero, Vector2.zero);
            var background = panel.AddComponent<Image>();
            background.color = TemporaryUiKit.PanelColor;
            TemporaryUiKit.AddOutline(background, TemporaryUiKit.PanelBorderColor);

            var headerBand = CreateUiObject("Header", panel.transform);
            Stretch(
                headerBand.GetComponent<RectTransform>(),
                new Vector2(0f, 1f),
                Vector2.one,
                new Vector2(0f, -HeaderHeight),
                Vector2.zero);
            var headerImage = headerBand.AddComponent<Image>();
            headerImage.color = TemporaryUiKit.HeaderColor;
            headerImage.raycastTarget = false;
            header = TemporaryUiKit.AddText(
                CreateUiObject("Title", headerBand.transform),
                string.Empty,
                20,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                TemporaryUiKit.TextPrimary);
            Stretch(header.rectTransform, Vector2.zero, Vector2.one, new Vector2(14f, 0f), new Vector2(-14f, 0f));
            headerDetail = TemporaryUiKit.AddText(
                CreateUiObject("Detail", headerBand.transform),
                string.Empty,
                14,
                FontStyle.Normal,
                TextAnchor.MiddleRight,
                TemporaryUiKit.TextSecondary);
            Stretch(headerDetail.rectTransform, Vector2.zero, Vector2.one, new Vector2(14f, 0f), new Vector2(-14f, 0f));

            footer = null;
            if (withFooter)
            {
                var footerObject = CreateUiObject("Footer", panel.transform);
                footer = footerObject.GetComponent<RectTransform>();
                footer.anchorMin = Vector2.zero;
                footer.anchorMax = new Vector2(1f, 0f);
                footer.pivot = new Vector2(0.5f, 0f);
                footer.anchoredPosition = new Vector2(0f, FooterMargin);
                footer.sizeDelta = new Vector2(-2f * FooterMargin, 0f);
                var footerImage = footerObject.AddComponent<Image>();
                footerImage.color = TemporaryUiKit.HeaderColor;
                footerImage.raycastTarget = false;
                var footerLayout = footerObject.AddComponent<VerticalLayoutGroup>();
                footerLayout.padding = new RectOffset(10, 10, 8, 8);
                footerLayout.spacing = 6f;
                footerLayout.childControlWidth = true;
                footerLayout.childControlHeight = true;
                footerLayout.childForceExpandWidth = true;
                footerLayout.childForceExpandHeight = false;
            }

            var scrollObject = CreateUiObject("Scroll", panel.transform);
            scrollRectTransform = scrollObject.GetComponent<RectTransform>();
            Stretch(
                scrollRectTransform,
                Vector2.zero,
                Vector2.one,
                new Vector2(10f, 10f),
                new Vector2(-10f, -(HeaderHeight + 8f)));
            var scrollRect = scrollObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 30f;

            var viewport = CreateUiObject("Viewport", scrollObject.transform);
            var viewportRect = viewport.GetComponent<RectTransform>();
            Stretch(viewportRect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var viewportImage = viewport.AddComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0.01f);
            viewport.AddComponent<Mask>().showMaskGraphic = false;
            scrollRect.viewport = viewportRect;

            var content = CreateUiObject("Content", viewport.transform).GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(
                (int)ContentPadding,
                (int)ContentPadding,
                6,
                6);
            layout.spacing = 8f;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollRect.content = content;
            return content;
        }

        // The footer grows with its content and the scroll area above it shrinks to match.
        private void FitCharacterFooter()
        {
            if (characterFooter == null || characterScroll == null)
            {
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(characterFooter);
            var height = LayoutUtility.GetPreferredHeight(characterFooter);
            characterFooter.sizeDelta = new Vector2(characterFooter.sizeDelta.x, height);
            characterScroll.offsetMin = new Vector2(
                characterScroll.offsetMin.x,
                FooterMargin + (height > 0f ? height + 8f : 0f));
        }

        // Slots share one size, fitted to five columns in the character module.
        private Vector2 ResolveSlotSize()
        {
            var width = characterContent != null ? characterContent.rect.width : 0f;
            if (width <= 0f)
            {
                return new Vector2(MaximumSlotWidth, SlotHeight);
            }

            var available = width - 2f * ContentPadding - SlotSpacing * (StorageColumns - 1);
            return new Vector2(
                Mathf.Clamp(Mathf.Floor(available / StorageColumns), MinimumSlotWidth, MaximumSlotWidth),
                SlotHeight);
        }

        private RectTransform CreateGrid(
            Transform parent,
            int columns,
            int cellCount,
            float spacing,
            TextAnchor alignment)
        {
            var gridObject = CreateUiObject("SlotGrid", parent);
            var grid = gridObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = slotSize;
            grid.spacing = new Vector2(spacing, spacing);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = Mathf.Max(1, columns);
            grid.childAlignment = alignment;
            var rows = Mathf.Max(1, Mathf.CeilToInt(cellCount / (float)Mathf.Max(1, columns)));
            gridObject.AddComponent<LayoutElement>().preferredHeight =
                rows * slotSize.y + (rows - 1) * spacing;
            return gridObject.GetComponent<RectTransform>();
        }

        private GameObject CreateSlot(Transform parent, SlotSpec spec)
        {
            var catalog = controller?.State.Catalog;
            var item = spec.Item;
            var visualState = item == null
                ? spec.Blocked ? InventorySlotVisualState.Blocked : InventorySlotVisualState.Empty
                : spec.Selected ? InventorySlotVisualState.Selected : InventorySlotVisualState.Occupied;
            var slotObject = CreateUiObject("Slot " + spec.Key, parent);
            var image = slotObject.AddComponent<Image>();
            var baseColor = visualState switch
            {
                InventorySlotVisualState.Blocked => SlotBlockedColor,
                InventorySlotVisualState.Empty => SlotEmptyColor,
                InventorySlotVisualState.Selected => SlotSelectedColor,
                _ => SlotOccupiedColor
            };
            image.color = baseColor;
            TemporaryUiKit.AddOutline(
                image,
                visualState switch
                {
                    InventorySlotVisualState.Selected => TemporaryUiKit.AccentColor,
                    InventorySlotVisualState.Blocked => new Color(0.55f, 0.20f, 0.18f, 1f),
                    InventorySlotVisualState.Empty => SlotEmptyBorderColor,
                    _ => SlotBorderColor
                });
            slotObject.AddComponent<InventorySlotView>().Configure(
                spec.Key,
                visualState,
                item?.DefinitionId);
            var button = slotObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;
            button.interactable = spec.Interactable;
            if (spec.OnClick != null)
            {
                var onClick = spec.OnClick;
                button.onClick.AddListener(() => onClick());
            }

            Sprite icon = null;
            var dragLabel = string.Empty;
            if (item != null)
            {
                var displayName = catalog != null
                    ? catalog.GetDisplayName(item.DefinitionId)
                    : item.DefinitionId;
                var quantity = spec.QuantityText ?? InventorySlotPresentation.QuantityText(item.Quantity);
                dragLabel = displayName + (quantity.Length > 0 ? "  x" + quantity : string.Empty);
                var strip = CreateUiObject("Category", slotObject.transform).AddComponent<Image>();
                strip.color = CategoryColor(InventorySlotPresentation.CategoryId(catalog, item.DefinitionId));
                strip.raycastTarget = false;
                Stretch(strip.rectTransform, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -3f), Vector2.zero);

                var nameLeft = 6f;
                icon = catalog?.LoadIcon(item.DefinitionId);
                if (icon != null)
                {
                    var iconImage = CreateUiObject("Icon", slotObject.transform).AddComponent<Image>();
                    iconImage.sprite = icon;
                    iconImage.preserveAspect = true;
                    iconImage.raycastTarget = false;
                    var iconRect = iconImage.rectTransform;
                    iconRect.anchorMin = new Vector2(0f, 0.5f);
                    iconRect.anchorMax = new Vector2(0f, 0.5f);
                    iconRect.pivot = new Vector2(0f, 0.5f);
                    iconRect.anchoredPosition = new Vector2(5f, 0f);
                    iconRect.sizeDelta = new Vector2(slotSize.y - 22f, slotSize.y - 22f);
                    nameLeft = slotSize.y - 12f;
                }

                var nameText = TemporaryUiKit.AddText(
                    CreateUiObject("Name", slotObject.transform),
                    displayName,
                    14,
                    FontStyle.Normal,
                    TextAnchor.MiddleCenter,
                    TemporaryUiKit.TextPrimary);
                nameText.horizontalOverflow = HorizontalWrapMode.Wrap;
                nameText.verticalOverflow = VerticalWrapMode.Truncate;
                nameText.resizeTextForBestFit = true;
                nameText.resizeTextMinSize = 10;
                nameText.resizeTextMaxSize = 14;
                Stretch(nameText.rectTransform, Vector2.zero, Vector2.one, new Vector2(nameLeft, 15f), new Vector2(-6f, -15f));
                // A replaced quantity, such as a live count, keeps its label to update.
                if (quantity.Length > 0 || spec.QuantityText != null)
                {
                    var quantityText = TemporaryUiKit.AddText(
                        CreateUiObject(QuantityLabelName, slotObject.transform),
                        quantity,
                        14,
                        FontStyle.Bold,
                        TextAnchor.LowerRight,
                        QuantityColor);
                    Stretch(quantityText.rectTransform, Vector2.zero, Vector2.one, new Vector2(4f, 2f), new Vector2(-6f, -2f));
                }

                var badge = InventorySlotPresentation.PolicyBadge(item);
                if (badge.Length > 0)
                {
                    var badgeText = TemporaryUiKit.AddText(
                        CreateUiObject("PolicyBadge", slotObject.transform),
                        badge,
                        12,
                        FontStyle.Bold,
                        TextAnchor.UpperRight,
                        TemporaryUiKit.SuccessColor);
                    Stretch(badgeText.rectTransform, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-6f, -5f));
                }

                if (!string.IsNullOrEmpty(spec.CornerLabel))
                {
                    var cornerText = TemporaryUiKit.AddText(
                        CreateUiObject("SlotLabel", slotObject.transform),
                        spec.CornerLabel,
                        11,
                        FontStyle.Normal,
                        TextAnchor.UpperLeft,
                        TemporaryUiKit.TextMuted);
                    Stretch(cornerText.rectTransform, Vector2.zero, Vector2.one, new Vector2(6f, 4f), new Vector2(-18f, -5f));
                }
            }
            else if (!string.IsNullOrEmpty(spec.EmptyLabel) || spec.Blocked)
            {
                var emptyText = TemporaryUiKit.AddText(
                    CreateUiObject("EmptyLabel", slotObject.transform),
                    spec.Blocked ? spec.EmptyLabel + "\n<b>Blocked</b>" : spec.EmptyLabel,
                    13,
                    FontStyle.Normal,
                    TextAnchor.MiddleCenter,
                    spec.Blocked ? TemporaryUiKit.DangerColor : TemporaryUiKit.TextMuted);
                emptyText.horizontalOverflow = HorizontalWrapMode.Wrap;
                Stretch(emptyText.rectTransform, Vector2.zero, Vector2.one, new Vector2(4f, 2f), new Vector2(-4f, -2f));
            }

            if (spec.DragPayload != null)
            {
                slotObject.AddComponent<InventoryDragSource>().Configure(
                    dragCoordinator,
                    spec.DragPayload,
                    dragLabel,
                    icon,
                    spec.CanDrag);
            }

            if (spec.DropValidator != null)
            {
                slotObject.AddComponent<InventoryDropTarget>().Configure(
                    dragCoordinator,
                    image,
                    baseColor,
                    spec.DropValidator,
                    spec.OnDrop,
                    RejectDrop);
            }

            if (!string.IsNullOrWhiteSpace(spec.Tooltip))
            {
                slotObject.AddComponent<InventoryTooltipTrigger>().Configure(tooltip, spec.Tooltip);
            }

            return slotObject;
        }

        private void CreateSubheader(Transform parent, string title, string detail)
        {
            var row = CreateUiObject("Subheader", parent);
            row.AddComponent<LayoutElement>().preferredHeight = 26f;
            var titleText = TemporaryUiKit.AddText(
                CreateUiObject("Title", row.transform),
                title,
                16,
                FontStyle.Bold,
                TextAnchor.LowerLeft,
                TemporaryUiKit.TextPrimary);
            Stretch(titleText.rectTransform, Vector2.zero, Vector2.one, new Vector2(1f, 2f), Vector2.zero);
            var detailText = TemporaryUiKit.AddText(
                CreateUiObject("Detail", row.transform),
                detail,
                13,
                FontStyle.Normal,
                TextAnchor.LowerRight,
                TemporaryUiKit.TextMuted);
            Stretch(detailText.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, 3f), new Vector2(-2f, 0f));
        }

        private static void CreateMessage(Transform parent, string text, Color color)
        {
            TemporaryUiKit.CreateLabel(parent, text, 14, FontStyle.Normal, color, 22f);
        }

        private static GameObject CreateUiObject(string name, Transform parent)
        {
            return TemporaryUiKit.CreateUiObject(name, parent);
        }

        private static void Stretch(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            TemporaryUiKit.Stretch(rect, anchorMin, anchorMax, offsetMin, offsetMax);
        }

        // Hidden children leave layout at once; destruction itself happens at frame end.
        private static void ClearChildren(Transform parent)
        {
            if (parent == null)
            {
                return;
            }

            for (var index = parent.childCount - 1; index >= 0; index--)
            {
                var child = parent.GetChild(index).gameObject;
                child.SetActive(false);
                Destroy(child);
            }
        }

        private sealed class SlotSpec
        {
            public string Key = string.Empty;
            public InventoryItem Item;

            // Replaces the committed quantity, such as the live arrow count; null keeps it.
            public string QuantityText;
            public string CornerLabel = string.Empty;
            public string EmptyLabel = string.Empty;
            public bool Blocked;
            public bool Selected;
            public bool Interactable;
            public Action OnClick;
            public InventoryDragPayload DragPayload;
            public bool CanDrag;
            public InventoryDropValidator DropValidator;
            public Action<InventoryDragPayload> OnDrop;
            public string Tooltip = string.Empty;
        }

        // The drawn equipped ammunition slot, whose count follows every shot.
        private sealed class LiveAmmunitionSlot
        {
            public InventoryItem Item;
            public string Description = string.Empty;
            public int DrawnQuantity;
            public Text QuantityText;
            public InventoryTooltipTrigger Tooltip;
        }
    }
}
