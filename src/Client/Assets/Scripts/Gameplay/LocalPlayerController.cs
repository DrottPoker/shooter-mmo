using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Click-to-move for the local player. A left click on the ground plans a path around
    // obstacles, the prediction starts walking it at once, and the worker follows the same
    // waypoints with authoritative collision. Owner snapshots reconcile the prediction.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterBody), typeof(LocalPlayerInput))]
    public sealed class LocalPlayerController : MonoBehaviour
    {
        private const int MaximumSimulationTicksPerFrame = 5;
        private const float HardReconciliationDistance = 3f;
        private const float ReconciliationSharpness = 12f;
        private const float MaximumClickDistance = 250f;

        [SerializeField] private Transform cameraTarget;
        [SerializeField] private OrbitCameraController playerCamera;

        private readonly WorldClickHandlerList clickHandlers = new WorldClickHandlerList();
        private readonly LocalMovementPresentation movementPresentation =
            new LocalMovementPresentation();
        private readonly CollisionQueryBuffer presentationGroundQuery =
            new CollisionQueryBuffer();
        private readonly CollisionQueryBuffer clickQuery = new CollisionQueryBuffer();
        private readonly GroundedVerticalPresentation groundedVerticalPresentation =
            new GroundedVerticalPresentation();
        private readonly SimulationVector3[] collisionStreamingAnchor =
            new SimulationVector3[1];
        private CharacterBody characterBody;
        private LocalPlayerInput playerInput;
        private RealtimeSimulationClient realtimeClient;
        private ClientMovementPrediction movementPrediction;
        private LocalPlayerNavigator navigator;
        private WorldPointerFocus pointerFocus;
        private MovementSimulationSettings networkSettings;
        private PlayerMovementState authoritativeState;
        private bool hasAuthoritativeState;
        private float networkTickAccumulator;
        private bool isInitialNetworkTickPending;
        private uint nextInputSequence;
        private uint clientTick;
        private Vector3 reconciliationOffset;
        private float reconciliationYawOffset;
        private long reconciliationCount;
        private long hardReconciliationCount;
        private float latestReconciliationDistance;

        public Transform CameraTarget
        {
            get { return cameraTarget != null ? cameraTarget : transform; }
        }

        public LocalPlayerInput PlayerInput
        {
            get { return playerInput; }
        }

        public OrbitCameraController PlayerCamera
        {
            get { return playerCamera; }
        }

        public bool IsServerAuthoritative
        {
            get { return movementPrediction != null && realtimeClient != null; }
        }

        public int PendingPredictedInputCount
        {
            get { return movementPrediction == null ? 0 : movementPrediction.PendingInputCount; }
        }

        public uint ClientSimulationTick
        {
            get { return clientTick; }
        }

        public long ReconciliationCount
        {
            get { return reconciliationCount; }
        }

        public long HardReconciliationCount
        {
            get { return hardReconciliationCount; }
        }

        public float LatestReconciliationDistance
        {
            get { return latestReconciliationDistance; }
        }

        // The path the player follows, or null while it stands.
        public MovementPath CurrentPath
        {
            get { return navigator != null ? navigator.CurrentPath : null; }
        }

        // True while the prediction still walks toward the end of the current path.
        public bool IsFollowingPath
        {
            get
            {
                var path = CurrentPath;
                return path != null && PredictedState.PathProgress.IsFollowing(path);
            }
        }

        public PlayerMovementState PredictedState
        {
            get { return movementPrediction != null ? movementPrediction.State : default(PlayerMovementState); }
        }

        // The newest walk, approach, or stop request, and the request whose plan applied last.
        public int NavigationIntentId
        {
            get { return navigator != null ? navigator.IntentId : 0; }
        }

        public int AppliedNavigationIntentId
        {
            get { return navigator != null ? navigator.AppliedIntentId : 0; }
        }

        public NavigationResult LatestNavigationResult
        {
            get
            {
                if (navigator == null)
                {
                    return NavigationResult.NoRoute;
                }

                return navigator.AppliedIntentId == navigator.IntentId
                    ? navigator.LastResult
                    : NavigationResult.Planning;
            }
        }

        public MovementPathPlanStatus LatestPlanStatus
        {
            get { return navigator != null ? navigator.LastStatus : MovementPathPlanStatus.NoPath; }
        }

        public int LatestPlanExpandedNodes
        {
            get { return navigator != null ? navigator.LastExpandedNodes : 0; }
        }

        public double LatestPlanMilliseconds
        {
            get { return navigator != null ? navigator.LastPlanMilliseconds : 0d; }
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
            if (playerInput == null)
            {
                CacheComponents();
            }

            if (!IsServerAuthoritative || !playerInput.IsConfigured)
            {
                return;
            }

            HandlePointerInput();
            navigator.Update(movementPrediction.State);
            UpdateNetworkMovement();
        }

        public bool EnableServerAuthoritativeMovement(RealtimeSimulationClient simulationClient)
        {
            if (simulationClient == null
                || !simulationClient.IsJoined
                || simulationClient.MovementSession == null)
            {
                return false;
            }

            DisableServerAuthoritativeMovement();
            CacheComponents();
            realtimeClient = simulationClient;
            var session = simulationClient.MovementSession;
            networkSettings = session.Settings;
            movementPrediction = new ClientMovementPrediction(
                session.InitialState,
                networkSettings,
                session.CarryState,
                session.CollisionWorld);
            navigator = new LocalPlayerNavigator(
                session.CollisionWorld,
                networkSettings,
                MovementPathPlannerSettings.Default);
            authoritativeState = session.InitialState;
            hasAuthoritativeState = true;
            realtimeClient.OwnerSimulationSnapshotReceived += OnOwnerSimulationSnapshotReceived;
            realtimeClient.CarryStateChanged += OnCarryStateChanged;
            networkTickAccumulator = 0f;
            isInitialNetworkTickPending = true;
            nextInputSequence = 0;
            clientTick = 0;
            ResetCorrections();
            movementPresentation.Reset(movementPrediction.State);
            ApplyNetworkPresentation(movementPresentation.State);
            return true;
        }

        public void DisableServerAuthoritativeMovement()
        {
            if (realtimeClient != null)
            {
                realtimeClient.OwnerSimulationSnapshotReceived -= OnOwnerSimulationSnapshotReceived;
                realtimeClient.CarryStateChanged -= OnCarryStateChanged;
            }

            realtimeClient = null;
            movementPrediction = null;
            navigator = null;
            networkSettings = null;
            hasAuthoritativeState = false;
            networkTickAccumulator = 0f;
            isInitialNetworkTickPending = false;
            ResetCorrections();
        }

        // The focus keeps clicks on interface panels from also moving the player.
        public void SetPointerFocus(WorldPointerFocus focus)
        {
            pointerFocus = focus;
        }

        // Handlers see a click in WorldClickOrder, whichever registered first.
        public void AddClickHandler(IWorldClickHandler handler, int order)
        {
            clickHandlers.Add(handler, order);
        }

        public void RemoveClickHandler(IWorldClickHandler handler)
        {
            clickHandlers.Remove(handler);
        }

        // Walks to the destination, or as close to it as the World allows. Planning runs in
        // the background, so this returns the intent id that LatestNavigationResult reports on.
        public int MoveTo(Vector3 destination)
        {
            return Navigate(destination, 0f);
        }

        // Walks until the player is within range of the target.
        public int Approach(Vector3 target, float range)
        {
            return Navigate(target, Mathf.Max(0f, range));
        }

        public int StopMoving()
        {
            return navigator != null ? navigator.Stop() : 0;
        }

        public bool TryGetAuthoritativePosition(out Vector3 position)
        {
            position = new Vector3(
                authoritativeState.PositionX,
                authoritativeState.PositionY,
                authoritativeState.PositionZ);
            return hasAuthoritativeState;
        }

        public bool TryGetPointerRay(out Ray ray)
        {
            ray = default(Ray);
            if (playerCamera == null || playerInput == null || !playerInput.IsConfigured)
            {
                return false;
            }

            var pointer = playerInput.PointerPosition;
            if (pointerFocus != null && pointerFocus.IsPointerOverInterface(pointer))
            {
                return false;
            }

            ray = playerCamera.ScreenPointToRay(pointer);
            return true;
        }

        private void OnDestroy()
        {
            DisableServerAuthoritativeMovement();
        }

        private void HandlePointerInput()
        {
            if (!playerInput.SelectPressedThisFrame || !TryGetPointerRay(out var ray))
            {
                return;
            }

            if (clickHandlers.TryHandle(ray))
            {
                return;
            }

            // The click raycast uses the same baked collision as the planner and the worker.
            if (CollisionRaycast.TryRaycast(
                    realtimeClient.MovementSession.CollisionWorld,
                    new SimulationVector3(ray.origin.x, ray.origin.y, ray.origin.z),
                    new SimulationVector3(ray.direction.x, ray.direction.y, ray.direction.z),
                    MaximumClickDistance,
                    CollisionLayers.CharacterMovement,
                    clickQuery,
                    out var hit))
            {
                MoveTo(new Vector3(hit.Point.X, hit.Point.Y, hit.Point.Z));
            }
        }

        private int Navigate(Vector3 destination, float range)
        {
            if (navigator == null)
            {
                return 0;
            }

            return navigator.Request(
                movementPrediction.State.Position,
                new SimulationVector3(destination.x, destination.y, destination.z),
                range);
        }

        private void UpdateNetworkMovement()
        {
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
            var collisionSession = realtimeClient != null
                ? realtimeClient.MovementSession
                : null;
            if (collisionSession != null
                && !collisionSession.TryEnsureCollisionChunks(
                    new[] { movementPrediction.State.Position },
                    out var collisionError))
            {
                realtimeClient.DisconnectForClientFailure(
                    "collision_stream_failed",
                    collisionError);
                return;
            }

            unchecked
            {
                nextInputSequence++;
                clientTick++;
            }

            var input = new PlayerMovementInput(
                nextInputSequence,
                clientTick,
                navigator.CurrentPath);
            movementPrediction.Predict(input);
            movementPresentation.Retarget(movementPrediction.State);
            var batch = movementPrediction.CreateRedundantInputBatch(out var unconfirmedPath);
            realtimeClient.TrySendMovementInputs(batch, unconfirmedPath);
        }

        private void OnOwnerSimulationSnapshotReceived(
            RealtimeOwnerSimulationSnapshot snapshot)
        {
            var movementSession = realtimeClient != null ? realtimeClient.MovementSession : null;
            if (movementSession == null)
            {
                return;
            }

            var state = snapshot.State;
            collisionStreamingAnchor[0] = new SimulationVector3(
                state.PositionX,
                state.PositionY,
                state.PositionZ);
            if (!movementSession.TryRefreshCollisionStreaming(
                    collisionStreamingAnchor,
                    out var collisionError))
            {
                realtimeClient.DisconnectForClientFailure(
                    "collision_stream_failed",
                    collisionError);
                return;
            }

            var progress = snapshot.PathProgress;
            authoritativeState = new PlayerMovementState(
                state.PositionX,
                state.PositionY,
                state.PositionZ,
                state.VelocityX,
                state.VelocityY,
                state.VelocityZ,
                state.YawDegrees,
                state.IsGrounded,
                new MovementPathProgress(
                    progress.PathId,
                    progress.NextWaypointIndex,
                    progress.BlockedTicks));
            hasAuthoritativeState = true;
            var reconciliation = movementPrediction.Reconcile(
                authoritativeState,
                snapshot.LastProcessedInputSequence);
            ApplyReconciliationCorrection(reconciliation);
        }

        private void OnCarryStateChanged(PlayerCarryState carryState)
        {
            if (movementPrediction != null)
            {
                movementPrediction.ApplyCarryState(carryState);
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
            latestReconciliationDistance = positionError.magnitude;
            reconciliationCount++;
            if (latestReconciliationDistance >= HardReconciliationDistance)
            {
                hardReconciliationCount++;
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
        }

        public void Teleport(Vector3 position, Quaternion rotation)
        {
            CacheComponents();
            characterBody.Teleport(position, rotation);
            groundedVerticalPresentation.Clear();
        }

        public void RefreshCharacterDimensions()
        {
            CacheComponents();
            characterBody.RefreshDimensions();
        }

        private void ResetCorrections()
        {
            reconciliationOffset = Vector3.zero;
            reconciliationYawOffset = 0f;
            reconciliationCount = 0;
            hardReconciliationCount = 0;
            latestReconciliationDistance = 0f;
            groundedVerticalPresentation.Clear();
        }

        private void CacheComponents()
        {
            if (characterBody == null)
            {
                characterBody = GetComponent<CharacterBody>();
            }

            if (playerInput == null)
            {
                playerInput = GetComponent<LocalPlayerInput>();
            }
        }
    }
}
