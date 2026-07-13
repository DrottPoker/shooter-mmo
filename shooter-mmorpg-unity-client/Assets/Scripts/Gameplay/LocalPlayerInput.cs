using UnityEngine;
using UnityEngine.InputSystem;

namespace ShooterMmo.Gameplay
{
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerInput))]
    public sealed class LocalPlayerInput : MonoBehaviour
    {
        private const string MoveActionName = "Move";
        private const string SprintActionName = "Sprint";
        private const string JumpActionName = "Jump";
        private const string AimActionName = "Aim";
        private const string LookActionName = "Look";
        private const string ToggleDebugCursorActionName = "ToggleDebugCursor";
        private const string ToggleWorldDebugActionName = "ToggleWorldDebug";

        private PlayerInput playerInput;
        private InputAction moveAction;
        private InputAction sprintAction;
        private InputAction jumpAction;
        private InputAction aimAction;
        private InputAction lookAction;
        private InputAction toggleDebugCursorAction;
        private InputAction toggleWorldDebugAction;
        private bool pointerInputEnabled = true;

        public bool IsConfigured { get; private set; }

        public Vector2 Move
        {
            get { return IsConfigured ? moveAction.ReadValue<Vector2>() : Vector2.zero; }
        }

        public bool SprintHeld
        {
            get { return IsConfigured && sprintAction.IsPressed(); }
        }

        public bool JumpPressedThisFrame
        {
            get { return IsConfigured && jumpAction.WasPressedThisFrame(); }
        }

        public bool AimHeld
        {
            get { return IsConfigured && pointerInputEnabled && aimAction.IsPressed(); }
        }

        public Vector2 LookDelta
        {
            get
            {
                return IsConfigured && pointerInputEnabled
                    ? lookAction.ReadValue<Vector2>()
                    : Vector2.zero;
            }
        }

        public bool ToggleDebugCursorPressedThisFrame
        {
            get { return IsConfigured && toggleDebugCursorAction.WasPressedThisFrame(); }
        }

        public bool ToggleWorldDebugPressedThisFrame
        {
            get { return IsConfigured && toggleWorldDebugAction.WasPressedThisFrame(); }
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
                + "Move, Sprint, Jump, Aim, Look, ToggleDebugCursor, and ToggleWorldDebug actions.",
                this);
        }

        public void SetPointerInputEnabled(bool isEnabled)
        {
            pointerInputEnabled = isEnabled;
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

            moveAction = playerInput.actions.FindAction(MoveActionName, false);
            sprintAction = playerInput.actions.FindAction(SprintActionName, false);
            jumpAction = playerInput.actions.FindAction(JumpActionName, false);
            aimAction = playerInput.actions.FindAction(AimActionName, false);
            lookAction = playerInput.actions.FindAction(LookActionName, false);
            toggleDebugCursorAction = playerInput.actions.FindAction(ToggleDebugCursorActionName, false);
            toggleWorldDebugAction = playerInput.actions.FindAction(ToggleWorldDebugActionName, false);

            return moveAction != null
                && sprintAction != null
                && jumpAction != null
                && aimAction != null
                && lookAction != null
                && toggleDebugCursorAction != null
                && toggleWorldDebugAction != null;
        }
    }
}
