using UnityEngine;
using UnityEngine.InputSystem;

namespace ShooterMmo.Gameplay
{
    public sealed class ThirdPersonCameraController : MonoBehaviour
    {
        private const int CollisionHitCapacity = 16;

        [SerializeField] private Transform target;
        [SerializeField] private float distance = 6f;
        [SerializeField] private float minDistance = 3f;
        [SerializeField] private float maxDistance = 10f;
        [SerializeField] private float mouseSensitivity = 0.12f;
        [SerializeField] private float zoomSensitivity = 0.03f;
        [SerializeField] private float minPitch = -15f;
        [SerializeField] private float maxPitch = 55f;
        [SerializeField] private float collisionRadius = 0.25f;
        [SerializeField] private float collisionPadding = 0.1f;
        [SerializeField] private LayerMask collisionLayers = ~0;

        private readonly RaycastHit[] collisionHits = new RaycastHit[CollisionHitCapacity];

        private InputAction orbitAction;
        private InputAction lookAction;
        private InputAction zoomAction;
        private float yaw;
        private float pitch = 20f;
        private bool ownsCursorLock;

        private void Awake()
        {
            orbitAction = new InputAction("Orbit Camera", InputActionType.Button, "<Mouse>/rightButton");
            lookAction = new InputAction("Look", InputActionType.Value, "<Mouse>/delta");
            zoomAction = new InputAction("Zoom", InputActionType.Value, "<Mouse>/scroll/y");
        }

        private void OnEnable()
        {
            orbitAction.Enable();
            lookAction.Enable();
            zoomAction.Enable();
        }

        private void OnDisable()
        {
            orbitAction.Disable();
            lookAction.Disable();
            zoomAction.Disable();
            ReleaseCursorLock();
        }

        private void OnDestroy()
        {
            orbitAction.Dispose();
            lookAction.Dispose();
            zoomAction.Dispose();
        }

        private void LateUpdate()
        {
            if (target == null)
            {
                ReleaseCursorLock();
                return;
            }

            ReadCameraInput();
            ApplyCameraTransform();
        }

        public void SetTarget(Transform followTarget)
        {
            target = followTarget;
            if (followTarget != null)
            {
                yaw = followTarget.eulerAngles.y;
            }
        }

        private void ReadCameraInput()
        {
            if (orbitAction.IsPressed())
            {
                ownsCursorLock = true;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;

                var delta = lookAction.ReadValue<Vector2>();
                yaw += delta.x * mouseSensitivity;
                pitch = Mathf.Clamp(pitch - (delta.y * mouseSensitivity), minPitch, maxPitch);
            }
            else
            {
                ReleaseCursorLock();
            }

            var scroll = zoomAction.ReadValue<float>();
            if (Mathf.Abs(scroll) > 0.01f)
            {
                distance = Mathf.Clamp(distance - (scroll * zoomSensitivity), minDistance, maxDistance);
            }
        }

        private void ApplyCameraTransform()
        {
            var focusPosition = target.position;
            var cameraRotation = Quaternion.Euler(pitch, yaw, 0f);
            var direction = cameraRotation * Vector3.back;
            var collisionDistance = ResolveCollisionDistance(focusPosition, direction, distance);
            var cameraPosition = focusPosition + (direction * collisionDistance);

            transform.SetPositionAndRotation(
                cameraPosition,
                Quaternion.LookRotation(focusPosition - cameraPosition, Vector3.up));
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
                if (hit.collider == null || hit.collider.transform.root == target.root)
                {
                    continue;
                }

                resolvedDistance = Mathf.Min(
                    resolvedDistance,
                    Mathf.Max(0.1f, hit.distance - collisionPadding));
            }

            return resolvedDistance;
        }

        private void ReleaseCursorLock()
        {
            if (!ownsCursorLock)
            {
                return;
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            ownsCursorLock = false;
        }
    }
}
