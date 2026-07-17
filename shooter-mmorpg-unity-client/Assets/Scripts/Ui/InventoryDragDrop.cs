using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ShooterMmo.Ui
{
    public enum InventoryDragPayloadKind
    {
        Item = 1,
        RecoveryDelivery = 2
    }

    public sealed class InventoryDragPayload
    {
        private InventoryDragPayload(
            InventoryDragPayloadKind kind,
            Guid itemInstanceId,
            Guid recoveryDeliveryId)
        {
            Kind = kind;
            ItemInstanceId = itemInstanceId;
            RecoveryDeliveryId = recoveryDeliveryId;
        }

        public InventoryDragPayloadKind Kind { get; }

        public Guid ItemInstanceId { get; }

        public Guid RecoveryDeliveryId { get; }

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
                recoveryDeliveryId);
        }
    }

    public delegate bool InventoryDropValidator(
        InventoryDragPayload payload,
        out string reason);

    [DisallowMultipleComponent]
    public sealed class InventoryDragCoordinator : MonoBehaviour
    {
        private RectTransform visualRoot;
        private Font font;
        private RectTransform dragGhost;

        public bool IsDragging { get; private set; }

        public InventoryDragPayload ActivePayload { get; private set; }

        public void Configure(RectTransform root, Font dragFont)
        {
            visualRoot = root;
            font = dragFont;
        }

        public bool TryBegin(
            InventoryDragPayload payload,
            string label,
            Sprite icon,
            PointerEventData eventData)
        {
            if (payload == null || eventData == null || IsDragging)
            {
                return false;
            }

            ActivePayload = payload;
            IsDragging = true;
            CreateGhost(label, icon);
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
                dragGhost.anchoredPosition = localPoint;
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

        private void CreateGhost(string label, Sprite icon)
        {
            if (visualRoot == null)
            {
                return;
            }

            var ghostObject = new GameObject(
                "InventoryDragGhost",
                typeof(RectTransform),
                typeof(CanvasGroup),
                typeof(Image));
            dragGhost = ghostObject.GetComponent<RectTransform>();
            dragGhost.SetParent(visualRoot, false);
            dragGhost.anchorMin = new Vector2(0.5f, 0.5f);
            dragGhost.anchorMax = new Vector2(0.5f, 0.5f);
            dragGhost.pivot = new Vector2(0.5f, 0.5f);
            dragGhost.sizeDelta = new Vector2(230f, 62f);
            ghostObject.GetComponent<CanvasGroup>().blocksRaycasts = false;
            ghostObject.GetComponent<CanvasGroup>().alpha = 0.92f;
            ghostObject.GetComponent<Image>().color = new Color(0.16f, 0.32f, 0.48f, 0.98f);
            ghostObject.transform.SetAsLastSibling();

            var textOffset = 8f;
            if (icon != null)
            {
                var iconObject = new GameObject(
                    "Icon",
                    typeof(RectTransform),
                    typeof(Image));
                var iconRect = iconObject.GetComponent<RectTransform>();
                iconRect.SetParent(dragGhost, false);
                iconRect.anchorMin = new Vector2(0f, 0.5f);
                iconRect.anchorMax = new Vector2(0f, 0.5f);
                iconRect.pivot = new Vector2(0f, 0.5f);
                iconRect.anchoredPosition = new Vector2(7f, 0f);
                iconRect.sizeDelta = new Vector2(48f, 48f);
                var iconImage = iconObject.GetComponent<Image>();
                iconImage.sprite = icon;
                iconImage.preserveAspect = true;
                iconImage.raycastTarget = false;
                textOffset = 60f;
            }

            var textObject = new GameObject(
                "Label",
                typeof(RectTransform),
                typeof(Text));
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.SetParent(dragGhost, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(textOffset, 4f);
            textRect.offsetMax = new Vector2(-8f, -4f);
            var text = textObject.GetComponent<Text>();
            text.font = font;
            text.text = label ?? string.Empty;
            text.fontSize = 13;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = Color.white;
            text.raycastTarget = false;
        }
    }

    [DisallowMultipleComponent]
    public sealed class InventoryDragSource : MonoBehaviour,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        private InventoryDragCoordinator coordinator;
        private InventoryDragPayload payload;
        private CanvasGroup canvasGroup;
        private string label;
        private Sprite icon;
        private bool canDrag;
        private bool ownsActiveDrag;

        public bool IsDragging => ownsActiveDrag;

        public void Configure(
            InventoryDragCoordinator dragCoordinator,
            InventoryDragPayload dragPayload,
            string dragLabel,
            Sprite dragIcon,
            bool enabled)
        {
            coordinator = dragCoordinator;
            payload = dragPayload;
            label = dragLabel ?? string.Empty;
            icon = dragIcon;
            canDrag = enabled;
            canvasGroup = GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            ownsActiveDrag = canDrag
                && coordinator != null
                && payload != null
                && coordinator.TryBegin(payload, label, icon, eventData);
            if (ownsActiveDrag && canvasGroup != null)
            {
                canvasGroup.blocksRaycasts = false;
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
            }

            if (ownsActiveDrag && coordinator != null && coordinator.IsDragging)
            {
                coordinator.Cancel();
            }

            ownsActiveDrag = false;
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
