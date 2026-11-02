using System;
using Mmorpg.Client.Items;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
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
            Guid corpseId,
            int? quantity)
        {
            Kind = kind;
            ItemInstanceId = itemInstanceId;
            RecoveryDeliveryId = recoveryDeliveryId;
            CorpseId = corpseId;
            Quantity = quantity;
        }

        public InventoryDragPayloadKind Kind { get; }

        public Guid ItemInstanceId { get; }

        public Guid RecoveryDeliveryId { get; }

        public Guid CorpseId { get; }

        // The part of the stack that moves, such as half with Shift or the amount chosen in
        // the split box; null moves the whole stack.
        public int? Quantity { get; }

        public bool IsPartial
        {
            get { return Quantity != null; }
        }

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
                Guid.Empty,
                null);
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
                Guid.Empty,
                null);
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
                corpseId,
                null);
        }

        // The same item moving as part of its stack. A Recovery delivery is always whole.
        public InventoryDragPayload WithQuantity(int quantity)
        {
            if (quantity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(quantity), "A part of a stack holds at least one item.");
            }

            return Kind == InventoryDragPayloadKind.RecoveryDelivery
                ? this
                : new InventoryDragPayload(Kind, ItemInstanceId, RecoveryDeliveryId, CorpseId, quantity);
        }
    }

    public delegate bool InventoryDropValidator(
        InventoryDragPayload payload,
        out string reason);

    // The item the player moves between slots: dragged with the left button held, or a part of
    // a stack carried on the pointer after the split box, which a click on a slot puts down and
    // a click beside the windows, a right-click, or Escape puts back.
    [DisallowMultipleComponent]
    public sealed class InventoryDragCoordinator : MonoBehaviour
    {
        public const string GhostName = "InventoryDragGhost";

        // The ghost trails below and right of the pointer so the hovered slot's green or
        // red highlight stays visible.
        private static readonly Vector2 GhostPointerOffset = new Vector2(16f, -16f);

        private RectTransform visualRoot;
        private UiTheme theme;
        private RectTransform dragGhost;
        private int carryEndedFrame = -1;

        // A drag started, held or carried, with its payload.
        public event Action<InventoryDragPayload> DragStarted;

        // The drag ended: dropped, put back, or cancelled.
        public event Action DragEnded;

        // A drag ended over no window and no slot, such as over the world, with its payload.
        public event Action<InventoryDragPayload> DroppedOutside;

        public bool IsDragging { get; private set; }

        // The player carries a part of a stack on the pointer without holding a button.
        public bool IsCarrying { get; private set; }

        public InventoryDragPayload ActivePayload { get; private set; }

        // World input leaves the pointer alone while a part is carried and in the frame its
        // carry ended, so the click that put it down or back never also walks or targets.
        public bool CapturesPointer
        {
            get { return IsCarrying || Time.frameCount <= carryEndedFrame; }
        }

        public void Configure(RectTransform root, UiTheme dragTheme)
        {
            visualRoot = root;
            theme = dragTheme;
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

            Begin(payload, label, icon, quantity);
            Move(eventData);
            return true;
        }

        // Puts a part of a stack on the pointer, where it stays until a click puts it on a
        // slot or puts it back.
        public bool BeginCarry(
            InventoryDragPayload payload,
            string label,
            Sprite icon,
            string quantity,
            Vector2 screenPosition)
        {
            if (payload == null || IsDragging)
            {
                return false;
            }

            IsCarrying = true;
            Begin(payload, label, icon, quantity);
            MoveTo(screenPosition, null);
            return true;
        }

        public void Move(PointerEventData eventData)
        {
            if (eventData == null)
            {
                return;
            }

            var canvas = visualRoot != null ? visualRoot.GetComponentInParent<Canvas>() : null;
            MoveTo(
                eventData.position,
                canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                    ? canvas.worldCamera
                    : eventData.pressEventCamera);
        }

        // Ends a drag that no slot took. Released over nothing of the interface, the payload
        // dropped outside every window.
        public void EndWithoutTarget(PointerEventData eventData)
        {
            if (!IsDragging)
            {
                return;
            }

            var payload = ActivePayload;
            var overInterface = eventData != null && eventData.pointerCurrentRaycast.gameObject != null;
            Cancel();
            if (!overInterface && payload != null)
            {
                DroppedOutside?.Invoke(payload);
            }
        }

        public void Cancel()
        {
            var wasDragging = IsDragging;
            if (IsCarrying)
            {
                carryEndedFrame = Time.frameCount;
            }

            IsDragging = false;
            IsCarrying = false;
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

            if (wasDragging)
            {
                DragEnded?.Invoke();
            }
        }

        // A carried part follows the pointer; a click beside every window puts it back.
        private void Update()
        {
            var mouse = Mouse.current;
            if (!IsCarrying || mouse == null)
            {
                return;
            }

            MoveTo(mouse.position.ReadValue(), null);
            if ((mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)
                && (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject()))
            {
                Cancel();
            }
        }

        private void OnDisable()
        {
            Cancel();
        }

        private void Begin(InventoryDragPayload payload, string label, Sprite icon, string quantity)
        {
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

            DragStarted?.Invoke(payload);
        }

        private void MoveTo(Vector2 screenPosition, Camera eventCamera)
        {
            if (!IsDragging || dragGhost == null || visualRoot == null)
            {
                return;
            }

            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    visualRoot,
                    screenPosition,
                    eventCamera,
                    out var localPoint))
            {
                dragGhost.anchoredPosition = localPoint + GhostPointerOffset;
            }
        }

        private void CreateIconGhost(Sprite icon, string quantity)
        {
            var size = theme != null ? theme.SlotSize : 54f;
            var image = CreateGhost(new Vector2(size, size));
            if (image == null)
            {
                return;
            }

            image.sprite = icon;
            image.preserveAspect = true;
            if (!string.IsNullOrEmpty(quantity))
            {
                var text = CreateGhostText(
                    "Quantity",
                    quantity,
                    TextAlignmentOptions.BottomRight,
                    theme != null ? theme.QuantityColor : Color.white);
                text.rectTransform.offsetMin = new Vector2(4f, 2f);
                text.rectTransform.offsetMax = new Vector2(-5f, -2f);
            }
        }

        private void CreateLabelGhost(string label)
        {
            var image = CreateGhost(new Vector2(200f, 40f));
            if (image == null)
            {
                return;
            }

            if (theme != null && theme.TooltipFrame != null)
            {
                UiFactory.SetSprite(image, theme.TooltipFrame, theme.WindowTint);
            }
            else
            {
                image.color = new Color(0.05f, 0.08f, 0.09f, 0.98f);
            }

            var text = CreateGhostText(
                "Label",
                label,
                TextAlignmentOptions.MidlineLeft,
                theme != null ? theme.TextColor : Color.white);
            text.rectTransform.offsetMin = new Vector2(10f, 4f);
            text.rectTransform.offsetMax = new Vector2(-10f, -4f);
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
            var text = UiFactory.Text(
                dragGhost,
                objectName,
                theme != null ? theme.BodyFont : TMP_Settings.defaultFontAsset,
                theme != null ? theme.QuantitySize : 15f,
                color,
                alignment,
                value);
            text.fontStyle = FontStyles.Bold;
            text.outlineWidth = 0.2f;
            text.outlineColor = new Color32(0, 0, 0, 220);
            UiFactory.Fill(text.rectTransform);
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
        private int halfQuantity;
        private bool ownsActiveDrag;

        public bool IsDragging => ownsActiveDrag;

        // Tests and tools hold Shift through this instead of the keyboard.
        public bool? PartialOverride { get; set; }

        // The half is the part Shift drags; zero for a stack that cannot be split.
        public void Configure(
            InventoryDragCoordinator dragCoordinator,
            InventoryDragPayload dragPayload,
            string dragLabel,
            Sprite dragIcon,
            string dragQuantity,
            bool enabled,
            int half = 0)
        {
            coordinator = dragCoordinator;
            payload = dragPayload;
            label = dragLabel ?? string.Empty;
            icon = dragIcon;
            quantity = dragQuantity ?? string.Empty;
            canDrag = enabled && dragPayload != null;
            halfQuantity = half;
            // The Editor's GetComponent returns a fake null object instead of null, which ??
            // treats as a component.
            if (!TryGetComponent(out canvasGroup))
            {
                canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }
        }

        // Only the left button drags; a right press is a right-click move. Shift drags half of
        // a stack, rounded down, and the rest stays.
        public void OnBeginDrag(PointerEventData eventData)
        {
            var half = halfQuantity > 0 && (PartialOverride ?? InventoryModifierKeys.IsShiftHeld());
            ownsActiveDrag = canDrag
                && eventData != null
                && eventData.button == PointerEventData.InputButton.Left
                && coordinator != null
                && payload != null
                && coordinator.TryBegin(
                    half ? payload.WithQuantity(halfQuantity) : payload,
                    label,
                    icon,
                    half ? InventorySlotPresentation.QuantityText(halfQuantity) : quantity,
                    eventData);
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
            var owned = ownsActiveDrag;
            RestoreSlot();
            ownsActiveDrag = false;
            // A slot that took the drop already ended it.
            if (owned && coordinator != null && coordinator.IsDragging)
            {
                coordinator.EndWithoutTarget(eventData);
            }
        }

        private void OnDisable()
        {
            RestoreSlot();
            if (ownsActiveDrag && coordinator != null && coordinator.IsDragging)
            {
                coordinator.Cancel();
            }

            ownsActiveDrag = false;
        }

        private void RestoreSlot()
        {
            if (canvasGroup != null)
            {
                canvasGroup.blocksRaycasts = true;
                canvasGroup.alpha = 1f;
            }
        }
    }

    public static class InventoryModifierKeys
    {
        public static bool IsShiftHeld()
        {
            var keyboard = Keyboard.current;
            return keyboard != null && keyboard.shiftKey.isPressed;
        }

        public static bool IsControlHeld()
        {
            var keyboard = Keyboard.current;
            return keyboard != null && keyboard.ctrlKey.isPressed;
        }
    }

    // The clicks of a slot: right-click moves the item, Shift+right-click moves half of its
    // stack, and Ctrl+click opens the split box. While a part is carried on the pointer, a
    // click puts it on the slot and a right-click puts it back.
    [DisallowMultipleComponent]
    public sealed class InventorySlotClicks : MonoBehaviour, IPointerClickHandler
    {
        private InventoryDragCoordinator coordinator;
        private InventoryDropTarget dropTarget;
        private Action onRightClick;
        private Action onHalfRightClick;
        private Action onSplitClick;

        // Tests and tools hold Shift or Ctrl through these instead of the keyboard.
        public bool? ShiftOverride { get; set; }

        public bool? ControlOverride { get; set; }

        // Without its own half move, Shift+right-click does what right-click does, such as for
        // a single item.
        public void Configure(
            InventoryDragCoordinator dragCoordinator,
            InventoryDropTarget target,
            Action rightClick,
            Action halfRightClick,
            Action splitClick)
        {
            coordinator = dragCoordinator;
            dropTarget = target;
            onRightClick = rightClick;
            onHalfRightClick = halfRightClick;
            onSplitClick = splitClick;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData == null)
            {
                return;
            }

            if (coordinator != null && coordinator.IsCarrying)
            {
                if (eventData.button == PointerEventData.InputButton.Left && dropTarget != null)
                {
                    dropTarget.DropCarried();
                }
                else if (eventData.button == PointerEventData.InputButton.Right)
                {
                    coordinator.Cancel();
                }

                return;
            }

            if (eventData.button == PointerEventData.InputButton.Right)
            {
                var half = onHalfRightClick != null && (ShiftOverride ?? InventoryModifierKeys.IsShiftHeld());
                (half ? onHalfRightClick : onRightClick)?.Invoke();
            }
            else if (eventData.button == PointerEventData.InputButton.Left
                && (ControlOverride ?? InventoryModifierKeys.IsControlHeld()))
            {
                onSplitClick?.Invoke();
            }
        }
    }

    // A slot that takes drops. While an item is dragged or carried over it, its highlight shows
    // whether the drop is valid.
    [DisallowMultipleComponent]
    public sealed class InventoryDropTarget : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IDropHandler
    {
        private InventoryDragCoordinator coordinator;
        private Image highlight;
        private Color validColor;
        private Color invalidColor;
        private InventoryDropValidator validator;
        private Action<InventoryDragPayload> onDrop;
        private Action<string> onRejected;

        // The shown feedback: null without a drag over the slot.
        public bool? ShowsValidDrop { get; private set; }

        public void Configure(
            InventoryDragCoordinator dragCoordinator,
            Image highlightImage,
            Color valid,
            Color invalid,
            InventoryDropValidator dropValidator,
            Action<InventoryDragPayload> accepted,
            Action<string> rejected)
        {
            coordinator = dragCoordinator;
            highlight = highlightImage;
            validColor = valid;
            invalidColor = invalid;
            validator = dropValidator;
            onDrop = accepted;
            onRejected = rejected;
            ClearHighlight();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (coordinator == null || !coordinator.IsDragging || validator == null)
            {
                return;
            }

            var valid = Validate(coordinator.ActivePayload, out _);
            ShowsValidDrop = valid;
            if (highlight != null)
            {
                highlight.color = valid ? validColor : invalidColor;
                highlight.enabled = true;
            }
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            ClearHighlight();
        }

        public void OnDrop(PointerEventData eventData)
        {
            if (coordinator != null && coordinator.IsDragging)
            {
                Take();
            }
        }

        // Puts the carried part on the slot. A slot that refuses it says why, and the part
        // stays on the pointer.
        public void DropCarried()
        {
            if (coordinator != null && coordinator.IsCarrying)
            {
                Take();
            }
        }

        private void Take()
        {
            if (validator == null)
            {
                return;
            }

            var payload = coordinator.ActivePayload;
            var accepted = Validate(payload, out var reason);
            if (accepted || !coordinator.IsCarrying)
            {
                coordinator.Cancel();
                ClearHighlight();
            }

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
            ClearHighlight();
        }

        private bool Validate(InventoryDragPayload payload, out string reason)
        {
            reason = "This inventory destination is not valid.";
            return payload != null
                && validator != null
                && validator(payload, out reason);
        }

        private void ClearHighlight()
        {
            ShowsValidDrop = null;
            if (highlight != null)
            {
                highlight.enabled = false;
            }
        }
    }
}
