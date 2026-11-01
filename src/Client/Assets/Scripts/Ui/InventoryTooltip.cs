using UnityEngine;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    public enum InventorySlotVisualState
    {
        Empty,
        Occupied,
        Selected,
        Blocked
    }

    // Names a drawn slot and its look, so tests and tools can inspect the layout.
    [DisallowMultipleComponent]
    public sealed class InventorySlotView : MonoBehaviour
    {
        public string SlotKey { get; private set; } = string.Empty;

        public InventorySlotVisualState VisualState { get; private set; }

        public string ItemDefinitionId { get; private set; } = string.Empty;

        public void Configure(
            string slotKey,
            InventorySlotVisualState visualState,
            string itemDefinitionId)
        {
            SlotKey = slotKey ?? string.Empty;
            VisualState = visualState;
            ItemDefinitionId = itemDefinitionId ?? string.Empty;
        }
    }

    // One shared hover tooltip per canvas. It never shows during a drag, where the drop
    // highlight is the feedback.
    [DisallowMultipleComponent]
    public sealed class InventoryTooltipView : MonoBehaviour
    {
        private const float MaximumWidth = 320f;
        private static readonly Vector2 PointerOffset = new Vector2(18f, -18f);

        private RectTransform rect;
        private RectTransform bounds;
        private TextMeshProUGUI label;
        private LayoutElement labelLayout;
        private InventoryDragCoordinator dragCoordinator;
        private object owner;

        public bool IsVisible
        {
            get { return owner != null && gameObject.activeSelf; }
        }

        public string Text
        {
            get { return label != null ? label.text : string.Empty; }
        }

        public static InventoryTooltipView Create(
            RectTransform parent,
            InventoryDragCoordinator coordinator)
        {
            var tooltipObject = TemporaryUiKit.CreateUiObject("InventoryTooltip", parent);
            var tooltipRect = tooltipObject.GetComponent<RectTransform>();
            tooltipRect.anchorMin = new Vector2(0.5f, 0.5f);
            tooltipRect.anchorMax = new Vector2(0.5f, 0.5f);
            tooltipRect.pivot = new Vector2(0f, 1f);
            var background = tooltipObject.AddComponent<Image>();
            background.color = new Color(0.02f, 0.025f, 0.035f, 0.97f);
            background.raycastTarget = false;
            TemporaryUiKit.AddOutline(background, TemporaryUiKit.PanelBorderColor);
            var group = tooltipObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;
            var layout = tooltipObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 8, 8);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            var fitter = tooltipObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var text = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Text", tooltipObject.transform),
                string.Empty,
                14,
                FontStyles.Normal,
                TextAlignmentOptions.TopLeft,
                TemporaryUiKit.TextPrimary);
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;

            var view = tooltipObject.AddComponent<InventoryTooltipView>();
            view.rect = tooltipRect;
            view.bounds = parent;
            view.label = text;
            view.labelLayout = text.gameObject.AddComponent<LayoutElement>();
            view.dragCoordinator = coordinator;
            tooltipObject.SetActive(false);
            return view;
        }

        public void Show(object requester, string value, Vector2 screenPosition)
        {
            if (requester == null
                || string.IsNullOrWhiteSpace(value)
                || (dragCoordinator != null && dragCoordinator.IsDragging))
            {
                return;
            }

            owner = requester;
            label.text = value;
            labelLayout.preferredWidth = Mathf.Min(MaximumWidth, label.preferredWidth + 2f);
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            Move(requester, screenPosition);
        }

        public void Move(object requester, Vector2 screenPosition)
        {
            if (requester == null || requester != owner || bounds == null)
            {
                return;
            }

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    bounds,
                    screenPosition,
                    null,
                    out var pointer))
            {
                return;
            }

            // Keep the tooltip inside its bounds by flipping it to the other pointer side.
            var size = rect.rect.size;
            var area = bounds.rect;
            var position = pointer + PointerOffset;
            if (position.x + size.x > area.xMax)
            {
                position.x = pointer.x - PointerOffset.x - size.x;
            }

            if (position.y - size.y < area.yMin)
            {
                position.y = pointer.y - PointerOffset.y + size.y;
            }

            position.x = Mathf.Clamp(position.x, area.xMin, Mathf.Max(area.xMin, area.xMax - size.x));
            position.y = Mathf.Clamp(position.y, Mathf.Min(area.yMax, area.yMin + size.y), area.yMax);
            rect.anchoredPosition = position;
        }

        // A null requester hides the tooltip whoever shows it.
        public void Hide(object requester)
        {
            if (requester != null && requester != owner)
            {
                return;
            }

            owner = null;
            if (gameObject.activeSelf)
            {
                gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            if (dragCoordinator != null && dragCoordinator.IsDragging)
            {
                Hide(null);
            }
        }
    }

    [DisallowMultipleComponent]
    public sealed class InventoryTooltipTrigger : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerMoveHandler
    {
        private InventoryTooltipView view;
        private string text = string.Empty;

        public void Configure(InventoryTooltipView tooltipView, string tooltipText)
        {
            view = tooltipView;
            text = tooltipText ?? string.Empty;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (view != null)
            {
                view.Show(this, text, eventData.position);
            }
        }

        public void OnPointerMove(PointerEventData eventData)
        {
            if (view != null)
            {
                view.Move(this, eventData.position);
            }
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (view != null)
            {
                view.Hide(this);
            }
        }

        private void OnDisable()
        {
            if (view != null)
            {
                view.Hide(this);
            }
        }
    }
}
