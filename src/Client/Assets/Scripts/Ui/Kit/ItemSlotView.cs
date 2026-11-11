using System;
using System.Collections.Generic;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // What one item slot shows and does. A slot without a drag payload is not dragged, and a
    // slot without a validator takes no drops.
    public sealed class ItemSlotSpec
    {
        public string Key = string.Empty;
        public InventoryItem Item;
        public string DisplayName = string.Empty;
        public Sprite Icon;

        // Replaces the item's quantity, such as the live arrow count; null shows the item's.
        public string QuantityText;

        // The faint picture of the empty slot, such as a helmet for the head.
        public Sprite Silhouette;
        public bool Blocked;
        public bool Dimmed;
        public bool RolledBack;
        public Color FrameColor = Color.white;
        public string Badge = string.Empty;
        public Action OnRightClick;

        // Shift+right-click moves half the stack where right-click moves it; without it,
        // Shift+right-click does what right-click does.
        public Action OnHalfRightClick;

        // Ctrl+click asks how many items to take and carries them on the pointer.
        public Action OnSplitClick;
        public InventoryDragPayload DragPayload;
        public bool CanDrag;

        // The part of the stack that Shift drags: half, rounded down; zero for a stack that
        // cannot be split.
        public int HalfQuantity;
        public InventoryDropValidator DropValidator;
        public Action<InventoryDragPayload> OnDrop;
        public Action<ClientText> OnRejected;
        public string Tooltip = string.Empty;
    }

    // One square item slot: its background, the silhouette of an empty slot, the item's icon
    // in a frame of its rarity color, its quantity and policy badges, a lock while blocked, a
    // glow while a dragged item may go there, and highlights for hover and drop feedback. A
    // slot is built once and bound again whenever what it shows changes, so redrawing never
    // rebuilds it.
    [DisallowMultipleComponent]
    public sealed class ItemSlotView : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public const string QuantityLabelName = "Quantity";
        public const string IconName = "Icon";
        public const string DropTargetGlowName = "DropTargetGlow";

        private static readonly Color FallbackSlotColor = new Color(0.07f, 0.10f, 0.11f, 1f);

        private UiTheme theme;
        private InventoryDragCoordinator coordinator;
        private InventoryTooltipView tooltip;
        private Image background;
        private CanvasGroup content;
        private Image silhouette;
        private Image icon;
        private Image frame;
        private TextMeshProUGUI nameText;
        private TextMeshProUGUI quantity;
        private TextMeshProUGUI badge;
        private Image lockIcon;
        private Image glow;
        private Image hover;
        private Image highlight;
        private InventorySlotView slotView;
        private InventoryDragSource dragSource;
        private InventoryDropTarget dropTarget;
        private InventoryTooltipTrigger tooltipTrigger;
        private InventorySlotClicks clicks;
        private bool hoverable;

        public ItemSlotSpec Spec { get; private set; }

        public TextMeshProUGUI QuantityLabel
        {
            get { return quantity; }
        }

        public bool ShowsDropTargetGlow
        {
            get { return glow.gameObject.activeSelf; }
        }

        public static ItemSlotView Create(
            UiTheme theme,
            Transform parent,
            InventoryDragCoordinator coordinator,
            InventoryTooltipView tooltip)
        {
            var root = UiFactory.Create("Slot", parent);
            root.sizeDelta = new Vector2(theme.SlotSize, theme.SlotSize);
            var view = root.gameObject.AddComponent<ItemSlotView>();
            view.Build(theme, coordinator, tooltip);
            return view;
        }

        public void Bind(ItemSlotSpec spec)
        {
            Spec = spec ?? new ItemSlotSpec();
            var item = Spec.Item;
            name = "Slot " + Spec.Key;
            var state = item != null
                ? InventorySlotVisualState.Occupied
                : Spec.Blocked ? InventorySlotVisualState.Blocked : InventorySlotVisualState.Empty;
            slotView.Configure(Spec.Key, state, item?.DefinitionId, Spec.Dimmed);

            silhouette.gameObject.SetActive(item == null && Spec.Silhouette != null && !Spec.Blocked);
            if (silhouette.gameObject.activeSelf)
            {
                UiFactory.SetSprite(silhouette, Spec.Silhouette, theme.SilhouetteColor);
            }

            var showsIcon = item != null && Spec.Icon != null;
            icon.gameObject.SetActive(showsIcon);
            if (showsIcon)
            {
                UiFactory.SetSprite(icon, Spec.Icon, Color.white);
            }

            nameText.gameObject.SetActive(item != null && Spec.Icon == null);
            nameText.text = item != null ? Spec.DisplayName : string.Empty;

            frame.gameObject.SetActive(item != null);
            if (item != null)
            {
                UiFactory.SetSprite(frame, theme.ItemFrame, Spec.RolledBack ? theme.RollbackColor : Spec.FrameColor);
                if (theme.ItemFrame == null)
                {
                    frame.color = new Color(0f, 0f, 0f, 0f);
                }
            }

            var quantityText = item == null
                ? string.Empty
                : Spec.QuantityText ?? InventorySlotPresentation.QuantityText(item.Quantity);
            quantity.text = quantityText;
            quantity.gameObject.SetActive(item != null && (quantityText.Length > 0 || Spec.QuantityText != null));
            badge.text = item != null ? Spec.Badge ?? string.Empty : string.Empty;
            badge.gameObject.SetActive(badge.text.Length > 0);

            lockIcon.gameObject.SetActive(item == null && Spec.Blocked);
            background.color = item == null && Spec.Blocked
                ? theme.BlockedColor
                : theme.SlotBackground != null ? theme.SlotTint : FallbackSlotColor;
            content.alpha = Spec.Dimmed ? theme.SearchDimAlpha : 1f;

            dragSource.Configure(
                coordinator,
                Spec.DragPayload,
                Spec.DisplayName + (quantityText.Length > 0 ? "  x" + quantityText : string.Empty),
                Spec.Icon,
                quantityText,
                Spec.CanDrag && item != null,
                item != null ? Spec.HalfQuantity : 0);
            dropTarget.Configure(
                coordinator,
                highlight,
                theme.DropValidColor,
                theme.DropInvalidColor,
                Spec.DropValidator,
                Spec.OnDrop,
                Spec.OnRejected);
            tooltipTrigger.Configure(tooltip, Spec.Tooltip);
            clicks.Configure(coordinator, dropTarget, Spec.OnRightClick, Spec.OnHalfRightClick, Spec.OnSplitClick);
            SetDropTargetGlow(false);
            hoverable = item != null || !string.IsNullOrWhiteSpace(Spec.Tooltip);
            if (!hoverable)
            {
                hover.enabled = false;
            }
        }

        // Glows while a dragged item may be dropped here, such as an equipment slot that
        // takes the dragged ring.
        public void SetDropTargetGlow(bool shown)
        {
            if (glow.gameObject.activeSelf != shown)
            {
                glow.gameObject.SetActive(shown);
            }
        }

        // Shows a new quantity in place, such as the live arrow count after a shot.
        public void SetQuantity(string value)
        {
            quantity.text = value ?? string.Empty;
            quantity.gameObject.SetActive(Spec?.Item != null);
        }

        public void SetTooltip(string value)
        {
            if (Spec != null)
            {
                Spec.Tooltip = value ?? string.Empty;
            }

            tooltipTrigger.Configure(tooltip, value);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            hover.enabled = hoverable && (coordinator == null || !coordinator.IsDragging);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            hover.enabled = false;
        }

        private void OnDisable()
        {
            if (hover != null)
            {
                hover.enabled = false;
            }
        }

        private void Build(UiTheme slotTheme, InventoryDragCoordinator dragCoordinator, InventoryTooltipView tooltipView)
        {
            theme = slotTheme;
            coordinator = dragCoordinator;
            tooltip = tooltipView;
            background = gameObject.AddComponent<Image>();
            if (theme.SlotBackground != null)
            {
                UiFactory.SetSprite(background, theme.SlotBackground, theme.SlotTint);
            }
            else
            {
                background.color = FallbackSlotColor;
            }

            background.raycastTarget = true;

            var contentRect = UiFactory.Create("Content", transform);
            UiFactory.Fill(contentRect);
            content = contentRect.gameObject.AddComponent<CanvasGroup>();
            content.blocksRaycasts = false;
            content.interactable = false;

            silhouette = UiFactory.Image(contentRect, "Silhouette", null, theme.SilhouetteColor);
            UiFactory.Fill(silhouette.rectTransform, theme.SlotSize * 0.2f);
            icon = UiFactory.Image(contentRect, IconName, null, Color.white);
            UiFactory.Fill(icon.rectTransform, theme.IconInset);
            frame = UiFactory.Image(contentRect, "Frame", theme.ItemFrame, Color.white);
            UiFactory.Fill(frame.rectTransform);
            nameText = UiFactory.BodyText(theme, contentRect, "Name", theme.SmallSize, theme.TextColor, TextAlignmentOptions.Center);
            nameText.textWrappingMode = TextWrappingModes.Normal;
            nameText.overflowMode = TextOverflowModes.Truncate;
            nameText.enableAutoSizing = true;
            nameText.fontSizeMin = 9f;
            nameText.fontSizeMax = theme.SmallSize;
            UiFactory.Fill(nameText.rectTransform, 4f);
            quantity = UiFactory.BodyText(theme, contentRect, QuantityLabelName, theme.QuantitySize, theme.QuantityColor, TextAlignmentOptions.BottomRight);
            quantity.fontStyle = FontStyles.Bold;
            quantity.outlineWidth = 0.2f;
            quantity.outlineColor = new Color32(0, 0, 0, 220);
            UiFactory.Stretch(quantity.rectTransform, Vector2.zero, Vector2.one, new Vector2(3f, 1f), new Vector2(-5f, -2f));
            badge = UiFactory.BodyText(theme, contentRect, "PolicyBadge", theme.SmallSize, theme.AccentColor, TextAlignmentOptions.TopRight);
            badge.fontStyle = FontStyles.Bold;
            badge.outlineWidth = 0.2f;
            badge.outlineColor = new Color32(0, 0, 0, 220);
            UiFactory.Stretch(badge.rectTransform, Vector2.zero, Vector2.one, new Vector2(3f, 3f), new Vector2(-5f, -3f));
            lockIcon = UiFactory.Image(contentRect, "Lock", theme.LockIcon, theme.TextColor);
            UiFactory.Fill(lockIcon.rectTransform, theme.SlotSize * 0.28f);

            glow = UiFactory.Image(
                transform,
                DropTargetGlowName,
                theme.SlotGlow != null ? theme.SlotGlow : theme.SlotHighlight,
                theme.DropTargetGlowColor);
            UiFactory.Fill(glow.rectTransform);
            glow.gameObject.AddComponent<UiPulse>().Configure(
                glow,
                theme.DropTargetGlowColor,
                theme.DropTargetPulseSeconds,
                theme.DropTargetGlowFloor);
            glow.gameObject.SetActive(false);

            hover = UiFactory.Image(transform, "Hover", theme.SlotHighlight, theme.HoverColor);
            UiFactory.Fill(hover.rectTransform);
            hover.enabled = false;
            highlight = UiFactory.Image(transform, "DropHighlight", theme.SlotHighlight, theme.DropValidColor);
            UiFactory.Fill(highlight.rectTransform);
            highlight.enabled = false;

            slotView = gameObject.AddComponent<InventorySlotView>();
            dragSource = gameObject.AddComponent<InventoryDragSource>();
            dropTarget = gameObject.AddComponent<InventoryDropTarget>();
            tooltipTrigger = gameObject.AddComponent<InventoryTooltipTrigger>();
            clicks = gameObject.AddComponent<InventorySlotClicks>();
        }
    }

    // Breathes a graphic's alpha between a floor and its color's own alpha, so a highlight
    // glows. It runs only while its object is active.
    [DisallowMultipleComponent]
    public sealed class UiPulse : MonoBehaviour
    {
        private Graphic graphic;
        private Color color;
        private float period = 1f;
        private float floor;
        private float startTime;

        public void Configure(Graphic target, Color baseColor, float seconds, float minimum)
        {
            graphic = target;
            color = baseColor;
            period = Mathf.Max(0.05f, seconds);
            floor = Mathf.Clamp01(minimum);
        }

        private void OnEnable()
        {
            startTime = Time.unscaledTime;
            Apply();
        }

        private void Update()
        {
            Apply();
        }

        // Starts at the floor and rises, so the glow fades in.
        private void Apply()
        {
            if (graphic == null)
            {
                return;
            }

            var wave = 0.5f - 0.5f * Mathf.Cos((Time.unscaledTime - startTime) / period * 2f * Mathf.PI);
            graphic.color = new Color(color.r, color.g, color.b, color.a * Mathf.Lerp(floor, 1f, wave));
        }
    }

    // A grid of item slots in a fixed number of columns. Binding keeps the slots it has and
    // adds or hides slots only when the count changes.
    public sealed class ItemSlotGrid
    {
        private readonly UiTheme theme;
        private readonly InventoryDragCoordinator coordinator;
        private readonly InventoryTooltipView tooltip;
        private readonly List<ItemSlotView> slots = new List<ItemSlotView>();
        private readonly LayoutElement layout;
        private readonly int columns;

        public ItemSlotGrid(
            UiTheme theme,
            Transform parent,
            int columns,
            InventoryDragCoordinator coordinator,
            InventoryTooltipView tooltip,
            string name = "SlotGrid")
        {
            this.theme = theme;
            this.coordinator = coordinator;
            this.tooltip = tooltip;
            this.columns = Mathf.Max(1, columns);
            Rect = UiFactory.Create(name, parent);
            var grid = Rect.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(theme.SlotSize, theme.SlotSize);
            grid.spacing = new Vector2(theme.SlotSpacing, theme.SlotSpacing);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = this.columns;
            grid.childAlignment = TextAnchor.UpperLeft;
            layout = Rect.gameObject.AddComponent<LayoutElement>();
        }

        public RectTransform Rect { get; }

        public IReadOnlyList<ItemSlotView> Slots
        {
            get { return slots; }
        }

        public static float Width(UiTheme theme, int columns)
        {
            return columns * theme.SlotSize + (columns - 1) * theme.SlotSpacing;
        }

        public void Bind(IReadOnlyList<ItemSlotSpec> specs)
        {
            var count = specs != null ? specs.Count : 0;
            while (slots.Count < count)
            {
                slots.Add(ItemSlotView.Create(theme, Rect, coordinator, tooltip));
            }

            for (var index = 0; index < slots.Count; index++)
            {
                var shown = index < count;
                slots[index].gameObject.SetActive(shown);
                if (shown)
                {
                    slots[index].Bind(specs[index]);
                }
            }

            var rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)columns));
            layout.preferredHeight = count == 0 ? 0f : rows * theme.SlotSize + (rows - 1) * theme.SlotSpacing;
            layout.minHeight = layout.preferredHeight;
            layout.preferredWidth = Width(theme, columns);
        }

        public void SetActive(bool active)
        {
            Rect.gameObject.SetActive(active);
        }
    }
}
