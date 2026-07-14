using System;
using System.Collections;
using ShooterMmo.Gameplay;
using ShooterMmo.GameProtocol;
using ShooterMmo.Networking;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace ShooterMmo.Ui
{
    public sealed class WorldScenePanel : MonoBehaviour
    {
        private const float LabelWidth = 205f;
        private const float TargetFrameBudgetMilliseconds = 1000f / 60f;

        private readonly ClientOperationState operationState = new ClientOperationState();
        private readonly WorldDebugTelemetryTracker telemetry = new WorldDebugTelemetryTracker();

        private string status = "Realtime simulation connection active.";
        private Vector2 scrollPosition;
        private LocalPlayerInput localPlayerInput;
        private LocalPlayerController localPlayerController;
        private bool isVisible = true;

        private void Start()
        {
            FindLocalPlayerComponents();
            if (ShooterMmoClientSession.ActiveSimulationSession == null)
            {
#if UNITY_EDITOR
                status = "No active simulation session. Local player is not spawned.";
                return;
#else
                SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
                return;
#endif
            }

            if (ShooterMmoClientBootstrap.SimulationClient == null
                || !ShooterMmoClientBootstrap.SimulationClient.IsJoined)
            {
                ShooterMmoClientSession.ActiveSimulationSession = null;
                SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
            }
        }

        private void Update()
        {
            telemetry.RecordFrame(Time.unscaledDeltaTime);
            if (localPlayerInput == null || localPlayerController == null)
            {
                FindLocalPlayerComponents();
            }

            if (localPlayerInput != null && localPlayerInput.ToggleWorldDebugPressedThisFrame)
            {
                isVisible = !isVisible;
            }

            var client = ShooterMmoClientBootstrap.SimulationClient;
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

        private void OnGUI()
        {
            if (!isVisible)
            {
                return;
            }

            GUILayout.BeginArea(
                TemporaryPanelStyles.GetBottomLeftPanelRect(520f, 670f),
                "World Client Debug (F2)",
                GUI.skin.window);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);

            DrawConnectionSection();
            DrawRenderingSection();
            DrawPredictionSection();
            DrawNetworkSection();
            DrawStatusAndLeave();

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static void DrawSectionHeader(string text)
        {
            GUILayout.Space(5f);
            GUILayout.Label(text, GUI.skin.box, GUILayout.ExpandWidth(true));
        }

        private static void DrawMetric(
            string label,
            string value,
            WorldDebugHealth health = WorldDebugHealth.Neutral)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(LabelWidth));
            var previousColor = GUI.color;
            GUI.color = ColorFor(health);
            GUILayout.Label(value, GUILayout.ExpandWidth(true));
            GUI.color = previousColor;
            GUILayout.EndHorizontal();
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
                    return new Color(0.65f, 0.65f, 0.65f);
                default:
                    return Color.white;
            }
        }

        private void DrawConnectionSection()
        {
            DrawSectionHeader("Connection");
            var session = ShooterMmoClientSession.ActiveSimulationSession;
            var client = ShooterMmoClientBootstrap.SimulationClient;
            if (session == null || client == null)
            {
                DrawMetric("State", "No local simulation session", WorldDebugHealth.Unavailable);
                return;
            }

            DrawMetric(
                "State",
                client.State.ToString(),
                client.IsJoined ? WorldDebugHealth.Healthy : WorldDebugHealth.Warning);
            DrawMetric("Character", session.characterName);
            DrawMetric("Shard / World", session.shardId + " / " + session.worldId);
            DrawMetric(
                "Worker / Runtime",
                session.workerId + " / "
                + WorldDebugDiagnostics.ShortIdentifier(session.workerRuntimeId));
            DrawMetric(
                "UDP / Protocol",
                client.ConnectedHost + ":" + client.ConnectedPort
                + " / v" + RealtimeProtocol.Version);
            DrawMetric("Observed Server Tick", client.LatestServerTick.ToString());
        }

        private void DrawRenderingSection()
        {
            DrawSectionHeader("Client Rendering");
            DrawMetric(
                "FPS / 60 FPS Budget",
                WorldDebugDiagnostics.Rate(telemetry.FramesPerSecond, "FPS")
                + " / " + WorldDebugDiagnostics.Milliseconds(TargetFrameBudgetMilliseconds),
                WorldDebugDiagnostics.EvaluateFrame(
                    telemetry.P95FrameMilliseconds,
                    TargetFrameBudgetMilliseconds));
            DrawMetric(
                "Frame Avg / P95 / Max",
                WorldDebugDiagnostics.Milliseconds(telemetry.AverageFrameMilliseconds)
                + " / " + WorldDebugDiagnostics.Milliseconds(telemetry.P95FrameMilliseconds)
                + " / " + WorldDebugDiagnostics.Milliseconds(telemetry.MaximumFrameMilliseconds));
            DrawMetric(
                "Resolution / Quality",
                Screen.width + "x" + Screen.height + " / " + CurrentQualityName());
            DrawMetric(
                "VSync / Target FPS",
                QualitySettings.vSyncCount + " / " + Application.targetFrameRate);
            DrawMetric(
                "Unity Allocated / Reserved",
                WorldDebugDiagnostics.Megabytes(BytesToMebibytes(Profiler.GetTotalAllocatedMemoryLong()))
                + " / "
                + WorldDebugDiagnostics.Megabytes(BytesToMebibytes(Profiler.GetTotalReservedMemoryLong())));
            DrawMetric(
                "Managed Heap Used",
                WorldDebugDiagnostics.Megabytes(BytesToMebibytes(Profiler.GetMonoUsedSizeLong())));
        }

        private void DrawPredictionSection()
        {
            DrawSectionHeader("Client Prediction");
            var client = ShooterMmoClientBootstrap.SimulationClient;
            if (client == null
                || client.MovementSession == null
                || localPlayerController == null)
            {
                DrawMetric("Prediction", "Unavailable", WorldDebugHealth.Unavailable);
                return;
            }

            DrawMetric(
                "Authority",
                localPlayerController.IsServerAuthoritative
                    ? "Server authoritative with client prediction"
                    : "Offline",
                localPlayerController.IsServerAuthoritative
                    ? WorldDebugHealth.Healthy
                    : WorldDebugHealth.Warning);
            DrawMetric(
                "Tick / Snapshot Configuration",
                client.MovementSession.Settings.TickRateHz + " Hz / "
                + client.MovementSession.SnapshotRateHz + " Hz");
            DrawMetric(
                "Client Tick / Entity",
                localPlayerController.ClientSimulationTick + " / "
                + client.MovementSession.ControlledEntityId);
            DrawMetric(
                "Pending Predicted Inputs",
                localPlayerController.PendingPredictedInputCount.ToString(),
                WorldDebugDiagnostics.EvaluatePendingInputs(
                    localPlayerController.PendingPredictedInputCount));
            DrawMetric(
                "Reconciliations / Hard Snaps",
                localPlayerController.ReconciliationCount + " / "
                + localPlayerController.HardReconciliationCount,
                localPlayerController.HardReconciliationCount == 0
                    ? WorldDebugHealth.Healthy
                    : WorldDebugHealth.Warning);
            DrawMetric(
                "Latest Position Correction",
                localPlayerController.LatestReconciliationDistance.ToString("0.000") + " m");
        }

        private void DrawNetworkSection()
        {
            DrawSectionHeader("Observed Realtime Network");
            var client = ShooterMmoClientBootstrap.SimulationClient;
            if (client == null || client.MovementSession == null)
            {
                DrawMetric("Network", "Unavailable", WorldDebugHealth.Unavailable);
                return;
            }

            DrawMetric(
                "Round Trip Time",
                client.RoundTripTimeMilliseconds + " ms",
                WorldDebugDiagnostics.EvaluatePing(client.RoundTripTimeMilliseconds));
            DrawMetric(
                "Latest Snapshot Age",
                WorldDebugDiagnostics.Age(client.LatestSnapshotAgeSeconds),
                WorldDebugDiagnostics.EvaluateSnapshotAge(
                    client.LatestSnapshotAgeSeconds,
                    client.MovementSession.SnapshotRateHz));
            DrawMetric(
                "Observed Snapshot Frames",
                WorldDebugDiagnostics.Rate(telemetry.SnapshotFramesPerSecond, "frames/s")
                + " of " + client.MovementSession.SnapshotRateHz + " configured");
            DrawMetric(
                "Snapshot Packets / Frames",
                WorldDebugDiagnostics.Rate(telemetry.SnapshotPacketsPerSecond, "pkt/s")
                + " / "
                + WorldDebugDiagnostics.Rate(telemetry.SnapshotFramesPerSecond, "frames/s"));
            DrawMetric(
                "App Packets In / Out",
                WorldDebugDiagnostics.Rate(telemetry.ReceivedPacketsPerSecond, "pkt/s")
                + " / " + WorldDebugDiagnostics.Rate(telemetry.SentPacketsPerSecond, "pkt/s"));
            DrawMetric(
                "Payload In / Out",
                WorldDebugDiagnostics.Rate(telemetry.ReceivedKibibytesPerSecond, "KiB/s")
                + " / " + WorldDebugDiagnostics.Rate(telemetry.SentKibibytesPerSecond, "KiB/s"));
            var estimatedLoss = WorldDebugDiagnostics.EstimateSnapshotLossPercent(
                client.SnapshotFramesReceived,
                client.EstimatedMissingSnapshotFrames);
            DrawMetric(
                "Estimated Snapshot Loss",
                WorldDebugDiagnostics.Percent(estimatedLoss)
                + " (" + client.EstimatedMissingSnapshotFrames + " missing)",
                WorldDebugDiagnostics.EvaluateLoss(estimatedLoss));
            DrawMetric(
                "Snapshot Frames / Packets Total",
                client.SnapshotFramesReceived + " / " + client.SnapshotPacketsReceived);
            var knownEntities = client.SpawnedEntityCount;
            DrawMetric(
                "Known / Remote Entities",
                knownEntities + " / " + Math.Max(0, knownEntities - 1));
            DrawMetric(
                "Rate Sample Window",
                WorldDebugDiagnostics.Age(telemetry.NetworkSampleSeconds));
        }

        private void DrawStatusAndLeave()
        {
            GUILayout.Space(8f);
            GUILayout.Label(operationState.IsBusy ? "Working..." : status);
            var previousGuiState = GUI.enabled;
            GUI.enabled = previousGuiState && !operationState.IsBusy;
            if (GUILayout.Button("Leave Shard", GUILayout.Height(28f)))
            {
                BeginLeave();
            }

            GUI.enabled = previousGuiState;
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
            var session = ShooterMmoClientSession.ActiveSimulationSession;
            var simulationClient = ShooterMmoClientBootstrap.SimulationClient;
            if (session == null || simulationClient == null)
            {
                ShooterMmoClientSession.ActiveSimulationSession = null;
                SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
                yield break;
            }

            RealtimeClientError error = null;
            yield return simulationClient.Leave(
                session.simulationSessionId,
                () => { },
                value => error = value);

            if (error != null)
            {
                SetError(error);
                if (simulationClient.IsJoined)
                {
                    yield break;
                }
            }

            ShooterMmoClientSession.ActiveSimulationSession = null;
            status = "Left shard.";
            SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
        }

        private void SetError(RealtimeClientError error)
        {
            status = "Error: " + error.ToDisplayMessage();
        }
    }
}
