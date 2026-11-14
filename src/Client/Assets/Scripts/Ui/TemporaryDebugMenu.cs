using System;
using System.Collections;
using System.Globalization;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Worlds;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.WorldData.Text;
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
    // second, the pace the frame telemetry changes at. Every frame, network, and correction number
    // comes from the client measurements, which also record to a file on request. It also runs
    // the World scene's session check and Leave Shard, through the World exit.
    public sealed class TemporaryDebugMenu : MonoBehaviour
    {
        public const string WindowName = "DebugMenu";

        private const float RefreshSeconds = 0.25f;
        private const float TargetFrameBudgetMilliseconds = 1000f / 60f;

        private readonly ClientOperationState operationState = new ClientOperationState();
        private readonly CollisionQueryBuffer waterBuffer = new CollisionQueryBuffer();
        private IClientSession clientSession;
        private WorldExit worldExit;
        private IRealtimeSession realtimeSession;
        private ClientMeasurements measurements;
        private IRealtimeMovementChannel realtimeMovement;
        private IRealtimePlayerChannel realtimePlayers;
        private PlayerBodyTintSetting bodyTint;
        private WorldDebugViewSettings viewSettings;
        private Material lineMaterial;
        private TextCatalog texts = TextCatalog.Empty(TextTableFormat.SourceLocale);
        private ClientContentReport content;
        private LocalPlayerSource localPlayers;
        // The local player and its input of this frame.
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
        private TextMeshProUGUI playerWater;
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
        private Toggle recordToggle;
        private TextMeshProUGUI recordFile;
        private TextMeshProUGUI recordCorrections;
        private TextMeshProUGUI recordJumps;
        private TextMeshProUGUI recordRemote;
        private TextMeshProUGUI recordIntents;

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

        // Called by the composition root before the component is activated, with every realtime
        // part or none.
        public void Initialize(
            IClientSession accountSession,
            WorldExit exit,
            IRealtimeSession session,
            ClientMeasurements clientMeasurements,
            IRealtimeMovementChannel movement,
            IRealtimePlayerChannel players,
            LocalPlayerSource localPlayerSource,
            PlayerBodyTintSetting tintSetting,
            WorldDebugViewSettings debugViewSettings,
            Material debugLineMaterial,
            TextCatalog textCatalog,
            ClientContentReport contentReport)
        {
            clientSession = accountSession;
            worldExit = exit;
            realtimeSession = session;
            measurements = clientMeasurements;
            realtimeMovement = movement;
            realtimePlayers = players;
            localPlayers = localPlayerSource;
            bodyTint = tintSetting;
            viewSettings = debugViewSettings;
            lineMaterial = debugLineMaterial;
            texts = textCatalog ?? TextCatalog.Empty(TextTableFormat.SourceLocale);
            content = contentReport;
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
            ReadLocalPlayer();
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

            if (clientSession == null || clientSession.ActiveSimulationSession == null)
            {
#if UNITY_EDITOR
                SetStatus("No active simulation session. Local player is not spawned.", TemporaryUiKit.WarningColor);
                return;
#else
                ClientSceneLoader.LoadCharacterSelect();
                return;
#endif
            }

            if (realtimeSession == null
                || !realtimeSession.IsJoined)
            {
                worldExit.LeaveUnjoinedWorld();
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
            ReadLocalPlayer();

            if (localPlayerInput != null && localPlayerInput.ToggleDebugMenuPressedThisFrame)
            {
                SetOpen(!IsOpen);
            }

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
            overlay.Initialize(realtimeMovement, localPlayers, lineMaterial);
        }

        private void BuildOverviewTab(RectTransform page)
        {
            TemporaryMenuWindow.AddSection(page, "Position");
            playerPosition = TemporaryMenuWindow.AddValue(page, "Player");
            playerFacing = TemporaryMenuWindow.AddValue(page, "Facing");
            playerChunk = TemporaryMenuWindow.AddValue(page, "Collision chunk");
            playerWater = TemporaryMenuWindow.AddValue(page, "Water depth");
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
                + Colored("purple", CollisionDebugOverlay.ColorOf(CollisionDebugKind.CombatOnly)) + " sight only, "
                + Colored("blue", CollisionDebugOverlay.ColorOf(CollisionDebugKind.Water)) + " water surface, "
                + Colored("pink", CollisionDebugOverlay.ColorOf(CollisionDebugKind.DeepWaterEdge)) + " deep water edge. "
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

            BuildMeasurementSection(page);
            BuildContentSection(page);
        }

        // A recording of the client measurements to a file, a switch of development builds and the
        // Editor only, with what the running recording measured so far.
        private void BuildMeasurementSection(RectTransform page)
        {
            TemporaryMenuWindow.AddSection(page, "Measurements");
            if (!Debug.isDebugBuild || measurements == null)
            {
                TemporaryMenuWindow.AddNote(page, "Recording is available in the Editor and development builds.");
                return;
            }

            recordToggle = TemporaryMenuWindow.AddToggle(
                page,
                "Record measurements to a file",
                measurements.IsRecording,
                SetRecording);
            recordFile = TemporaryMenuWindow.AddValue(page, "File");
            recordCorrections = TemporaryMenuWindow.AddValue(page, "Corrections p95 / hard snaps");
            recordJumps = TemporaryMenuWindow.AddValue(page, "Largest jump / jumps");
            recordRemote = TemporaryMenuWindow.AddValue(page, "Remote starved / longest");
            recordIntents = TemporaryMenuWindow.AddValue(page, "Intents answered / p95");
        }

        private void SetRecording(bool record)
        {
            if (measurements == null)
            {
                return;
            }

            if (record)
            {
                var path = measurements.StartRecording("debug-menu");
                SetStatus("Recording measurements to " + path, TemporaryUiKit.SuccessColor);
            }
            else
            {
                measurements.StopRecording();
                SetStatus("Wrote measurements to " + measurements.RecordingPath, TemporaryUiKit.SuccessColor);
            }
        }

        // The content files this client resolved at start: revision prefix and where each came from.
        private void BuildContentSection(RectTransform page)
        {
            TemporaryMenuWindow.AddSection(page, "Content");
            if (content == null)
            {
                TemporaryMenuWindow.AddNote(page, "The content of this client is unknown.");
                return;
            }

            foreach (var file in content.Files)
            {
                var name = file.Kind + " " + (file.Locale.Length > 0 ? file.Locale + "/" + file.Id : file.Id);
                var revision = file.Revision.Length >= 8 ? file.Revision.Substring(0, 8) : "unknown";
                TemporaryMenuWindow.AddValue(page, name).text = revision + "  " + SourceName(file.Source);
            }

            TemporaryMenuWindow.AddValue(page, "Fell back to bundled").text = content.FellBackToBundled
                ? "Yes: " + content.FallbackCode + ". " + content.FallbackDiagnostic
                : "No";
        }

        private static string SourceName(ClientContentSource source)
        {
            switch (source)
            {
                case ClientContentSource.Cache:
                    return "cache";
                case ClientContentSource.Downloaded:
                    return "downloaded";
                default:
                    return "bundled";
            }
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
            RefreshMeasurements();
            RefreshMovement();
            window.SetStatus(operationState.IsBusy ? "Working..." : status, statusColor);
        }

        private void RefreshPosition()
        {
            var session = realtimeMovement != null ? realtimeMovement.MovementSession : null;
            if (localPlayerController == null || !localPlayerController.IsServerAuthoritative || session == null)
            {
                Set(playerPosition, "No local player", WorldDebugHealth.Unavailable);
                Set(playerFacing, "-", WorldDebugHealth.Unavailable);
                Set(playerChunk, "-", WorldDebugHealth.Unavailable);
                Set(playerWater, "-", WorldDebugHealth.Unavailable);
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
                if (WaterRules.TryGetDepth(session.CollisionWorld, state.Position, waterBuffer, out var depth))
                {
                    Set(
                        playerWater,
                        depth.ToString("0.00", CultureInfo.InvariantCulture) + " m, "
                        + (depth >= WaterRules.WadeDepth
                            ? "wading at " + (WaterRules.WadeSpeedMultiplier * 100f).ToString("0", CultureInfo.InvariantCulture) + " % speed"
                            : "full speed"));
                }
                else
                {
                    Set(playerWater, "Not in water", WorldDebugHealth.Unavailable);
                }
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
            if (measurements == null)
            {
                Set(glanceFrame, "-", WorldDebugHealth.Unavailable);
                Set(glancePing, "-", WorldDebugHealth.Unavailable);
                Set(glanceSnapshots, "-", WorldDebugHealth.Unavailable);
                Set(glanceConnection, "No measurements", WorldDebugHealth.Unavailable);
                return;
            }

            var telemetry = measurements.Telemetry;
            Set(
                glanceFrame,
                DebugMenuReadout.FrameGlance(measurements),
                WorldDebugDiagnostics.EvaluateFrame(telemetry.P95FrameMilliseconds, TargetFrameBudgetMilliseconds));
            var movementSession = realtimeMovement != null ? realtimeMovement.MovementSession : null;
            if (realtimeSession == null || movementSession == null)
            {
                Set(glancePing, "-", WorldDebugHealth.Unavailable);
                Set(glanceSnapshots, "-", WorldDebugHealth.Unavailable);
                Set(
                    glanceConnection,
                    realtimeSession != null ? realtimeSession.State.ToString() : "No client",
                    WorldDebugHealth.Unavailable);
                return;
            }

            Set(
                glancePing,
                DebugMenuReadout.RoundTrip(measurements),
                WorldDebugDiagnostics.EvaluatePing(measurements.RoundTripMilliseconds));
            Set(
                glanceSnapshots,
                DebugMenuReadout.SnapshotAge(measurements) + " ago",
                WorldDebugDiagnostics.EvaluateSnapshotAge(
                    measurements.LatestSnapshotAgeSeconds,
                    movementSession.SnapshotRateHz));
            Set(
                glanceConnection,
                realtimeSession.State.ToString(),
                realtimeSession.IsJoined ? WorldDebugHealth.Healthy : WorldDebugHealth.Warning);
        }

        private void RefreshRendering()
        {
            if (measurements == null)
            {
                return;
            }

            var telemetry = measurements.Telemetry;
            Set(
                renderingFps,
                WorldDebugDiagnostics.Rate(telemetry.FramesPerSecond, "FPS")
                + " / " + WorldDebugDiagnostics.Milliseconds(TargetFrameBudgetMilliseconds),
                WorldDebugDiagnostics.EvaluateFrame(telemetry.P95FrameMilliseconds, TargetFrameBudgetMilliseconds));
            Set(renderingFrame, DebugMenuReadout.FrameTimes(measurements));
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
            var session = clientSession != null ? clientSession.ActiveSimulationSession : null;
            if (session == null || realtimeSession == null || measurements == null)
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
                realtimeSession.State.ToString(),
                realtimeSession.IsJoined ? WorldDebugHealth.Healthy : WorldDebugHealth.Warning);
            Set(connectionCharacter, session.characterName);
            Set(connectionWorld, session.shardId + " / " + session.worldId);
            Set(
                connectionWorker,
                session.workerId + " / " + WorldDebugDiagnostics.ShortIdentifier(session.workerRuntimeId));
            Set(
                connectionEndpoint,
                measurements.ConnectedHost + ":" + measurements.ConnectedPort
                + " / v" + RealtimeProtocol.Version);
            Set(connectionTick, measurements.LatestServerTick.ToString(CultureInfo.InvariantCulture));
        }

        private void RefreshNetwork()
        {
            var movementSession = realtimeMovement != null ? realtimeMovement.MovementSession : null;
            if (measurements == null || movementSession == null)
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

            var telemetry = measurements.Telemetry;
            Set(
                networkPing,
                DebugMenuReadout.RoundTrip(measurements),
                WorldDebugDiagnostics.EvaluatePing(measurements.RoundTripMilliseconds));
            Set(
                networkSnapshotAge,
                DebugMenuReadout.SnapshotAge(measurements),
                WorldDebugDiagnostics.EvaluateSnapshotAge(
                    measurements.LatestSnapshotAgeSeconds,
                    movementSession.SnapshotRateHz));
            Set(
                networkSnapshotRate,
                WorldDebugDiagnostics.Rate(telemetry.SnapshotFramesPerSecond, "frames/s")
                + " of " + movementSession.SnapshotRateHz + " configured");
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
            Set(
                networkLoss,
                DebugMenuReadout.SnapshotLoss(measurements),
                WorldDebugDiagnostics.EvaluateLoss(measurements.EstimatedSnapshotLossPercent));
            Set(
                networkTotals,
                measurements.SnapshotFramesReceived + " / " + measurements.SnapshotPacketsReceived);
            var knownEntities = realtimePlayers.SpawnedEntityCount;
            Set(networkEntities, knownEntities + " / " + Math.Max(0, knownEntities - 1));
            Set(networkWindow, WorldDebugDiagnostics.Age(telemetry.NetworkSampleSeconds));
        }

        private void RefreshMovement()
        {
            var session = realtimeMovement != null ? realtimeMovement.MovementSession : null;
            if (session == null || localPlayerController == null)
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
            if (measurements != null)
            {
                Set(
                    movementReconciliations,
                    DebugMenuReadout.Reconciliations(measurements),
                    measurements.HardSnaps == 0 ? WorldDebugHealth.Healthy : WorldDebugHealth.Warning);
                Set(movementCorrection, DebugMenuReadout.LatestCorrection(measurements));
            }
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

        private void RefreshMeasurements()
        {
            if (recordToggle == null || measurements == null)
            {
                return;
            }

            recordToggle.SetIsOnWithoutNotify(measurements.IsRecording);
            var summary = measurements.LatestSummary;
            Set(
                recordFile,
                measurements.RecordingPath.Length > 0 ? measurements.RecordingPath : "Not recorded",
                measurements.IsRecording ? WorldDebugHealth.Healthy : WorldDebugHealth.Unavailable);
            if (summary == null)
            {
                foreach (var text in new[] { recordCorrections, recordJumps, recordRemote, recordIntents })
                {
                    Set(text, "-", WorldDebugHealth.Unavailable);
                }

                return;
            }

            Set(recordCorrections, DebugMenuReadout.RecordedCorrections(summary));
            Set(recordJumps, DebugMenuReadout.RecordedJumps(summary));
            Set(recordRemote, DebugMenuReadout.RecordedRemote(summary));
            Set(recordIntents, DebugMenuReadout.RecordedIntents(summary));
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

        private void ReadLocalPlayer()
        {
            localPlayerController = localPlayers != null ? localPlayers.Controller : null;
            localPlayerInput = localPlayers != null ? localPlayers.Input : null;
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
            if (worldExit == null)
            {
                ClientSceneLoader.LoadCharacterSelect();
                yield break;
            }

            yield return worldExit.Leave(
                error => SetStatus("Error: " + AccountEntryPresentation.ErrorText(error, texts), TemporaryUiKit.DangerColor),
                () => SetStatus("Left shard.", TemporaryUiKit.TextSecondary));
        }
    }
}
