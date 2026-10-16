using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // A RuneScape-style camera that orbits the player. Dragging with the middle mouse
    // button or holding the arrow keys turns and tilts it, and the scroll wheel zooms. The
    // cursor always stays free for click-to-move.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class OrbitCameraController : MonoBehaviour
    {
        private const int CollisionHitCapacity = 16;

        [SerializeField] private Transform target;
        [SerializeField, Min(0.5f)] private float minimumDistance = 3f;
        [SerializeField, Min(0.5f)] private float maximumDistance = 30f;
        [SerializeField, Min(0.5f)] private float initialDistance = 12f;
        [SerializeField, Range(0f, 89f)] private float minimumPitch = 10f;
        [SerializeField, Range(0f, 89f)] private float maximumPitch = 80f;
        [SerializeField, Range(0f, 89f)] private float initialPitch = 40f;
        [SerializeField, Min(0f)] private float dragDegreesPerPixel = 0.25f;
        [SerializeField, Min(0f)] private float keyYawDegreesPerSecond = 120f;
        [SerializeField, Min(0f)] private float keyPitchDegreesPerSecond = 60f;
        [SerializeField, Range(0.01f, 0.9f)] private float zoomStepFraction = 0.15f;
        [SerializeField, Min(0.01f)] private float zoomSharpness = 12f;
        [SerializeField, Min(0.01f)] private float obstructionReleaseSharpness = 6f;
        [SerializeField, Range(1f, 179f)] private float fieldOfView = 55f;
        [SerializeField, Min(0.01f)] private float collisionRadius = 0.25f;
        [SerializeField, Min(0f)] private float collisionPadding = 0.1f;
        [SerializeField] private LayerMask collisionLayers = ~0;

        private readonly RaycastHit[] collisionHits = new RaycastHit[CollisionHitCapacity];
        private Camera controlledCamera;
        private LocalPlayerInput playerInput;
        private WorldPointerFocus pointerFocus;
        private Transform collisionIgnoreRoot;
        private float yaw;
        private float pitch;
        private float zoomDistance;
        private float smoothedZoomDistance;
        private float appliedDistance;
        private bool poseInitialized;

        public Camera Camera
        {
            get
            {
                if (controlledCamera == null)
                {
                    controlledCamera = GetComponent<Camera>();
                }

                return controlledCamera;
            }
        }

        public float Yaw
        {
            get { return yaw; }
        }

        public float Pitch
        {
            get { return pitch; }
        }

        public float ZoomDistance
        {
            get { return zoomDistance; }
        }

        private void Awake()
        {
            InitializePose();
        }

        private void OnEnable()
        {
            InitializePose();
            ReleaseCursor();
        }

        private void LateUpdate()
        {
            if (target == null)
            {
                return;
            }

            if (playerInput == null)
            {
                playerInput = target.GetComponentInParent<LocalPlayerInput>();
                if (playerInput != null)
                {
                    collisionIgnoreRoot = playerInput.transform;
                    playerInput.Initialize();
                }
            }

            if (playerInput != null)
            {
                // Scrolling a panel must not zoom the camera behind it.
                var overInterface = pointerFocus != null
                    && pointerFocus.IsPointerOverInterface(playerInput.PointerPosition);
                ApplyInput(
                    playerInput.CameraOrbitHeld ? playerInput.CameraOrbitDelta : Vector2.zero,
                    playerInput.CameraRotate,
                    overInterface ? 0f : playerInput.CameraZoom,
                    Time.unscaledDeltaTime);
            }

            ApplyCameraTransform(Time.unscaledDeltaTime);
        }

        public void SetTarget(Transform followTarget, LocalPlayerInput input)
        {
            target = followTarget;
            playerInput = input;
            collisionIgnoreRoot = input != null ? input.transform : followTarget;
            if (followTarget != null)
            {
                // Start behind the player, looking where it faces.
                yaw = followTarget.eulerAngles.y;
                appliedDistance = smoothedZoomDistance;
            }
        }

        public void SetPointerFocus(WorldPointerFocus focus)
        {
            pointerFocus = focus;
        }

        public Ray ScreenPointToRay(Vector2 screenPosition)
        {
            return Camera.ScreenPointToRay(new Vector3(screenPosition.x, screenPosition.y, 0f));
        }

        // Turns, tilts, and zooms the camera. The orbit delta is in pixels, the rotate
        // input in -1..1 per axis, and the zoom in scroll notches, positive toward the player.
        public void ApplyInput(Vector2 orbitDelta, Vector2 rotate, float zoom, float deltaTime)
        {
            InitializePose();
            var clampedDeltaTime = Mathf.Max(0f, deltaTime);
            yaw = Mathf.Repeat(
                yaw
                + (orbitDelta.x * dragDegreesPerPixel)
                - (rotate.x * keyYawDegreesPerSecond * clampedDeltaTime),
                360f);
            pitch = Mathf.Clamp(
                pitch
                - (orbitDelta.y * dragDegreesPerPixel)
                + (rotate.y * keyPitchDegreesPerSecond * clampedDeltaTime),
                minimumPitch,
                maximumPitch);

            // One notch zooms by a fixed share of the distance, which feels even at every
            // zoom level. Platform scroll ranges differ, so a frame counts as one notch.
            var notches = Mathf.Clamp(zoom, -1f, 1f);
            if (!Mathf.Approximately(notches, 0f))
            {
                zoomDistance = Mathf.Clamp(
                    zoomDistance * Mathf.Pow(1f - zoomStepFraction, notches),
                    minimumDistance,
                    maximumDistance);
            }
        }

        private void InitializePose()
        {
            if (poseInitialized)
            {
                return;
            }

            Camera.fieldOfView = fieldOfView;
            pitch = Mathf.Clamp(initialPitch, minimumPitch, maximumPitch);
            zoomDistance = Mathf.Clamp(initialDistance, minimumDistance, maximumDistance);
            smoothedZoomDistance = zoomDistance;
            appliedDistance = zoomDistance;
            poseInitialized = true;
        }

        private void ApplyCameraTransform(float deltaTime)
        {
            var zoomBlend = 1f - Mathf.Exp(-zoomSharpness * deltaTime);
            smoothedZoomDistance = Mathf.Lerp(smoothedZoomDistance, zoomDistance, zoomBlend);

            var focus = target.position;
            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            var direction = rotation * Vector3.back;
            var allowedDistance = ResolveCollisionDistance(focus, direction, smoothedZoomDistance);

            // Geometry pulls the camera in at once and lets it out smoothly, so walking past
            // a wall never makes the view jump back and forth.
            if (allowedDistance < appliedDistance)
            {
                appliedDistance = allowedDistance;
            }
            else
            {
                var releaseBlend = 1f - Mathf.Exp(-obstructionReleaseSharpness * deltaTime);
                appliedDistance = Mathf.Lerp(appliedDistance, allowedDistance, releaseBlend);
            }

            transform.SetPositionAndRotation(focus + (direction * appliedDistance), rotation);
        }

        private float ResolveCollisionDistance(Vector3 focus, Vector3 direction, float desiredDistance)
        {
            var hitCount = Physics.SphereCastNonAlloc(
                focus,
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

        private static void ReleaseCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
