using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using ShooterMmo.Networking;
using UnityEngine;

namespace ShooterMmo.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterBody), typeof(LocalPlayerInput))]
    public sealed class LocalPlayerController : MonoBehaviour
    {
        [SerializeField] private float walkSpeed = 5f;
        [SerializeField] private float sprintSpeed = 8f;
        [SerializeField] private float rotationSpeed = 720f;
        [SerializeField] private float gravity = -24f;
        [SerializeField] private float jumpVelocity = 7f;
        [SerializeField] private float groundedVerticalVelocity = -2f;
        [SerializeField] private Transform cameraTarget;
        [SerializeField] private ThirdPersonCameraController playerCamera;

        private CharacterBody characterBody;
        private CharacterController characterController;
        private LocalPlayerInput playerInput;
        private float verticalVelocity;
        private bool isSprinting;
        private RealtimeWorldClient realtimeClient;
        private ClientMovementPrediction movementPrediction;
        private readonly LocalMovementPresentation movementPresentation =
            new LocalMovementPresentation();
        private readonly CollisionQueryBuffer presentationGroundQuery =
            new CollisionQueryBuffer();
        private readonly GroundedVerticalPresentation groundedVerticalPresentation =
            new GroundedVerticalPresentation();
        private MovementSimulationSettings networkSettings;
        private float networkTickAccumulator;
        private bool isInitialNetworkTickPending;
        private uint nextInputSequence;
        private uint clientTick;
        private bool jumpQueued;
        private Vector3 reconciliationOffset;
        private float reconciliationYawOffset;

        private const int MaximumSimulationTicksPerFrame = 5;
        private const float HardReconciliationDistance = 3f;
        private const float ReconciliationSharpness = 12f;

        public Transform CameraTarget
        {
            get { return cameraTarget != null ? cameraTarget : transform; }
        }

        public LocalPlayerInput PlayerInput
        {
            get { return playerInput; }
        }

        public ThirdPersonCameraController PlayerCamera
        {
            get { return playerCamera; }
        }

        public bool IsSprinting
        {
            get { return isSprinting; }
        }

        public bool IsServerAuthoritative
        {
            get { return movementPrediction != null && realtimeClient != null; }
        }

        private void Awake()
        {
            CacheComponents();
        }

        private void OnEnable()
        {
            CacheComponents();
        }

        private void Update()
        {
            if (characterController == null || playerInput == null)
            {
                CacheComponents();
            }

            if (!playerInput.IsConfigured)
            {
                return;
            }

            if (IsServerAuthoritative)
            {
                UpdateNetworkMovement();
                return;
            }

            UpdateOfflineMovement();
        }

        private void UpdateOfflineMovement()
        {

            var moveInput = Vector2.ClampMagnitude(playerInput.Move, 1f);
            var planarMove = BuildCameraRelativeMove(moveInput);
            UpdateSprintState();
            var speed = isSprinting ? sprintSpeed : walkSpeed;

            ApplyGravityAndJump();

            var velocity = planarMove * speed;
            velocity.y = verticalVelocity;
            characterController.Move(velocity * Time.deltaTime);

            var facingDirection = playerInput.AimHeld
                ? GetCameraPlanarForward()
                : planarMove;
            if (facingDirection.sqrMagnitude > 0.001f)
            {
                var targetRotation = Quaternion.LookRotation(facingDirection, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation,
                    targetRotation,
                    rotationSpeed * Time.deltaTime);
            }
        }

        public bool EnableServerAuthoritativeMovement(RealtimeWorldClient worldClient)
        {
            if (worldClient == null
                || !worldClient.IsJoined
                || worldClient.MovementSession == null)
            {
                return false;
            }

            DisableServerAuthoritativeMovement();
            realtimeClient = worldClient;
            networkSettings = worldClient.MovementSession.Settings;
            movementPrediction = new ClientMovementPrediction(
                worldClient.MovementSession.InitialState,
                networkSettings,
                worldClient.MovementSession.CollisionWorld);
            realtimeClient.WorldSnapshotReceived += OnWorldSnapshotReceived;
            networkTickAccumulator = 0f;
            isInitialNetworkTickPending = true;
            nextInputSequence = 0;
            clientTick = 0;
            jumpQueued = false;
            reconciliationOffset = Vector3.zero;
            reconciliationYawOffset = 0f;
            groundedVerticalPresentation.Clear();
            movementPresentation.Reset(movementPrediction.State);
            ApplyNetworkPresentation(movementPresentation.State);
            return true;
        }

        public void DisableServerAuthoritativeMovement()
        {
            if (realtimeClient != null)
            {
                realtimeClient.WorldSnapshotReceived -= OnWorldSnapshotReceived;
            }

            realtimeClient = null;
            movementPrediction = null;
            networkSettings = null;
            networkTickAccumulator = 0f;
            isInitialNetworkTickPending = false;
            jumpQueued = false;
            reconciliationOffset = Vector3.zero;
            reconciliationYawOffset = 0f;
            groundedVerticalPresentation.Clear();
        }

        private void OnDestroy()
        {
            DisableServerAuthoritativeMovement();
        }

        private void UpdateNetworkMovement()
        {
            jumpQueued |= playerInput.JumpPressedThisFrame;
            if (playerInput.AimHeld)
            {
                jumpQueued = false;
            }

            if (isInitialNetworkTickPending)
            {
                PredictAndSendNetworkTick();
                isInitialNetworkTickPending = false;
            }

            networkTickAccumulator += Mathf.Min(Time.deltaTime, 0.25f);
            var processedTicks = 0;
            while (networkTickAccumulator >= networkSettings.FixedDeltaTime
                && processedTicks < MaximumSimulationTicksPerFrame)
            {
                PredictAndSendNetworkTick();
                networkTickAccumulator -= networkSettings.FixedDeltaTime;
                processedTicks++;
            }

            if (processedTicks == MaximumSimulationTicksPerFrame
                && networkTickAccumulator >= networkSettings.FixedDeltaTime)
            {
                networkTickAccumulator = 0f;
            }

            var correctionBlend = 1f - Mathf.Exp(-ReconciliationSharpness * Time.deltaTime);
            reconciliationOffset = Vector3.Lerp(
                reconciliationOffset,
                Vector3.zero,
                correctionBlend);
            reconciliationYawOffset = Mathf.Lerp(
                reconciliationYawOffset,
                0f,
                correctionBlend);
            movementPresentation.Advance(
                Time.deltaTime,
                networkSettings.FixedDeltaTime);
            ApplyNetworkPresentation(movementPresentation.State);
        }

        private void PredictAndSendNetworkTick()
        {
            unchecked
            {
                nextInputSequence++;
                clientTick++;
            }

            var move = Vector2.ClampMagnitude(playerInput.Move, 1f);
            var buttons = PlayerMovementButtons.None;
            var isAiming = playerInput.AimHeld;
            if (isAiming)
            {
                jumpQueued = false;
                buttons |= PlayerMovementButtons.Aim;
            }
            else if (playerInput.SprintHeld)
            {
                buttons |= PlayerMovementButtons.Sprint;
            }

            if (!isAiming && jumpQueued)
            {
                buttons |= PlayerMovementButtons.Jump;
                jumpQueued = false;
            }

            var cameraYaw = playerCamera != null
                ? playerCamera.transform.eulerAngles.y
                : transform.eulerAngles.y;
            var input = new PlayerMovementInput(
                nextInputSequence,
                clientTick,
                move.x,
                move.y,
                cameraYaw,
                buttons);
            movementPrediction.Predict(input);
            movementPresentation.Retarget(movementPrediction.State);
            realtimeClient.TrySendMovementInputs(movementPrediction.CreateRedundantInputBatch());
        }

        private void OnWorldSnapshotReceived(RealtimeWorldSnapshot snapshot)
        {
            var movementSession = realtimeClient != null ? realtimeClient.MovementSession : null;
            if (movementSession == null)
            {
                return;
            }

            for (var index = 0; index < snapshot.Entities.Length; index++)
            {
                var entity = snapshot.Entities[index];
                if (entity.EntityId != movementSession.ControlledEntityId)
                {
                    continue;
                }

                var state = entity.State;
                var authoritative = new PlayerMovementState(
                    state.PositionX,
                    state.PositionY,
                    state.PositionZ,
                    state.VelocityX,
                    state.VelocityY,
                    state.VelocityZ,
                    state.YawDegrees,
                    state.IsGrounded,
                    state.IsSprinting);
                var reconciliation = movementPrediction.Reconcile(
                    authoritative,
                    entity.LastProcessedInputSequence);
                ApplyReconciliationCorrection(reconciliation);
                return;
            }
        }

        private void ApplyReconciliationCorrection(MovementReconciliation reconciliation)
        {
            var previous = reconciliation.PreviousPrediction;
            var corrected = reconciliation.ReconciledPrediction;
            var positionError = new Vector3(
                previous.PositionX - corrected.PositionX,
                previous.PositionY - corrected.PositionY,
                previous.PositionZ - corrected.PositionZ);
            if (positionError.magnitude >= HardReconciliationDistance)
            {
                movementPresentation.Reset(corrected);
                groundedVerticalPresentation.Clear();
                reconciliationOffset = Vector3.zero;
                reconciliationYawOffset = 0f;
                return;
            }

            movementPresentation.ApplySimulationCorrection(corrected);
            reconciliationOffset += positionError;
            reconciliationYawOffset += Mathf.DeltaAngle(
                corrected.YawDegrees,
                previous.YawDegrees);
        }

        private void ApplyNetworkPresentation(PlayerMovementState state)
        {
            var position = new Vector3(
                state.PositionX,
                state.PositionY,
                state.PositionZ) + reconciliationOffset;
            var movementSession = realtimeClient != null
                ? realtimeClient.MovementSession
                : null;
            var shouldSmoothGroundedHeight = false;
            if (movementSession != null
                && GroundedMovementPresentation.TryGetVisualHeight(
                    position.x,
                    position.y,
                    position.z,
                    state.IsGrounded,
                    movementSession.CollisionWorld,
                    networkSettings.CharacterCollision,
                    presentationGroundQuery,
                    out var visualHeight,
                    out var groundNormal))
            {
                position.y = visualHeight;
                shouldSmoothGroundedHeight = GroundedMovementPresentation.IsFlatGround(
                    groundNormal);
            }

            var maximumSmoothDistance = networkSettings.CharacterCollision.StepHeight
                + networkSettings.CharacterCollision.GroundSnapDistance;
            position.y = groundedVerticalPresentation.Update(
                position.y,
                state.IsGrounded && shouldSmoothGroundedHeight,
                maximumSmoothDistance,
                GroundedMovementPresentation.StepSmoothingDurationSeconds,
                Time.deltaTime);

            var yaw = state.YawDegrees + reconciliationYawOffset;
            characterBody.Teleport(position, Quaternion.Euler(0f, yaw, 0f));
            verticalVelocity = state.VelocityY;
            isSprinting = state.IsSprinting;
        }

        public void Teleport(Vector3 position, Quaternion rotation)
        {
            if (characterController == null)
            {
                CacheComponents();
            }

            characterBody.Teleport(position, rotation);
            groundedVerticalPresentation.Clear();
            verticalVelocity = 0f;
            isSprinting = false;
        }

        public void RefreshCharacterDimensions()
        {
            CacheComponents();
            characterBody.RefreshDimensions();
            characterController = characterBody.Controller;
        }

        private void CacheComponents()
        {
            if (characterBody == null)
            {
                characterBody = GetComponent<CharacterBody>();
            }

            if (characterController == null && characterBody != null)
            {
                characterController = characterBody.Controller;
            }

            if (playerInput == null)
            {
                playerInput = GetComponent<LocalPlayerInput>();
            }
        }

        private void UpdateSprintState()
        {
            if (playerInput.AimHeld || !playerInput.SprintHeld)
            {
                isSprinting = false;
                return;
            }

            if (!isSprinting && characterController.isGrounded)
            {
                isSprinting = true;
            }
        }

        private Vector3 BuildCameraRelativeMove(Vector2 moveInput)
        {
            if (moveInput.sqrMagnitude <= 0.001f)
            {
                return Vector3.zero;
            }

            var referenceTransform = playerCamera != null ? playerCamera.transform : transform;
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

        private Vector3 GetCameraPlanarForward()
        {
            if (playerCamera == null)
            {
                return transform.forward;
            }

            var forward = playerCamera.transform.forward;
            forward.y = 0f;
            return forward.sqrMagnitude > 0.001f ? forward.normalized : transform.forward;
        }

        private void ApplyGravityAndJump()
        {
            if (characterController.isGrounded && verticalVelocity < 0f)
            {
                verticalVelocity = groundedVerticalVelocity;
            }

            if (playerInput.JumpPressedThisFrame
                && characterController.isGrounded
                && !playerInput.AimHeld)
            {
                verticalVelocity = jumpVelocity;
            }

            verticalVelocity += gravity * Time.deltaTime;
        }

    }
}
