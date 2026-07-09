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
        private float verticalVelocity;

        public Transform CameraTarget
        {
            get { return cameraTarget != null ? cameraTarget : transform; }
        }

        private void Awake()
        {
            characterController = GetComponent<CharacterController>();
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            var moveInput = ReadMoveInput(keyboard);
            var planarMove = BuildCameraRelativeMove(moveInput);
            var speed = IsSprinting(keyboard) ? sprintSpeed : walkSpeed;

            ApplyGravityAndJump(keyboard);

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

        private static Vector2 ReadMoveInput(Keyboard keyboard)
        {
            if (keyboard == null)
            {
                return Vector2.zero;
            }

            var horizontal = 0f;
            var vertical = 0f;

            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
            {
                horizontal -= 1f;
            }

            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
            {
                horizontal += 1f;
            }

            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
            {
                vertical -= 1f;
            }

            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
            {
                vertical += 1f;
            }

            return Vector2.ClampMagnitude(new Vector2(horizontal, vertical), 1f);
        }

        private static bool IsSprinting(Keyboard keyboard)
        {
            return keyboard != null && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
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

        private void ApplyGravityAndJump(Keyboard keyboard)
        {
            if (characterController.isGrounded && verticalVelocity < 0f)
            {
                verticalVelocity = -1f;
            }

            if (keyboard != null && keyboard.spaceKey.wasPressedThisFrame && characterController.isGrounded)
            {
                verticalVelocity = jumpVelocity;
            }

            verticalVelocity += gravity * Time.deltaTime;
        }
    }
}

