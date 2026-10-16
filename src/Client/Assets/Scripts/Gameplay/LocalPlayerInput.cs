using UnityEngine;
using UnityEngine.InputSystem;

namespace Mmorpg.Client.Gameplay
{
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerInput))]
    public sealed class LocalPlayerInput : MonoBehaviour
    {
        private const string SelectActionName = "Select";
        private const string PointerPositionActionName = "PointerPosition";
        private const string CameraOrbitActionName = "CameraOrbit";
        private const string CameraOrbitDeltaActionName = "CameraOrbitDelta";
        private const string CameraRotateActionName = "CameraRotate";
        private const string CameraZoomActionName = "CameraZoom";
        private const string CancelActionName = "Cancel";
        private const string ToggleWorldDebugActionName = "ToggleWorldDebug";
        private const string ToggleInventoryActionName = "ToggleInventory";
        private const string ToggleCharacterInventoryActionName = "ToggleCharacterInventory";
        private const string ToggleEquipmentInventoryActionName = "ToggleEquipmentInventory";

        private PlayerInput playerInput;
        private InputAction selectAction;
        private InputAction pointerPositionAction;
        private InputAction cameraOrbitAction;
        private InputAction cameraOrbitDeltaAction;
        private InputAction cameraRotateAction;
        private InputAction cameraZoomAction;
        private InputAction cancelAction;
        private InputAction toggleWorldDebugAction;
        private InputAction toggleInventoryAction;
        private InputAction toggleCharacterInventoryAction;
        private InputAction toggleEquipmentInventoryAction;

        public bool IsConfigured { get; private set; }

        // Left click: walk to the ground under the pointer or act on the target there.
        public bool SelectPressedThisFrame
        {
            get { return IsConfigured && selectAction.WasPressedThisFrame(); }
        }

        public Vector2 PointerPosition
        {
            get { return IsConfigured ? pointerPositionAction.ReadValue<Vector2>() : Vector2.zero; }
        }

        public bool CameraOrbitHeld
        {
            get { return IsConfigured && cameraOrbitAction.IsPressed(); }
        }

        public Vector2 CameraOrbitDelta
        {
            get { return IsConfigured ? cameraOrbitDeltaAction.ReadValue<Vector2>() : Vector2.zero; }
        }

        // Arrow keys: x turns the camera around the player and y tilts it.
        public Vector2 CameraRotate
        {
            get { return IsConfigured ? cameraRotateAction.ReadValue<Vector2>() : Vector2.zero; }
        }

        public float CameraZoom
        {
            get { return IsConfigured ? cameraZoomAction.ReadValue<float>() : 0f; }
        }

        public bool CancelPressedThisFrame
        {
            get { return IsConfigured && cancelAction.WasPressedThisFrame(); }
        }

        public bool ToggleWorldDebugPressedThisFrame
        {
            get { return IsConfigured && toggleWorldDebugAction.WasPressedThisFrame(); }
        }

        public bool ToggleInventoryPressedThisFrame
        {
            get { return IsConfigured && toggleInventoryAction.WasPressedThisFrame(); }
        }

        public bool ToggleCharacterInventoryPressedThisFrame
        {
            get { return IsConfigured && toggleCharacterInventoryAction.WasPressedThisFrame(); }
        }

        public bool ToggleEquipmentInventoryPressedThisFrame
        {
            get { return IsConfigured && toggleEquipmentInventoryAction.WasPressedThisFrame(); }
        }

        private void Awake()
        {
            playerInput = GetComponent<PlayerInput>();
        }

        private void OnEnable()
        {
            Initialize();
        }

        private void Start()
        {
            if (Initialize())
            {
                return;
            }

            Debug.LogError(
                "LocalPlayerInput requires a PlayerInput actions asset containing "
                + "Select, PointerPosition, CameraOrbit, CameraOrbitDelta, CameraRotate, "
                + "CameraZoom, Cancel, ToggleWorldDebug, ToggleInventory, "
                + "ToggleCharacterInventory, and ToggleEquipmentInventory actions.",
                this);
        }

        public bool Initialize()
        {
            if (IsConfigured)
            {
                return true;
            }

            if (playerInput == null)
            {
                playerInput = GetComponent<PlayerInput>();
            }

            IsConfigured = TryBindActions();
            return IsConfigured;
        }

        private bool TryBindActions()
        {
            if (playerInput == null || playerInput.actions == null)
            {
                return false;
            }

            var actions = playerInput.actions;
            selectAction = actions.FindAction(SelectActionName, false);
            pointerPositionAction = actions.FindAction(PointerPositionActionName, false);
            cameraOrbitAction = actions.FindAction(CameraOrbitActionName, false);
            cameraOrbitDeltaAction = actions.FindAction(CameraOrbitDeltaActionName, false);
            cameraRotateAction = actions.FindAction(CameraRotateActionName, false);
            cameraZoomAction = actions.FindAction(CameraZoomActionName, false);
            cancelAction = actions.FindAction(CancelActionName, false);
            toggleWorldDebugAction = actions.FindAction(ToggleWorldDebugActionName, false);
            toggleInventoryAction = actions.FindAction(ToggleInventoryActionName, false);
            toggleCharacterInventoryAction = actions.FindAction(
                ToggleCharacterInventoryActionName,
                false);
            toggleEquipmentInventoryAction = actions.FindAction(
                ToggleEquipmentInventoryActionName,
                false);

            return selectAction != null
                && pointerPositionAction != null
                && cameraOrbitAction != null
                && cameraOrbitDeltaAction != null
                && cameraRotateAction != null
                && cameraZoomAction != null
                && cancelAction != null
                && toggleWorldDebugAction != null
                && toggleInventoryAction != null
                && toggleCharacterInventoryAction != null
                && toggleEquipmentInventoryAction != null;
        }
    }
}
