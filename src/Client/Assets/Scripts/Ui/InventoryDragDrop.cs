using System;
using Mmorpg.Client.Items;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    public enum InventoryDragPayloadKind
    {
        Item = 1,
        RecoveryDelivery = 2,
        CorpseItem = 3
    }

    public sealed class InventoryDragPayload
    {
        private InventoryDragPayload(
            InventoryDragPayloadKind kind,
            Guid itemInstanceId,
            Guid recoveryDeliveryId,
            Guid corpseId)
        {
            Kind = kind;
            ItemInstanceId = itemInstanceId;
            RecoveryDeliveryId = recoveryDeliveryId;
            CorpseId = corpseId;
        }

        public InventoryDragPayloadKind Kind { get; }

        public Guid ItemInstanceId { get; }

        public Guid RecoveryDeliveryId { get; }

        public Guid CorpseId { get; }

        public static InventoryDragPayload ForItem(Guid itemInstanceId)
        {
            if (itemInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "An item drag payload requires an item instance id.",
                    nameof(itemInstanceId));
            }

            return new InventoryDragPayload(
                InventoryDragPayloadKind.Item,
                itemInstanceId,
                Guid.Empty,
                Guid.Empty);
        }

        public static InventoryDragPayload ForRecoveryDelivery(Guid recoveryDeliveryId)
        {
            if (recoveryDeliveryId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A Recovery drag payload requires a delivery id.",
                    nameof(recoveryDeliveryId));
            }

            return new InventoryDragPayload(
                InventoryDragPayloadKind.RecoveryDelivery,
                Guid.Empty,
                recoveryDeliveryId,
                Guid.Empty);
        }

        public static InventoryDragPayload ForCorpseItem(
            Guid corpseId,
            Guid itemInstanceId)
        {
            if (corpseId == Guid.Empty || itemInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A corpse item drag payload requires corpse and item instance ids.");
            }

            return new InventoryDragPayload(
                InventoryDragPayloadKind.CorpseItem,
                itemInstanceId,
                Guid.Empty,
                corpseId);
        }
    }

    public delegate bool InventoryDropValidator(
        InventoryDragPayload payload,
        out string reason);

    [DisallowMultipleComponent]
    public sealed class InventoryDragCoordinator : MonoBehaviour
    {
        public const string GhostName = "InventoryDragGhost";

        // The ghost trails below and right of the pointer so the hovered slot's green or
        // red highlight stays visible.
        private static readonly Vector2 GhostPointerOffset = new Vector2(16f, -16f);
        private const float GhostIconSize = 64f;

        private RectTransform visualRoot;
        private TMP_FontAsset font;
        private RectTransform dragGhost;

        public bool IsDragging { get; private set; }

        public InventoryDragPayload ActivePayload { get; private set; }

        public void Configure(RectTransform root, TMP_FontAsset dragFont)
        {
            visualRoot = root;
            font = dragFont;
        }

        // The ghost is the item's icon with its quantity, or its label without an icon.
        public bool TryBegin(
            InventoryDragPayload payload,
            string label,
            Sprite icon,
            string quantity,
            PointerEventData eventData)
        {
            if (payload == null || eventData == null || IsDragging)
            {
                return false;
            }

            ActivePayload = payload;
            IsDragging = true;
            if (icon != null)
            {
                CreateIconGhost(icon, quantity);
            }
            else
            {
                CreateLabelGhost(label);
            }

            Move(eventData);
            return true;
        }

        public void Move(PointerEventData eventData)
        {
            if (!IsDragging || dragGhost == null || visualRoot == null || eventData == null)
            {
                return;
            }

            var canvas = visualRoot.GetComponentInParent<Canvas>();
            var eventCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : eventData.pressEventCamera;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    visualRoot,
                    eventData.position,
                    eventCamera,
                    out var localPoint))
            {
                dragGhost.anchoredPosition = localPoint + GhostPointerOffset;
            }
        }

        public void Cancel()
        {
            IsDragging = false;
            ActivePayload = null;
            if (dragGhost != null)
            {
                var ghostObject = dragGhost.gameObject;
                dragGhost = null;
                if (Application.isPlaying)
                {
                    Destroy(ghostObject);
                }
                else
                {
                    DestroyImmediate(ghostObject);
                }
            }
        }

        private void OnDisable()
        {
            Cancel();
        }

        private void CreateIconGhost(Sprite icon, string quantity)
        {
            var image = CreateGhost(new Vector2(GhostIconSize, GhostIconSize));
            if (image == null)
            {
                return;
            }

            image.sprite = icon;
            image.preserveAspect = true;
            if (!string.IsNullOrEmpty(quantity))
            {
                var text = CreateGhostText("Quantity", quantity, TextAlignmentOptions.BottomRight, TemporaryUiKit.QuantityColor);
                text.rectTransform.offsetMin = new Vector2(4f, 2f);
                text.rectTransform.offsetMax = new Vector2(-6f, -2f);
            }
        }

        private void CreateLabelGhost(string label)
        {
            var image = CreateGhost(new Vector2(200f, 40f));
            if (image == null)
            {
                return;
            }

            image.color = new Color(0.16f, 0.32f, 0.48f, 0.98f);
            var text = CreateGhostText("Label", label, TextAlignmentOptions.Left, Color.white);
            text.rectTransform.offsetMin = new Vector2(8f, 4f);
            text.rectTransform.offsetMax = new Vector2(-8f, -4f);
        }

        private Image CreateGhost(Vector2 size)
        {
            if (visualRoot == null)
            {
                return null;
            }

            var ghostObject = new GameObject(
                GhostName,
                typeof(RectTransform),
                typeof(CanvasGroup),
                typeof(Image));
            dragGhost = ghostObject.GetComponent<RectTransform>();
            dragGhost.SetParent(visualRoot, false);
            dragGhost.anchorMin = new Vector2(0.5f, 0.5f);
            dragGhost.anchorMax = new Vector2(0.5f, 0.5f);
            dragGhost.pivot = new Vector2(0f, 1f);
            dragGhost.sizeDelta = size;
            ghostObject.GetComponent<CanvasGroup>().blocksRaycasts = false;
            ghostObject.GetComponent<CanvasGroup>().alpha = 0.9f;
            ghostObject.transform.SetAsLastSibling();
            var image = ghostObject.GetComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        private TextMeshProUGUI CreateGhostText(
            string objectName,
            string value,
            TextAlignmentOptions alignment,
            Color color)
        {
            var textObject = new GameObject(
                objectName,
                typeof(RectTransform),
                typeof(TextMeshProUGUI));
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.SetParent(dragGhost, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            var text = textObject.GetComponent<TextMeshProUGUI>();
            text.font = font;
            text.text = value ?? string.Empty;
            text.fontSize = 14;
            text.fontStyle = FontStyles.Bold;
            text.alignment = alignment;
            text.color = color;
            text.raycastTarget = false;
            return text;
        }
    }

    [DisallowMultipleComponent]
    public sealed class InventoryDragSource : MonoBehaviour,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        // The slot fades while its item is picked up, so the ghost reads as the moved item.
        public const float DraggedAlpha = 0.4f;

        private InventoryDragCoordinator coordinator;
        private InventoryDragPayload payload;
        private CanvasGroup canvasGroup;
        private string label;
        private Sprite icon;
        private string quantity;
        private bool canDrag;
        private bool ownsActiveDrag;

        public bool IsDragging => ownsActiveDrag;

        public void Configure(
            InventoryDragCoordinator dragCoordinator,
            InventoryDragPayload dragPayload,
            string dragLabel,
            Sprite dragIcon,
            string dragQuantity,
            bool enabled)
        {
            coordinator = dragCoordinator;
            payload = dragPayload;
            label = dragLabel ?? string.Empty;
            icon = dragIcon;
            quantity = dragQuantity ?? string.Empty;
            canDrag = enabled;
            // The Editor's GetComponent returns a fake null object instead of null, which ??
            // treats as a component.
            if (!TryGetComponent(out canvasGroup))
            {
                canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }
        }

        // Only the left button drags; a right press is a right-click move.
        public void OnBeginDrag(PointerEventData eventData)
        {
            ownsActiveDrag = canDrag
                && eventData != null
                && eventData.button == PointerEventData.InputButton.Left
                && coordinator != null
                && payload != null
                && coordinator.TryBegin(payload, label, icon, quantity, eventData);
            if (ownsActiveDrag && canvasGroup != null)
            {
                canvasGroup.blocksRaycasts = false;
                canvasGroup.alpha = DraggedAlpha;
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (ownsActiveDrag)
            {
                coordinator.Move(eventData);
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            EndOwnedDrag();
        }

        private void OnDisable()
        {
            EndOwnedDrag();
        }

        private void EndOwnedDrag()
        {
            if (canvasGroup != null)
            {
                canvasGroup.blocksRaycasts = true;
                canvasGroup.alpha = 1f;
            }

            if (ownsActiveDrag && coordinator != null && coordinator.IsDragging)
            {
                coordinator.Cancel();
            }

            ownsActiveDrag = false;
        }
    }

    // Runs a slot's right-click move.
    [DisallowMultipleComponent]
    public sealed class InventorySlotRightClick : MonoBehaviour, IPointerClickHandler
    {
        private Action onRightClick;

        public void Configure(Action rightClick)
        {
            onRightClick = rightClick;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Right)
            {
                onRightClick?.Invoke();
            }
        }
    }

    [DisallowMultipleComponent]
    public sealed class InventoryDropTarget : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IDropHandler
    {
        private static readonly Color ValidColor = new Color(0.18f, 0.52f, 0.30f, 1f);
        private static readonly Color InvalidColor = new Color(0.55f, 0.18f, 0.18f, 1f);

        private InventoryDragCoordinator coordinator;
        private Image image;
        private Color baseColor;
        private InventoryDropValidator validator;
        private Action<InventoryDragPayload> onDrop;
        private Action<string> onRejected;

        public void Configure(
            InventoryDragCoordinator dragCoordinator,
            Image targetImage,
            Color normalColor,
            InventoryDropValidator dropValidator,
            Action<InventoryDragPayload> accepted,
            Action<string> rejected)
        {
            coordinator = dragCoordinator;
            image = targetImage;
            baseColor = normalColor;
            validator = dropValidator;
            onDrop = accepted;
            onRejected = rejected;
            RestoreColor();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (coordinator == null || !coordinator.IsDragging || image == null)
            {
                return;
            }

            image.color = Validate(coordinator.ActivePayload, out _)
                ? ValidColor
                : InvalidColor;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            RestoreColor();
        }

        public void OnDrop(PointerEventData eventData)
        {
            if (coordinator == null || !coordinator.IsDragging)
            {
                return;
            }

            var payload = coordinator.ActivePayload;
            var accepted = Validate(payload, out var reason);
            coordinator.Cancel();
            RestoreColor();
            if (accepted)
            {
                onDrop?.Invoke(payload);
            }
            else
            {
                onRejected?.Invoke(string.IsNullOrWhiteSpace(reason)
                    ? "This inventory destination is not valid."
                    : reason);
            }
        }

        private void OnDisable()
        {
            RestoreColor();
        }

        private bool Validate(InventoryDragPayload payload, out string reason)
        {
            reason = "This inventory destination is not valid.";
            return payload != null
                && validator != null
                && validator(payload, out reason);
        }

        private void RestoreColor()
        {
            if (image != null)
            {
                image.color = baseColor;
            }
        }
    }
}
