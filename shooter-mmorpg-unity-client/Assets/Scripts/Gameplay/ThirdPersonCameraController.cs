using UnityEngine;

namespace ShooterMmo.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class ThirdPersonCameraController : MonoBehaviour
    {
        private const int CollisionHitCapacity = 16;

        [SerializeField] private Transform target;
        [SerializeField, Min(0f)] private float shoulderOffset = 1.1f;
        [SerializeField, Min(0f)] private float aimShoulderOffset = 1.3f;
        [SerializeField, Min(0f)] private float verticalOffset = 0.45f;
        [SerializeField, Min(0f)] private float aimVerticalOffset = 0.35f;
        [SerializeField, Min(0.1f)] private float distance = 4.75f;
        [SerializeField, Min(0.1f)] private float aimDistance = 4.25f;
        [SerializeField] private float mouseSensitivity = 0.12f;
        [SerializeField] private float initialPitch = 12f;
        [SerializeField] private float minPitch = -50f;
        [SerializeField] private float maxPitch = 75f;
        [SerializeField, Range(1f, 179f)] private float normalFieldOfView = 60f;
        [SerializeField, Range(1f, 179f)] private float aimFieldOfView = 45f;
        [SerializeField, Min(0.01f)] private float aimTransitionSharpness = 12f;
        [SerializeField] private float collisionRadius = 0.25f;
        [SerializeField] private float collisionPadding = 0.1f;
        [SerializeField] private LayerMask collisionLayers = ~0;

        private readonly RaycastHit[] collisionHits = new RaycastHit[CollisionHitCapacity];

        private Camera controlledCamera;
        private LocalPlayerInput playerInput;
        private Transform collisionIgnoreRoot;
        private float yaw;
        private float pitch;
        private float currentShoulderOffset;
        private float currentVerticalOffset;
        private float currentDistance;
        private float currentFieldOfView;
        private bool debugCursorReleased;
        private bool uiCursorReleased;
        private bool applicationHasFocus;
        private bool cameraPoseInitialized;

        private void Awake()
        {
            InitializeCameraState();
        }

        private void OnEnable()
        {
            applicationHasFocus = Application.isFocused;
            InitializeCameraState();
            RestoreTargetInput();

            if (target != null)
            {
                ApplyCursorMode();
            }
        }

        private void InitializeCameraState()
        {
            if (controlledCamera == null)
            {
                controlledCamera = GetComponent<Camera>();
            }

            currentShoulderOffset = shoulderOffset;
            currentVerticalOffset = verticalOffset;
            currentDistance = distance;
            currentFieldOfView = normalFieldOfView;
            controlledCamera.fieldOfView = currentFieldOfView;

            if (!cameraPoseInitialized)
            {
                pitch = Mathf.Clamp(initialPitch, minPitch, maxPitch);
                cameraPoseInitialized = true;
            }
        }

        private void OnDisable()
        {
            SetPointerInputEnabled(false);
            ReleaseCursor();

            if (controlledCamera != null)
            {
                controlledCamera.fieldOfView = normalFieldOfView;
            }
        }

        private void LateUpdate()
        {
            if (target == null)
            {
                SetPointerInputEnabled(false);
                ReleaseCursor();
                return;
            }

            RestoreTargetInput();

            if (playerInput != null && playerInput.ToggleDebugCursorPressedThisFrame)
            {
                debugCursorReleased = !debugCursorReleased;
                ApplyCursorMode();
            }

            var isAiming = playerInput != null && playerInput.AimHeld;
            ReadCameraInput();
            UpdateAimPresentation(isAiming);
            ApplyCameraTransform();
        }

        public void SetTarget(Transform followTarget, LocalPlayerInput input)
        {
            target = followTarget;
            playerInput = input;
            collisionIgnoreRoot = input != null ? input.transform : followTarget;
            if (followTarget != null)
            {
                yaw = followTarget.eulerAngles.y;
                debugCursorReleased = false;
                ApplyCursorMode();
            }
        }

        public void SetUiCursorReleased(bool isReleased)
        {
            uiCursorReleased = isReleased;
            if (target != null)
            {
                ApplyCursorMode();
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            applicationHasFocus = hasFocus;
            if (target != null)
            {
                ApplyCursorMode();
            }
        }

        private void ReadCameraInput()
        {
            if (debugCursorReleased || uiCursorReleased || playerInput == null)
            {
                return;
            }

            var delta = playerInput.LookDelta;
            yaw += delta.x * mouseSensitivity;
            pitch = Mathf.Clamp(pitch - (delta.y * mouseSensitivity), minPitch, maxPitch);
        }

        private void RestoreTargetInput()
        {
            if (playerInput == null && target != null)
            {
                playerInput = target.GetComponentInParent<LocalPlayerInput>();
                if (playerInput != null)
                {
                    collisionIgnoreRoot = playerInput.transform;
                    playerInput.Initialize();
                }
            }
        }

        private void UpdateAimPresentation(bool isAiming)
        {
            if (controlledCamera == null)
            {
                InitializeCameraState();
            }

            var blend = 1f - Mathf.Exp(-aimTransitionSharpness * Time.deltaTime);
            var targetShoulderOffset = isAiming ? aimShoulderOffset : shoulderOffset;
            var targetVerticalOffset = isAiming ? aimVerticalOffset : verticalOffset;
            var targetDistance = isAiming ? aimDistance : distance;
            var targetFieldOfView = isAiming ? aimFieldOfView : normalFieldOfView;

            currentShoulderOffset = Mathf.Lerp(currentShoulderOffset, targetShoulderOffset, blend);
            currentVerticalOffset = Mathf.Lerp(currentVerticalOffset, targetVerticalOffset, blend);
            currentDistance = Mathf.Lerp(currentDistance, targetDistance, blend);
            currentFieldOfView = Mathf.Lerp(currentFieldOfView, targetFieldOfView, blend);
            controlledCamera.fieldOfView = currentFieldOfView;
        }

        private void ApplyCameraTransform()
        {
            var focusPosition = target.position;
            var cameraRotation = Quaternion.Euler(pitch, yaw, 0f);
            var desiredOffset = cameraRotation
                * new Vector3(currentShoulderOffset, 0f, -currentDistance);
            desiredOffset += Vector3.up * currentVerticalOffset;
            if (desiredOffset.sqrMagnitude < 0.0001f)
            {
                desiredOffset = cameraRotation * (Vector3.back * 0.1f);
            }

            var desiredDistance = desiredOffset.magnitude;
            var direction = desiredOffset / desiredDistance;
            var collisionDistance = ResolveCollisionDistance(focusPosition, direction, desiredDistance);
            var cameraPosition = focusPosition + (direction * collisionDistance);

            transform.SetPositionAndRotation(cameraPosition, cameraRotation);
        }

        private float ResolveCollisionDistance(Vector3 focusPosition, Vector3 direction, float desiredDistance)
        {
            var hitCount = Physics.SphereCastNonAlloc(
                focusPosition,
                collisionRadius,
                direction,
                collisionHits,
                desiredDistance,
                collisionLayers,
                QueryTriggerInteraction.Ignore);
            var resolvedDistance = desiredDistance;

            for (var index = 0; index < hitCount; index++)
            {
                var hit = collisionHits[index];
                if (hit.collider == null
                    || (collisionIgnoreRoot != null
                        && hit.collider.transform.IsChildOf(collisionIgnoreRoot)))
                {
                    continue;
                }

                resolvedDistance = Mathf.Min(
                    resolvedDistance,
                    Mathf.Max(0.1f, hit.distance - collisionPadding));
            }

            return resolvedDistance;
        }

        private void ApplyCursorMode()
        {
            var shouldCapture = !debugCursorReleased
                && !uiCursorReleased
                && applicationHasFocus;
            SetPointerInputEnabled(shouldCapture);

            if (shouldCapture)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                return;
            }

            ReleaseCursor();
        }

        private void SetPointerInputEnabled(bool isEnabled)
        {
            if (playerInput != null)
            {
                playerInput.SetPointerInputEnabled(isEnabled);
            }
        }

        private static void ReleaseCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
