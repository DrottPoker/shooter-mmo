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
        Blocked
    }

    // Names a drawn slot and its look, so tests and tools can inspect the layout.
    [DisallowMultipleComponent]
    public sealed class InventorySlotView : MonoBehaviour
    {
        public string SlotKey { get; private set; } = string.Empty;

        public InventorySlotVisualState VisualState { get; private set; }

        public string ItemDefinitionId { get; private set; } = string.Empty;

        // Whether the search dims the slot.
        public bool Dimmed { get; private set; }

        public void Configure(
            string slotKey,
            InventorySlotVisualState visualState,
            string itemDefinitionId,
            bool dimmed = false)
        {
            SlotKey = slotKey ?? string.Empty;
            VisualState = visualState;
            ItemDefinitionId = itemDefinitionId ?? string.Empty;
            Dimmed = dimmed;
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
            UiTheme theme,
            RectTransform parent,
            InventoryDragCoordinator coordinator)
        {
            var tooltipRect = UiFactory.Create("InventoryTooltip", parent);
            tooltipRect.anchorMin = new Vector2(0.5f, 0.5f);
            tooltipRect.anchorMax = new Vector2(0.5f, 0.5f);
            tooltipRect.pivot = new Vector2(0f, 1f);
            var background = tooltipRect.gameObject.AddComponent<Image>();
            if (theme.TooltipFrame != null)
            {
                UiFactory.SetSprite(background, theme.TooltipFrame, theme.WindowTint);
            }
            else
            {
                background.color = new Color(0.03f, 0.05f, 0.06f, 0.97f);
            }

            background.raycastTarget = false;
            var group = tooltipRect.gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;
            var layout = tooltipRect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(14, 14, 10, 12);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            var fitter = tooltipRect.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var text = UiFactory.BodyText(
                theme,
                tooltipRect,
                "Text",
                theme.LabelSize,
                theme.TextColor,
                TextAlignmentOptions.TopLeft);
            text.textWrappingMode = TextWrappingModes.Normal;
            text.lineSpacing = 4f;

            var view = tooltipRect.gameObject.AddComponent<InventoryTooltipView>();
            view.rect = tooltipRect;
            view.bounds = parent;
            view.label = text;
            view.labelLayout = text.gameObject.AddComponent<LayoutElement>();
            view.dragCoordinator = coordinator;
            tooltipRect.gameObject.SetActive(false);
            return view;
        }

        public bool IsShownBy(object requester)
        {
            return requester != null && requester == owner && gameObject.activeSelf;
        }

        public void Show(object requester, string value, Vector2 screenPosition)
        {
            if (requester == null
                || string.IsNullOrWhiteSpace(value)
                || (dragCoordinator != null && dragCoordinator.IsDragging))
            {
                return;
            }

            // TextMesh Pro measures a text only while it is active, so the tooltip shows first.
            owner = requester;
            gameObject.SetActive(true);
            label.text = value;
            labelLayout.preferredWidth = Mathf.Min(MaximumWidth, label.preferredWidth + 2f);
            transform.SetAsLastSibling();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            Move(requester, screenPosition);
        }

        // Replaces the text the requester shows, such as a live arrow count, in place.
        public void Replace(object requester, string value)
        {
            if (!IsShownBy(requester) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            label.text = value;
            labelLayout.preferredWidth = Mathf.Min(MaximumWidth, label.preferredWidth + 2f);
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
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

        public string TooltipText
        {
            get { return text; }
        }

        // A new text replaces the shown one at once, so a hovered slot that is drawn again
        // keeps its tooltip current.
        public void Configure(InventoryTooltipView tooltipView, string tooltipText)
        {
            view = tooltipView;
            text = tooltipText ?? string.Empty;
            if (view != null && view.IsShownBy(this))
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    view.Hide(this);
                }
                else
                {
                    view.Replace(this, text);
                }
            }
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
