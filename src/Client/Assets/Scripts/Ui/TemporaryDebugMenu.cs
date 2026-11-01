using System;
using System.Collections;
using System.Globalization;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Worlds;
using Mmorpg.GameProtocol;
using TMPro;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The Debug menu (F2): what this client observes about itself, in tabs. Overview shows where
    // the player and the pointer are, a quick check of the frame and the connection, and what the
    // world draws for debugging, such as the collision; Performance, Network, and Movement show the
    // details. It is closed when a World scene loads, and refreshes its values four times a
    // second, the pace the frame telemetry changes at. It also owns the World scene's session
    // check and Leave Shard.
    public sealed class TemporaryDebugMenu : MonoBehaviour
    {
        public const string WindowName = "DebugMenu";

        private const float RefreshSeconds = 0.25f;
        private const float TargetFrameBudgetMilliseconds = 1000f / 60f;

        private readonly ClientOperationState operationState = new ClientOperationState();
        private readonly WorldDebugTelemetryTracker telemetry = new WorldDebugTelemetryTracker();
        private RealtimeSimulationClient simulationClient;
        private PlayerBodyTintSetting bodyTint;
        private WorldDebugViewSettings viewSettings;
        private Material lineMaterial;
        private LocalPlayerInput localPlayerInput;
        private LocalPlayerController localPlayerController;
        private TemporaryMenuWindow window;
        private CollisionDebugOverlay overlay;
        private float nextRefreshTime;
        private string status = "Realtime simulation connection active.";
        private Color statusColor = TemporaryUiKit.TextSecondary;
        private bool hasPointerPoint;
        private Vector3 pointerPoint;

        private TextMeshProUGUI playerPosition;
        private TextMeshProUGUI playerFacing;
        private TextMeshProUGUI playerChunk;
        private TextMeshProUGUI pointerPosition;
        private TextMeshProUGUI glanceFrame;
        private TextMeshProUGUI glancePing;
        private TextMeshProUGUI glanceSnapshots;
        private TextMeshProUGUI glanceConnection;
        private Toggle collisionToggle;
        private Toggle pathToggle;
        private Toggle tintToggle;
        private TextMeshProUGUI collisionShapes;

        private TextMeshProUGUI renderingFps;
        private TextMeshProUGUI renderingFrame;
        private TextMeshProUGUI renderingResolution;
        private TextMeshProUGUI renderingVsync;
        private TextMeshProUGUI renderingMemory;
        private TextMeshProUGUI renderingHeap;

        private TextMeshProUGUI connectionState;
        private TextMeshProUGUI connectionCharacter;
        private TextMeshProUGUI connectionWorld;
        private TextMeshProUGUI connectionWorker;
        private TextMeshProUGUI connectionEndpoint;
        private TextMeshProUGUI connectionTick;
        private TextMeshProUGUI networkPing;
        private TextMeshProUGUI networkSnapshotAge;
        private TextMeshProUGUI networkSnapshotRate;
        private TextMeshProUGUI networkSnapshotPackets;
        private TextMeshProUGUI networkPackets;
        private TextMeshProUGUI networkPayload;
        private TextMeshProUGUI networkLoss;
        private TextMeshProUGUI networkTotals;
        private TextMeshProUGUI networkEntities;
        private TextMeshProUGUI networkWindow;

        private TextMeshProUGUI movementAuthority;
        private TextMeshProUGUI movementRates;
        private TextMeshProUGUI movementSpeed;
        private TextMeshProUGUI movementCarry;
        private TextMeshProUGUI movementRevision;
        private TextMeshProUGUI movementTick;
        private TextMeshProUGUI movementPending;
        private TextMeshProUGUI movementReconciliations;
        private TextMeshProUGUI movementCorrection;
        private TextMeshProUGUI movementPath;
        private TextMeshProUGUI movementPlan;

        public bool IsOpen
        {
            get { return window != null && window.IsVisible; }
        }

        public TemporaryMenuWindow Window
        {
            get { return window; }
        }

        public CollisionDebugOverlay Overlay
        {
            get { return overlay; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(
            RealtimeSimulationClient realtimeClient,
            PlayerBodyTintSetting tintSetting,
            WorldDebugViewSettings debugViewSettings,
            Material debugLineMaterial)
        {
            simulationClient = realtimeClient;
            bodyTint = tintSetting;
            viewSettings = debugViewSettings;
            lineMaterial = debugLineMaterial;
        }

        public void SetOpen(bool open)
        {
            if (window == null)
            {
                return;
            }

            window.SetVisible(open);
            nextRefreshTime = 0f;
            UpdateOverlay();
        }

        private void Start()
        {
            FindLocalPlayerComponents();
            BuildUi();
            SetOpen(false);
            if (viewSettings != null)
            {
                viewSettings.Changed += OnViewSettingsChanged;
            }

            if (bodyTint != null)
            {
                bodyTint.Changed += OnViewSettingsChanged;
            }

            if (ClientSession.ActiveSimulationSession == null)
            {
#if UNITY_EDITOR
                SetStatus("No active simulation session. Local player is not spawned.", TemporaryUiKit.WarningColor);
                return;
#else
                ClientSceneLoader.LoadCharacterSelect();
                return;
#endif
            }

            if (simulationClient == null
                || !simulationClient.IsJoined)
            {
                ClientSession.ActiveSimulationSession = null;
                ClientSceneLoader.LoadCharacterSelect();
            }
        }

        private void OnDestroy()
        {
            if (viewSettings != null)
            {
                viewSettings.Changed -= OnViewSettingsChanged;
            }

            if (bodyTint != null)
            {
                bodyTint.Changed -= OnViewSettingsChanged;
            }

            window?.Destroy();
        }

        private void Update()
        {
            telemetry.RecordFrame(Time.unscaledDeltaTime);
            if (localPlayerInput == null || localPlayerController == null)
            {
                FindLocalPlayerComponents();
            }

            if (localPlayerInput != null && localPlayerInput.ToggleDebugMenuPressedThisFrame)
            {
                SetOpen(!IsOpen);
            }

            SampleNetwork();
            if (IsOpen)
            {
                TrackPointer();
                if (Time.unscaledTime >= nextRefreshTime)
                {
                    nextRefreshTime = Time.unscaledTime + RefreshSeconds;
                    Refresh();
                }
            }
        }

        private void BuildUi()
        {
            window = TemporaryMenuWindow.Create(
                transform,
                WindowName,
                "Debug",
                "F2",
                new Vector2(1f, 1f),
                new Vector2(-16f, -16f),
                new Vector2(470f, 700f),
                true,
                () => SetOpen(false));
            BuildOverviewTab(window.AddTab("Overview"));
            BuildPerformanceTab(window.AddTab("Performance"));
            BuildNetworkTab(window.AddTab("Network"));
            BuildMovementTab(window.AddTab("Movement"));
            window.AddFooterButton("Leave Shard", BeginLeave, TemporaryButtonTone.Danger);
            window.SetStatus(status, statusColor);

            overlay = new GameObject("CollisionDebugOverlay").AddComponent<CollisionDebugOverlay>();
            overlay.transform.SetParent(transform, false);
            overlay.Initialize(simulationClient, lineMaterial);
        }

        private void BuildOverviewTab(RectTransform page)
        {
            TemporaryMenuWindow.AddSection(page, "Position");
            playerPosition = TemporaryMenuWindow.AddValue(page, "Player");
            playerFacing = TemporaryMenuWindow.AddValue(page, "Facing");
            playerChunk = TemporaryMenuWindow.AddValue(page, "Collision chunk");
            pointerPosition = TemporaryMenuWindow.AddValue(page, "Ground at pointer");
            var copyRow = TemporaryMenuWindow.AddButtonRow(page);
            TemporaryUiKit.CreateButton(copyRow, "Copy player", CopyPlayerPosition, true, TemporaryButtonTone.Normal);
            TemporaryUiKit.CreateButton(copyRow, "Copy pointer", CopyPointerPosition, true, TemporaryButtonTone.Normal);

            TemporaryMenuWindow.AddSection(page, "At a glance");
            glanceFrame = TemporaryMenuWindow.AddValue(page, "Frame");
            glancePing = TemporaryMenuWindow.AddValue(page, "Ping");
            glanceSnapshots = TemporaryMenuWindow.AddValue(page, "Latest snapshot");
            glanceConnection = TemporaryMenuWindow.AddValue(page, "Connection");

            TemporaryMenuWindow.AddSection(page, "Show in the world");
            collisionToggle = TemporaryMenuWindow.AddToggle(
                page,
                "Collision around you",
                viewSettings != null && viewSettings.ShowCollision,
                value => SetView(value, null));
            TemporaryMenuWindow.AddNote(
                page,
                Colored("green", CollisionDebugOverlay.ColorOf(CollisionDebugKind.WalkableGround)) + " ground, "
                + Colored("yellow", CollisionDebugOverlay.ColorOf(CollisionDebugKind.SteepSurface)) + " too steep, "
                + Colored("red", CollisionDebugOverlay.ColorOf(CollisionDebugKind.Obstacle)) + " blocks movement and sight, "
                + Colored("orange", CollisionDebugOverlay.ColorOf(CollisionDebugKind.MovementOnly)) + " movement only, "
                + Colored("purple", CollisionDebugOverlay.ColorOf(CollisionDebugKind.CombatOnly)) + " sight only. "
                + "It shows " + (2f * CollisionDebugOverlay.Reach).ToString("0", CultureInfo.InvariantCulture)
                + " m around you.");
            collisionShapes = TemporaryMenuWindow.AddValue(page, "Shapes shown");
            pathToggle = TemporaryMenuWindow.AddToggle(
                page,
                "Path you follow, in " + Colored("cyan", CollisionDebugOverlay.PathLineColor),
                viewSettings != null && viewSettings.ShowPath,
                value => SetView(null, value));
            tintToggle = TemporaryMenuWindow.AddToggle(
                page,
                "Tint bodies: you purple, players orange, bots blue",
                bodyTint != null && bodyTint.Enabled,
                value =>
                {
                    if (bodyTint != null)
                    {
                        bodyTint.Enabled = value;
                    }
                });
        }

        private void BuildPerformanceTab(RectTransform page)
        {
            TemporaryMenuWindow.AddSection(page, "Client rendering");
            renderingFps = TemporaryMenuWindow.AddValue(page, "FPS / 60 FPS budget");
            renderingFrame = TemporaryMenuWindow.AddValue(page, "Frame avg / P95 / max");
            renderingResolution = TemporaryMenuWindow.AddValue(page, "Resolution / quality");
            renderingVsync = TemporaryMenuWindow.AddValue(page, "VSync / target FPS");
            renderingMemory = TemporaryMenuWindow.AddValue(page, "Unity allocated / reserved");
            renderingHeap = TemporaryMenuWindow.AddValue(page, "Managed heap used");
        }

        private void BuildNetworkTab(RectTransform page)
        {
            TemporaryMenuWindow.AddSection(page, "Connection");
            connectionState = TemporaryMenuWindow.AddValue(page, "State");
            connectionCharacter = TemporaryMenuWindow.AddValue(page, "Character");
            connectionWorld = TemporaryMenuWindow.AddValue(page, "Shard / World");
            connectionWorker = TemporaryMenuWindow.AddValue(page, "Worker / runtime");
            connectionEndpoint = TemporaryMenuWindow.AddValue(page, "UDP / protocol");
            connectionTick = TemporaryMenuWindow.AddValue(page, "Observed server tick");

            TemporaryMenuWindow.AddSection(page, "Observed realtime network");
            networkPing = TemporaryMenuWindow.AddValue(page, "Round trip time");
            networkSnapshotAge = TemporaryMenuWindow.AddValue(page, "Latest snapshot age");
            networkSnapshotRate = TemporaryMenuWindow.AddValue(page, "Snapshot frames");
            networkSnapshotPackets = TemporaryMenuWindow.AddValue(page, "Snapshot packets / frames");
            networkPackets = TemporaryMenuWindow.AddValue(page, "App packets in / out");
            networkPayload = TemporaryMenuWindow.AddValue(page, "Payload in / out");
            networkLoss = TemporaryMenuWindow.AddValue(page, "Estimated snapshot loss");
            networkTotals = TemporaryMenuWindow.AddValue(page, "Frames / packets total");
            networkEntities = TemporaryMenuWindow.AddValue(page, "Known / remote entities");
            networkWindow = TemporaryMenuWindow.AddValue(page, "Rate sample window");
        }

        private void BuildMovementTab(RectTransform page)
        {
            TemporaryMenuWindow.AddSection(page, "Client prediction");
            movementAuthority = TemporaryMenuWindow.AddValue(page, "Authority");
            movementRates = TemporaryMenuWindow.AddValue(page, "Tick / snapshot rate");
            movementSpeed = TemporaryMenuWindow.AddValue(page, "Walk speed");
            movementCarry = TemporaryMenuWindow.AddValue(page, "Carry weight / capacity");
            movementRevision = TemporaryMenuWindow.AddValue(page, "Item revision / movement");
            movementTick = TemporaryMenuWindow.AddValue(page, "Client tick / entity");
            movementPending = TemporaryMenuWindow.AddValue(page, "Pending predicted inputs");
            movementReconciliations = TemporaryMenuWindow.AddValue(page, "Reconciliations / hard snaps");
            movementCorrection = TemporaryMenuWindow.AddValue(page, "Latest position correction");
            movementPath = TemporaryMenuWindow.AddValue(page, "Path / waypoint / blocked");
            movementPlan = TemporaryMenuWindow.AddValue(page, "Latest plan");
        }

        private void SetView(bool? showCollision, bool? showPath)
        {
            if (viewSettings == null)
            {
                return;
            }

            if (showCollision.HasValue)
            {
                viewSettings.ShowCollision = showCollision.Value;
            }

            if (showPath.HasValue)
            {
                viewSettings.ShowPath = showPath.Value;
            }
        }

        private void OnViewSettingsChanged()
        {
            collisionToggle?.SetIsOnWithoutNotify(viewSettings != null && viewSettings.ShowCollision);
            pathToggle?.SetIsOnWithoutNotify(viewSettings != null && viewSettings.ShowPath);
            tintToggle?.SetIsOnWithoutNotify(bodyTint != null && bodyTint.Enabled);
            UpdateOverlay();
        }

        // The overlay draws only while the menu is open.
        private void UpdateOverlay()
        {
            if (overlay == null)
            {
                return;
            }

            overlay.ShowCollision = IsOpen && viewSettings != null && viewSettings.ShowCollision;
            overlay.ShowPath = IsOpen && viewSettings != null && viewSettings.ShowPath;
        }

        private void SampleNetwork()
        {
            var client = simulationClient;
            if (client == null)
            {
                telemetry.ResetNetwork();
                return;
            }

            telemetry.SampleNetwork(
                Time.realtimeSinceStartup,
                client.RealtimePacketsReceived,
                client.RealtimeBytesReceived,
                client.RealtimePacketsSent,
                client.RealtimeBytesSent,
                client.SnapshotPacketsReceived,
                client.SnapshotFramesReceived);
        }

        // The ground under the pointer, kept while the pointer is over the menu so it can be copied.
        private void TrackPointer()
        {
            if (localPlayerController != null
                && localPlayerController.TryGetPointerRay(out var ray)
                && localPlayerController.TryFindGroundPoint(ray, out var point))
            {
                pointerPoint = point;
                hasPointerPoint = true;
            }
        }

        private void Refresh()
        {
            RefreshPosition();
            RefreshGlance();
            RefreshRendering();
            RefreshConnection();
            RefreshNetwork();
            RefreshMovement();
            window.SetStatus(operationState.IsBusy ? "Working..." : status, statusColor);
        }

        private void RefreshPosition()
        {
            var session = simulationClient != null ? simulationClient.MovementSession : null;
            if (localPlayerController == null || !localPlayerController.IsServerAuthoritative || session == null)
            {
                Set(playerPosition, "No local player", WorldDebugHealth.Unavailable);
                Set(playerFacing, "-", WorldDebugHealth.Unavailable);
                Set(playerChunk, "-", WorldDebugHealth.Unavailable);
            }
            else
            {
                var state = localPlayerController.PredictedState;
                Set(playerPosition, Coordinates(state.PositionX, state.PositionY, state.PositionZ));
                Set(playerFacing, state.YawDegrees.ToString("0", CultureInfo.InvariantCulture) + " degrees");
                var chunkSize = session.CollisionWorld.ChunkSize;
                Set(
                    playerChunk,
                    Mathf.FloorToInt(state.PositionX / chunkSize) + ", " + Mathf.FloorToInt(state.PositionZ / chunkSize)
                    + " (" + chunkSize.ToString("0", CultureInfo.InvariantCulture) + " m chunks, "
                    + session.CollisionWorld.ChunkCount + " loaded)");
            }

            Set(
                pointerPosition,
                hasPointerPoint ? Coordinates(pointerPoint.x, pointerPoint.y, pointerPoint.z) : "Point at the ground",
                hasPointerPoint ? WorldDebugHealth.Neutral : WorldDebugHealth.Unavailable);
            Set(
                collisionShapes,
                overlay != null && overlay.ShowCollision ? overlay.ShownShapeCount.ToString(CultureInfo.InvariantCulture) : "Off",
                overlay != null && overlay.ShowCollision ? WorldDebugHealth.Neutral : WorldDebugHealth.Unavailable);
        }

        private void RefreshGlance()
        {
            Set(
                glanceFrame,
                WorldDebugDiagnostics.Rate(telemetry.FramesPerSecond, "FPS") + ", P95 "
                + WorldDebugDiagnostics.Milliseconds(telemetry.P95FrameMilliseconds),
                WorldDebugDiagnostics.EvaluateFrame(telemetry.P95FrameMilliseconds, TargetFrameBudgetMilliseconds));
            var client = simulationClient;
            if (client == null || client.MovementSession == null)
            {
                Set(glancePing, "-", WorldDebugHealth.Unavailable);
                Set(glanceSnapshots, "-", WorldDebugHealth.Unavailable);
                Set(glanceConnection, client != null ? client.State.ToString() : "No client", WorldDebugHealth.Unavailable);
                return;
            }

            Set(
                glancePing,
                client.RoundTripTimeMilliseconds + " ms",
                WorldDebugDiagnostics.EvaluatePing(client.RoundTripTimeMilliseconds));
            Set(
                glanceSnapshots,
                WorldDebugDiagnostics.Age(client.LatestSnapshotAgeSeconds) + " ago",
                WorldDebugDiagnostics.EvaluateSnapshotAge(client.LatestSnapshotAgeSeconds, client.MovementSession.SnapshotRateHz));
            Set(
                glanceConnection,
                client.State.ToString(),
                client.IsJoined ? WorldDebugHealth.Healthy : WorldDebugHealth.Warning);
        }

        private void RefreshRendering()
        {
            Set(
                renderingFps,
                WorldDebugDiagnostics.Rate(telemetry.FramesPerSecond, "FPS")
                + " / " + WorldDebugDiagnostics.Milliseconds(TargetFrameBudgetMilliseconds),
                WorldDebugDiagnostics.EvaluateFrame(telemetry.P95FrameMilliseconds, TargetFrameBudgetMilliseconds));
            Set(
                renderingFrame,
                WorldDebugDiagnostics.Milliseconds(telemetry.AverageFrameMilliseconds)
                + " / " + WorldDebugDiagnostics.Milliseconds(telemetry.P95FrameMilliseconds)
                + " / " + WorldDebugDiagnostics.Milliseconds(telemetry.MaximumFrameMilliseconds));
            Set(renderingResolution, Screen.width + "x" + Screen.height + " / " + CurrentQualityName());
            Set(renderingVsync, QualitySettings.vSyncCount + " / " + Application.targetFrameRate);
            Set(
                renderingMemory,
                WorldDebugDiagnostics.Megabytes(BytesToMebibytes(Profiler.GetTotalAllocatedMemoryLong()))
                + " / " + WorldDebugDiagnostics.Megabytes(BytesToMebibytes(Profiler.GetTotalReservedMemoryLong())));
            Set(renderingHeap, WorldDebugDiagnostics.Megabytes(BytesToMebibytes(Profiler.GetMonoUsedSizeLong())));
        }

        private void RefreshConnection()
        {
            var session = ClientSession.ActiveSimulationSession;
            var client = simulationClient;
            if (session == null || client == null)
            {
                Set(connectionState, "No local simulation session", WorldDebugHealth.Unavailable);
                Set(connectionCharacter, "-", WorldDebugHealth.Unavailable);
                Set(connectionWorld, "-", WorldDebugHealth.Unavailable);
                Set(connectionWorker, "-", WorldDebugHealth.Unavailable);
                Set(connectionEndpoint, "-", WorldDebugHealth.Unavailable);
                Set(connectionTick, "-", WorldDebugHealth.Unavailable);
                return;
            }

            Set(
                connectionState,
                client.State.ToString(),
                client.IsJoined ? WorldDebugHealth.Healthy : WorldDebugHealth.Warning);
            Set(connectionCharacter, session.characterName);
            Set(connectionWorld, session.shardId + " / " + session.worldId);
            Set(
                connectionWorker,
                session.workerId + " / " + WorldDebugDiagnostics.ShortIdentifier(session.workerRuntimeId));
            Set(
                connectionEndpoint,
                client.ConnectedHost + ":" + client.ConnectedPort + " / v" + RealtimeProtocol.Version);
            Set(connectionTick, client.LatestServerTick.ToString(CultureInfo.InvariantCulture));
        }

        private void RefreshNetwork()
        {
            var client = simulationClient;
            if (client == null || client.MovementSession == null)
            {
                foreach (var text in new[]
                {
                    networkPing, networkSnapshotAge, networkSnapshotRate, networkSnapshotPackets, networkPackets,
                    networkPayload, networkLoss, networkTotals, networkEntities, networkWindow
                })
                {
                    Set(text, "Unavailable", WorldDebugHealth.Unavailable);
                }

                return;
            }

            Set(
                networkPing,
                client.RoundTripTimeMilliseconds + " ms",
                WorldDebugDiagnostics.EvaluatePing(client.RoundTripTimeMilliseconds));
            Set(
                networkSnapshotAge,
                WorldDebugDiagnostics.Age(client.LatestSnapshotAgeSeconds),
                WorldDebugDiagnostics.EvaluateSnapshotAge(client.LatestSnapshotAgeSeconds, client.MovementSession.SnapshotRateHz));
            Set(
                networkSnapshotRate,
                WorldDebugDiagnostics.Rate(telemetry.SnapshotFramesPerSecond, "frames/s")
                + " of " + client.MovementSession.SnapshotRateHz + " configured");
            Set(
                networkSnapshotPackets,
                WorldDebugDiagnostics.Rate(telemetry.SnapshotPacketsPerSecond, "pkt/s")
                + " / " + WorldDebugDiagnostics.Rate(telemetry.SnapshotFramesPerSecond, "frames/s"));
            Set(
                networkPackets,
                WorldDebugDiagnostics.Rate(telemetry.ReceivedPacketsPerSecond, "pkt/s")
                + " / " + WorldDebugDiagnostics.Rate(telemetry.SentPacketsPerSecond, "pkt/s"));
            Set(
                networkPayload,
                WorldDebugDiagnostics.Rate(telemetry.ReceivedKibibytesPerSecond, "KiB/s")
                + " / " + WorldDebugDiagnostics.Rate(telemetry.SentKibibytesPerSecond, "KiB/s"));
            var estimatedLoss = WorldDebugDiagnostics.EstimateSnapshotLossPercent(
                client.SnapshotFramesReceived,
                client.EstimatedMissingSnapshotFrames);
            Set(
                networkLoss,
                WorldDebugDiagnostics.Percent(estimatedLoss) + " (" + client.EstimatedMissingSnapshotFrames + " missing)",
                WorldDebugDiagnostics.EvaluateLoss(estimatedLoss));
            Set(networkTotals, client.SnapshotFramesReceived + " / " + client.SnapshotPacketsReceived);
            var knownEntities = client.SpawnedEntityCount;
            Set(networkEntities, knownEntities + " / " + Math.Max(0, knownEntities - 1));
            Set(networkWindow, WorldDebugDiagnostics.Age(telemetry.NetworkSampleSeconds));
        }

        private void RefreshMovement()
        {
            var client = simulationClient;
            if (client == null || client.MovementSession == null || localPlayerController == null)
            {
                foreach (var text in new[]
                {
                    movementAuthority, movementRates, movementSpeed, movementCarry, movementRevision, movementTick,
                    movementPending, movementReconciliations, movementCorrection, movementPath, movementPlan
                })
                {
                    Set(text, "Unavailable", WorldDebugHealth.Unavailable);
                }

                return;
            }

            var session = client.MovementSession;
            Set(
                movementAuthority,
                localPlayerController.IsServerAuthoritative ? "Server authoritative with client prediction" : "Offline",
                localPlayerController.IsServerAuthoritative ? WorldDebugHealth.Healthy : WorldDebugHealth.Warning);
            Set(movementRates, session.Settings.TickRateHz + " Hz / " + session.SnapshotRateHz + " Hz");
            Set(
                movementSpeed,
                session.PredictionSettings.WalkSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s ("
                + session.WalkSpeedPercent + "% of the World's)",
                session.WalkSpeedPercent == 100 ? WorldDebugHealth.Neutral : WorldDebugHealth.Warning);
            var carryState = session.CarryState;
            Set(movementCarry, carryState.CarriedWeight + " / " + carryState.CarryCapacity);
            Set(
                movementRevision,
                carryState.ItemStateRevision + " / "
                + (carryState.MovementMultiplierBasisPoints / 100f).ToString("0.##", CultureInfo.InvariantCulture) + "%");
            Set(movementTick, localPlayerController.ClientSimulationTick + " / " + session.ControlledEntityId);
            Set(
                movementPending,
                localPlayerController.PendingPredictedInputCount.ToString(CultureInfo.InvariantCulture),
                WorldDebugDiagnostics.EvaluatePendingInputs(localPlayerController.PendingPredictedInputCount));
            Set(
                movementReconciliations,
                localPlayerController.ReconciliationCount + " / " + localPlayerController.HardReconciliationCount,
                localPlayerController.HardReconciliationCount == 0 ? WorldDebugHealth.Healthy : WorldDebugHealth.Warning);
            Set(
                movementCorrection,
                localPlayerController.LatestReconciliationDistance.ToString("0.000", CultureInfo.InvariantCulture) + " m");
            var path = localPlayerController.CurrentPath;
            var progress = localPlayerController.PredictedState.PathProgress;
            Set(
                movementPath,
                path == null
                    ? "Standing"
                    : path.Id + " / " + Math.Min(progress.NextWaypointIndex, path.Count)
                        + " of " + path.Count + " / " + progress.BlockedTicks);
            Set(
                movementPlan,
                localPlayerController.LatestPlanStatus + ", " + localPlayerController.LatestPlanExpandedNodes + " nodes, "
                + localPlayerController.LatestPlanMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms",
                localPlayerController.LatestPlanMilliseconds <= TargetFrameBudgetMilliseconds
                    ? WorldDebugHealth.Healthy
                    : WorldDebugHealth.Warning);
        }

        private void CopyPlayerPosition()
        {
            if (localPlayerController == null || !localPlayerController.IsServerAuthoritative)
            {
                SetStatus("No local player to copy the position of.", TemporaryUiKit.WarningColor);
                return;
            }

            var state = localPlayerController.PredictedState;
            Copy("player position", state.PositionX, state.PositionY, state.PositionZ);
        }

        private void CopyPointerPosition()
        {
            if (!hasPointerPoint)
            {
                SetStatus("Point at the ground first.", TemporaryUiKit.WarningColor);
                return;
            }

            Copy("ground at the pointer", pointerPoint.x, pointerPoint.y, pointerPoint.z);
        }

        private void Copy(string what, float x, float y, float z)
        {
            var text = Format(x) + ", " + Format(y) + ", " + Format(z);
            GUIUtility.systemCopyBuffer = text;
            SetStatus("Copied the " + what + ": " + text, TemporaryUiKit.SuccessColor);
        }

        private void SetStatus(string text, Color color)
        {
            status = text;
            statusColor = color;
            window?.SetStatus(text, color);
        }

        private static void Set(TextMeshProUGUI text, string value, WorldDebugHealth health = WorldDebugHealth.Neutral)
        {
            if (text == null)
            {
                return;
            }

            text.text = value ?? string.Empty;
            text.color = ColorFor(health);
        }

        private static Color ColorFor(WorldDebugHealth health)
        {
            switch (health)
            {
                case WorldDebugHealth.Healthy:
                    return new Color(0.45f, 1f, 0.55f);
                case WorldDebugHealth.Warning:
                    return new Color(1f, 0.78f, 0.25f);
                case WorldDebugHealth.Critical:
                    return new Color(1f, 0.35f, 0.3f);
                case WorldDebugHealth.Unavailable:
                    return new Color(0.55f, 0.58f, 0.62f);
                default:
                    return TemporaryUiKit.TextPrimary;
            }
        }

        private static string Coordinates(float x, float y, float z)
        {
            return "x " + Format(x) + "   y " + Format(y) + "   z " + Format(z);
        }

        private static string Format(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string Colored(string text, Color color)
        {
            return "<color=" + TemporaryUiKit.ColorTag(color) + ">" + text + "</color>";
        }

        private void FindLocalPlayerComponents()
        {
            localPlayerController = FindAnyObjectByType<LocalPlayerController>();
            localPlayerInput = localPlayerController != null
                ? localPlayerController.PlayerInput
                : FindAnyObjectByType<LocalPlayerInput>();
        }

        private static float BytesToMebibytes(long bytes)
        {
            return Mathf.Max(0f, bytes / 1024f / 1024f);
        }

        private static string CurrentQualityName()
        {
            var level = QualitySettings.GetQualityLevel();
            return level >= 0 && level < QualitySettings.names.Length
                ? QualitySettings.names[level]
                : "Unknown";
        }

        private void BeginLeave()
        {
            if (!operationState.TryBegin(ClientOperation.LeaveShard))
            {
                return;
            }

            StartCoroutine(RunLeaveOperation());
        }

        private IEnumerator RunLeaveOperation()
        {
            yield return LeaveShardRoutine();
            operationState.Complete(ClientOperation.LeaveShard);
        }

        private IEnumerator LeaveShardRoutine()
        {
            var session = ClientSession.ActiveSimulationSession;
            if (session == null || simulationClient == null)
            {
                ClientSession.ActiveSimulationSession = null;
                ClientSceneLoader.LoadCharacterSelect();
                yield break;
            }

            RealtimeClientError error = null;
            yield return simulationClient.Leave(
                session.simulationSessionId,
                () => { },
                value => error = value);

            if (error != null)
            {
                SetStatus("Error: " + error.ToDisplayMessage(), TemporaryUiKit.DangerColor);
                if (simulationClient.IsJoined)
                {
                    yield break;
                }
            }

            ClientSession.ActiveSimulationSession = null;
            SetStatus("Left shard.", TemporaryUiKit.TextSecondary);
            ClientSceneLoader.LoadCharacterSelect();
        }
    }
}
