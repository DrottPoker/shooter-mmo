using UnityEngine;
using UnityEngine.InputSystem;

namespace ShooterMmo.Gameplay
{
    [RequireComponent(typeof(CharacterController))]
    public sealed class LocalPlayerController : MonoBehaviour
    {
        [SerializeField] private float walkSpeed = 5f;
        [SerializeField] private float sprintSpeed = 8f;
        [SerializeField] private float rotationSpeed = 720f;
        [SerializeField] private float gravity = -24f;
        [SerializeField] private float jumpVelocity = 7f;
        [SerializeField] private Transform cameraTarget;

        private CharacterController characterController;
        private Transform cameraTransform;
        private InputAction moveAction;
        private InputAction sprintAction;
        private InputAction jumpAction;
        private float verticalVelocity;

        public Transform CameraTarget
        {
            get { return cameraTarget != null ? cameraTarget : transform; }
        }

        private void Awake()
        {
            characterController = GetComponent<CharacterController>();
            CreateInputActions();
        }

        private void OnEnable()
        {
            moveAction.Enable();
            sprintAction.Enable();
            jumpAction.Enable();
        }

        private void OnDisable()
        {
            moveAction.Disable();
            sprintAction.Disable();
            jumpAction.Disable();
        }

        private void OnDestroy()
        {
            moveAction.Dispose();
            sprintAction.Dispose();
            jumpAction.Dispose();
        }

        private void Update()
        {
            var moveInput = Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f);
            var planarMove = BuildCameraRelativeMove(moveInput);
            var speed = sprintAction.IsPressed() ? sprintSpeed : walkSpeed;

            ApplyGravityAndJump();

            var velocity = planarMove * speed;
            velocity.y = verticalVelocity;
            characterController.Move(velocity * Time.deltaTime);

            if (planarMove.sqrMagnitude > 0.001f)
            {
                var targetRotation = Quaternion.LookRotation(planarMove, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation,
                    targetRotation,
                    rotationSpeed * Time.deltaTime);
            }
        }

        public void SetCameraTarget(Transform target)
        {
            cameraTarget = target;
        }

        public void SetCameraTransform(Transform targetCameraTransform)
        {
            cameraTransform = targetCameraTransform;
        }

        private void CreateInputActions()
        {
            moveAction = new InputAction("Move", InputActionType.Value);
            moveAction.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Up", "<Keyboard>/upArrow")
                .With("Down", "<Keyboard>/s")
                .With("Down", "<Keyboard>/downArrow")
                .With("Left", "<Keyboard>/a")
                .With("Left", "<Keyboard>/leftArrow")
                .With("Right", "<Keyboard>/d")
                .With("Right", "<Keyboard>/rightArrow");
            moveAction.AddBinding("<Gamepad>/leftStick");

            sprintAction = new InputAction("Sprint", InputActionType.Button);
            sprintAction.AddBinding("<Keyboard>/leftShift");
            sprintAction.AddBinding("<Keyboard>/rightShift");
            sprintAction.AddBinding("<Gamepad>/leftStickPress");

            jumpAction = new InputAction("Jump", InputActionType.Button);
            jumpAction.AddBinding("<Keyboard>/space");
            jumpAction.AddBinding("<Gamepad>/buttonSouth");
        }

        private Vector3 BuildCameraRelativeMove(Vector2 moveInput)
        {
            if (moveInput.sqrMagnitude <= 0.001f)
            {
                return Vector3.zero;
            }

            var referenceTransform = cameraTransform != null ? cameraTransform : transform;
            var forward = referenceTransform.forward;
            var right = referenceTransform.right;

            forward.y = 0f;
            right.y = 0f;

            if (forward.sqrMagnitude <= 0.001f)
            {
                forward = transform.forward;
            }

            if (right.sqrMagnitude <= 0.001f)
            {
                right = transform.right;
            }

            forward.Normalize();
            right.Normalize();

            return Vector3.ClampMagnitude((right * moveInput.x) + (forward * moveInput.y), 1f);
        }

        private void ApplyGravityAndJump()
        {
            if (characterController.isGrounded && verticalVelocity < 0f)
            {
                verticalVelocity = -1f;
            }

            if (jumpAction.WasPressedThisFrame() && characterController.isGrounded)
            {
                verticalVelocity = jumpVelocity;
            }

            verticalVelocity += gravity * Time.deltaTime;
        }
    }
}
