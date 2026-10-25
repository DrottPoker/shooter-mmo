using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Worlds;
using Mmorpg.GameProtocol;
using UnityEngine;
using UnityEngine.Profiling;

namespace Mmorpg.Client.Ui
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
        private RealtimeSimulationClient simulationClient;
        private WorldPointerFocus pointerFocus;
        private PlayerBodyTintSetting bodyTint;
        private PlayerBodyLookSetting bodyLook;

        // Called by the composition root before the component is activated.
        public void Initialize(
            RealtimeSimulationClient realtimeClient,
            WorldPointerFocus focus,
            PlayerBodyTintSetting tintSetting,
            PlayerBodyLookSetting lookSetting)
        {
            simulationClient = realtimeClient;
            pointerFocus = focus;
            bodyTint = tintSetting;
            bodyLook = lookSetting;
        }

        private void Start()
        {
            FindLocalPlayerComponents();
            if (ClientSession.ActiveSimulationSession == null)
            {
#if UNITY_EDITOR
                status = "No active simulation session. Local player is not spawned.";
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

        private void OnGUI()
        {
            if (!isVisible)
            {
                return;
            }

            var panelRect = TemporaryPanelStyles.GetBottomLeftPanelRect(520f, 670f);
            pointerFocus?.ReportInterfaceRect(panelRect);
            GUILayout.BeginArea(
                panelRect,
                "World Client Debug (F2)",
                GUI.skin.window);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);

            DrawConnectionSection();
            DrawRenderingSection();
            DrawPredictionSection();
            DrawNetworkSection();
            DrawPresentationSection();
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
            var session = ClientSession.ActiveSimulationSession;
            var client = simulationClient;
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
            var client = simulationClient;
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
            var carryState = client.MovementSession.CarryState;
            DrawMetric(
                "Carry Weight / Capacity",
                carryState.CarriedWeight + " / " + carryState.CarryCapacity);
            DrawMetric(
                "Item Revision / Movement",
                carryState.ItemStateRevision + " / "
                + (carryState.MovementMultiplierBasisPoints / 100f).ToString("0.##")
                + "%");
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
            var path = localPlayerController.CurrentPath;
            var progress = localPlayerController.PredictedState.PathProgress;
            DrawMetric(
                "Path / Waypoint / Blocked",
                path == null
                    ? "Standing"
                    : path.Id + " / " + Math.Min(progress.NextWaypointIndex, path.Count)
                        + " of " + path.Count + " / " + progress.BlockedTicks);
            DrawMetric(
                "Latest Plan",
                localPlayerController.LatestPlanStatus + ", "
                + localPlayerController.LatestPlanExpandedNodes + " nodes, "
                + localPlayerController.LatestPlanMilliseconds.ToString("0.0") + " ms",
                localPlayerController.LatestPlanMilliseconds <= TargetFrameBudgetMilliseconds
                    ? WorldDebugHealth.Healthy
                    : WorldDebugHealth.Warning);
        }

        private void DrawNetworkSection()
        {
            DrawSectionHeader("Observed Realtime Network");
            var client = simulationClient;
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

        private void DrawPresentationSection()
        {
            DrawSectionHeader("Presentation");
            if (bodyTint == null)
            {
                DrawMetric("Body Tint", "Unavailable", WorldDebugHealth.Unavailable);
                return;
            }

            bodyTint.Enabled = GUILayout.Toggle(
                bodyTint.Enabled,
                " Tint bodies: you purple, players orange, bots blue");
            DrawBodyModel();
            DrawBodyLook();
        }

        // A row that steps through the body models of the World scene, such as Human_Male and
        // Human_Female, with the rule of when a player chooses one.
        private void DrawBodyModel()
        {
            var models = bodyLook != null ? bodyLook.Models : null;
            if (models == null || models.Models.Count < 2)
            {
                return;
            }

            var shown = models.NamedOrDefault(bodyLook.Model);
            var current = 0;
            for (var index = 0; index < models.Models.Count; index++)
            {
                if (models.Models[index] == shown)
                {
                    current = index;
                }
            }

            var step = StepButtons("Your body: " + (shown != null ? shown.Name : "none") + " (" + RuleText(AppearanceRules.BodyModel) + ")");
            if (step != 0)
            {
                var chosen = models.Models[(current + step + models.Models.Count) % models.Models.Count];
                if (chosen != null)
                {
                    bodyLook.Model = chosen.Name;
                }
            }
        }

        // One row per appearance slot of the local body, such as Face and Eye, and per category of
        // its paint layers, such as its underwear and a tattoo, that steps through the options,
        // with the rule of when a player chooses it.
        private void DrawBodyLook()
        {
            var catalog = bodyLook != null ? bodyLook.Catalog : null;
            if (catalog == null)
            {
                return;
            }

            foreach (var slot in catalog.Slots)
            {
                if (slot == null || slot.Options.Count < 2)
                {
                    continue;
                }

                var options = slot.Options;
                var current = 0;
                for (var index = 0; index < options.Count; index++)
                {
                    if (string.Equals(options[index].Name, bodyLook.Look.OptionFor(slot.Name), StringComparison.Ordinal))
                    {
                        current = index;
                    }
                }

                var category = AppearanceRules.OfSlot(slot.Name);
                var rule = category.HasValue ? " (" + RuleText(AppearanceRules.For(category.Value)) + ")" : string.Empty;
                var step = StepButtons("Your " + slot.Name.ToLowerInvariant() + ": " + options[current].Name + rule);
                if (step != 0)
                {
                    bodyLook.Look = bodyLook.Look.With(slot.Name, options[(current + step + options.Count) % options.Count].Name);
                }
            }

            // Paint layers show on the body without being equipped: one row per category and slot,
            // with no paint among the choices of a category that a body may go without.
            var shown = new List<BodyAppearanceCatalog.Paint>();
            catalog.ShownPaints(bodyLook.Look, null, shown);
            var groups = catalog.Paints.Where(paint => paint != null)
                .GroupBy(paint => (paint.Category, paint.Slot))
                .ToArray();
            foreach (var group in groups)
            {
                var rule = AppearanceRules.For(group.Key.Category);
                var choices = new List<BodyAppearanceCatalog.Paint>();
                if (!rule.Required)
                {
                    choices.Add(null);
                }

                choices.AddRange(group);
                var current = Math.Max(choices.FindIndex(choice => choice != null && shown.Contains(choice)), 0);
                var name = group.Key.Category.ToString().ToLowerInvariant() + (group.Key.Slot.Length > 0 ? " (" + group.Key.Slot + ")" : string.Empty);
                var step = StepButtons("Your " + name + ": " + (choices[current] != null ? choices[current].Name : "none") + " (" + RuleText(rule) + ")");
                if (step != 0 && choices.Count > 1)
                {
                    var look = bodyLook.Look;
                    foreach (var paint in group)
                    {
                        look = look.WithPaint(paint.Name, false);
                    }

                    var chosen = choices[(current + step + choices.Count) % choices.Count];
                    bodyLook.Look = chosen != null ? look.WithPaint(chosen.Name, true) : look;
                }
            }

            // The hair is part of the look, a garment of the hair slot or none; every other garment
            // comes from the equipped items.
            var hairs = new List<BodyAppearanceCatalog.Garment> { null };
            hairs.AddRange(catalog.Garments.Where(garment => garment != null && garment.Slot == BodyAppearanceCatalog.Garment.HairSlot));
            if (hairs.Count > 1)
            {
                var current = Math.Max(hairs.FindIndex(hair => hair != null && bodyLook.Look.HasGarment(hair.Name)), 0);
                var step = StepButtons("Your hair: " + (hairs[current] != null ? hairs[current].Name : "none") + " (" + RuleText(AppearanceRules.Hair) + ")");
                if (step != 0)
                {
                    var look = bodyLook.Look.WithoutGarments();
                    var chosen = hairs[(current + step + hairs.Count) % hairs.Count];
                    bodyLook.Look = chosen != null ? look.WithGarment(chosen.Name, true) : look;
                }
            }
        }

        // When a player chooses a kind of look; until character creation it only shows in F2.
        private static string RuleText(AppearanceRules.Rule rule)
        {
            return rule.ChangesInGame ? "creation, in game" : "creation only";
        }

        // A row with the label and buttons that step back and forth: -1, 1, or 0 when neither
        // was pressed.
        private static int StepButtons(string label)
        {
            var step = 0;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(300f));
            if (GUILayout.Button("<", GUILayout.Width(28f)))
            {
                step = -1;
            }

            if (GUILayout.Button(">", GUILayout.Width(28f)))
            {
                step = 1;
            }

            GUILayout.EndHorizontal();
            return step;
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
                SetError(error);
                if (simulationClient.IsJoined)
                {
                    yield break;
                }
            }

            ClientSession.ActiveSimulationSession = null;
            status = "Left shard.";
            ClientSceneLoader.LoadCharacterSelect();
        }

        private void SetError(RealtimeClientError error)
        {
            status = "Error: " + error.ToDisplayMessage();
        }
    }
}
