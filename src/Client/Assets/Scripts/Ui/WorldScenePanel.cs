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
    // The World debug panel (F2). IMGUI lays out and draws every control on every GUI event, so
    // the metrics are text built RefreshSeconds apart, the pace the frame telemetry changes at,
    // and drawn row by row without layout groups, and the look rows are built when the look
    // changes: built and laid out on every event, the open panel cost about 3 ms and 95 KB of
    // garbage a frame in a crowd.
    public sealed class WorldScenePanel : MonoBehaviour
    {
        private const float LabelWidth = 205f;
        private const float TargetFrameBudgetMilliseconds = 1000f / 60f;
        private const float RefreshSeconds = 0.25f;
        private const float PanelWidth = 520f;
        // The window's padding and the scroll bar beside the rows.
        private const float RowInset = 40f;

        private static readonly GUILayoutOption[] ExpandWidth = { GUILayout.ExpandWidth(true) };
        private static readonly GUILayoutOption[] StepLabelWidth = { GUILayout.Width(300f) };
        private static readonly GUILayoutOption[] StepButtonWidth = { GUILayout.Width(28f) };
        private static readonly GUILayoutOption[] LeaveButtonHeight = { GUILayout.Height(28f) };

        private readonly ClientOperationState operationState = new ClientOperationState();
        private readonly WorldDebugTelemetryTracker telemetry = new WorldDebugTelemetryTracker();
        private readonly List<MetricSection> sections = new List<MetricSection>();
        private readonly List<LookRow> lookRows = new List<LookRow>();
        private readonly GUIContent measured = new GUIContent();
        private int sectionCount;
        private MetricSection building;
        private float nextRefreshTime;
        private float laidOutWidth = -1f;
        private BodyLook rowsLook;
        private string rowsModel;
        private BodyAppearanceCatalog rowsCatalog;

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
                nextRefreshTime = 0f;
            }

            var client = simulationClient;
            if (client == null)
            {
                telemetry.ResetNetwork();
            }
            else
            {
                telemetry.SampleNetwork(
                    Time.realtimeSinceStartup,
                    client.RealtimePacketsReceived,
                    client.RealtimeBytesReceived,
                    client.RealtimePacketsSent,
                    client.RealtimeBytesSent,
                    client.SnapshotPacketsReceived,
                    client.SnapshotFramesReceived);
            }

            if (isVisible && Time.unscaledTime >= nextRefreshTime)
            {
                nextRefreshTime = Time.unscaledTime + RefreshSeconds;
                BuildMetrics();
            }
        }

        // The text of every metric section, from the latest state.
        private void BuildMetrics()
        {
            sectionCount = 0;
            BuildConnectionSection();
            BuildRenderingSection();
            BuildPredictionSection();
            BuildNetworkSection();
            laidOutWidth = -1f;
        }

        private void OnGUI()
        {
            if (!isVisible)
            {
                return;
            }

            var panelRect = TemporaryPanelStyles.GetBottomLeftPanelRect(PanelWidth, 670f);
            pointerFocus?.ReportInterfaceRect(panelRect);
            GUILayout.BeginArea(
                panelRect,
                "World Client Debug (F2)",
                GUI.skin.window);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);

            DrawMetricSections(panelRect.width - RowInset - LabelWidth);
            DrawPresentationSection();
            DrawStatusAndLeave();

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static void DrawSectionHeader(string text)
        {
            GUILayout.Space(5f);
            GUILayout.Label(text, GUI.skin.box, ExpandWidth);
        }

        // Every metric section: its header, then one rect for all its rows, with each label in a
        // column LabelWidth wide and its value, wrapped, beside it.
        private void DrawMetricSections(float valueWidth)
        {
            var style = GUI.skin.label;
            if (!Mathf.Approximately(laidOutWidth, valueWidth))
            {
                laidOutWidth = valueWidth;
                for (var index = 0; index < sectionCount; index++)
                {
                    sections[index].Measure(style, LabelWidth, valueWidth, measured);
                }
            }

            for (var index = 0; index < sectionCount; index++)
            {
                var section = sections[index];
                DrawSectionHeader(section.Title);
                var rect = GUILayoutUtility.GetRect(0f, section.Height, ExpandWidth);
                if (Event.current.type == EventType.Repaint)
                {
                    section.Draw(rect, style, LabelWidth);
                }
            }
        }

        private void BeginSection(string title)
        {
            if (sections.Count <= sectionCount)
            {
                sections.Add(new MetricSection());
            }

            building = sections[sectionCount++];
            building.Begin(title);
        }

        private void AddMetric(
            string label,
            string value,
            WorldDebugHealth health = WorldDebugHealth.Neutral)
        {
            building.Add(label, value, ColorFor(health));
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

        private void BuildConnectionSection()
        {
            BeginSection("Connection");
            var session = ClientSession.ActiveSimulationSession;
            var client = simulationClient;
            if (session == null || client == null)
            {
                AddMetric("State", "No local simulation session", WorldDebugHealth.Unavailable);
                return;
            }

            AddMetric(
                "State",
                client.State.ToString(),
                client.IsJoined ? WorldDebugHealth.Healthy : WorldDebugHealth.Warning);
            AddMetric("Character", session.characterName);
            AddMetric("Shard / World", session.shardId + " / " + session.worldId);
            AddMetric(
                "Worker / Runtime",
                session.workerId + " / "
                + WorldDebugDiagnostics.ShortIdentifier(session.workerRuntimeId));
            AddMetric(
                "UDP / Protocol",
                client.ConnectedHost + ":" + client.ConnectedPort
                + " / v" + RealtimeProtocol.Version);
            AddMetric("Observed Server Tick", client.LatestServerTick.ToString());
        }

        private void BuildRenderingSection()
        {
            BeginSection("Client Rendering");
            AddMetric(
                "FPS / 60 FPS Budget",
                WorldDebugDiagnostics.Rate(telemetry.FramesPerSecond, "FPS")
                + " / " + WorldDebugDiagnostics.Milliseconds(TargetFrameBudgetMilliseconds),
                WorldDebugDiagnostics.EvaluateFrame(
                    telemetry.P95FrameMilliseconds,
                    TargetFrameBudgetMilliseconds));
            AddMetric(
                "Frame Avg / P95 / Max",
                WorldDebugDiagnostics.Milliseconds(telemetry.AverageFrameMilliseconds)
                + " / " + WorldDebugDiagnostics.Milliseconds(telemetry.P95FrameMilliseconds)
                + " / " + WorldDebugDiagnostics.Milliseconds(telemetry.MaximumFrameMilliseconds));
            AddMetric(
                "Resolution / Quality",
                Screen.width + "x" + Screen.height + " / " + CurrentQualityName());
            AddMetric(
                "VSync / Target FPS",
                QualitySettings.vSyncCount + " / " + Application.targetFrameRate);
            AddMetric(
                "Unity Allocated / Reserved",
                WorldDebugDiagnostics.Megabytes(BytesToMebibytes(Profiler.GetTotalAllocatedMemoryLong()))
                + " / "
                + WorldDebugDiagnostics.Megabytes(BytesToMebibytes(Profiler.GetTotalReservedMemoryLong())));
            AddMetric(
                "Managed Heap Used",
                WorldDebugDiagnostics.Megabytes(BytesToMebibytes(Profiler.GetMonoUsedSizeLong())));
        }

        private void BuildPredictionSection()
        {
            BeginSection("Client Prediction");
            var client = simulationClient;
            if (client == null
                || client.MovementSession == null
                || localPlayerController == null)
            {
                AddMetric("Prediction", "Unavailable", WorldDebugHealth.Unavailable);
                return;
            }

            AddMetric(
                "Authority",
                localPlayerController.IsServerAuthoritative
                    ? "Server authoritative with client prediction"
                    : "Offline",
                localPlayerController.IsServerAuthoritative
                    ? WorldDebugHealth.Healthy
                    : WorldDebugHealth.Warning);
            AddMetric(
                "Tick / Snapshot Configuration",
                client.MovementSession.Settings.TickRateHz + " Hz / "
                + client.MovementSession.SnapshotRateHz + " Hz");
            var carryState = client.MovementSession.CarryState;
            AddMetric(
                "Carry Weight / Capacity",
                carryState.CarriedWeight + " / " + carryState.CarryCapacity);
            AddMetric(
                "Item Revision / Movement",
                carryState.ItemStateRevision + " / "
                + (carryState.MovementMultiplierBasisPoints / 100f).ToString("0.##")
                + "%");
            AddMetric(
                "Client Tick / Entity",
                localPlayerController.ClientSimulationTick + " / "
                + client.MovementSession.ControlledEntityId);
            AddMetric(
                "Pending Predicted Inputs",
                localPlayerController.PendingPredictedInputCount.ToString(),
                WorldDebugDiagnostics.EvaluatePendingInputs(
                    localPlayerController.PendingPredictedInputCount));
            AddMetric(
                "Reconciliations / Hard Snaps",
                localPlayerController.ReconciliationCount + " / "
                + localPlayerController.HardReconciliationCount,
                localPlayerController.HardReconciliationCount == 0
                    ? WorldDebugHealth.Healthy
                    : WorldDebugHealth.Warning);
            AddMetric(
                "Latest Position Correction",
                localPlayerController.LatestReconciliationDistance.ToString("0.000") + " m");
            var path = localPlayerController.CurrentPath;
            var progress = localPlayerController.PredictedState.PathProgress;
            AddMetric(
                "Path / Waypoint / Blocked",
                path == null
                    ? "Standing"
                    : path.Id + " / " + Math.Min(progress.NextWaypointIndex, path.Count)
                        + " of " + path.Count + " / " + progress.BlockedTicks);
            AddMetric(
                "Latest Plan",
                localPlayerController.LatestPlanStatus + ", "
                + localPlayerController.LatestPlanExpandedNodes + " nodes, "
                + localPlayerController.LatestPlanMilliseconds.ToString("0.0") + " ms",
                localPlayerController.LatestPlanMilliseconds <= TargetFrameBudgetMilliseconds
                    ? WorldDebugHealth.Healthy
                    : WorldDebugHealth.Warning);
        }

        private void BuildNetworkSection()
        {
            BeginSection("Observed Realtime Network");
            var client = simulationClient;
            if (client == null || client.MovementSession == null)
            {
                AddMetric("Network", "Unavailable", WorldDebugHealth.Unavailable);
                return;
            }

            AddMetric(
                "Round Trip Time",
                client.RoundTripTimeMilliseconds + " ms",
                WorldDebugDiagnostics.EvaluatePing(client.RoundTripTimeMilliseconds));
            AddMetric(
                "Latest Snapshot Age",
                WorldDebugDiagnostics.Age(client.LatestSnapshotAgeSeconds),
                WorldDebugDiagnostics.EvaluateSnapshotAge(
                    client.LatestSnapshotAgeSeconds,
                    client.MovementSession.SnapshotRateHz));
            AddMetric(
                "Observed Snapshot Frames",
                WorldDebugDiagnostics.Rate(telemetry.SnapshotFramesPerSecond, "frames/s")
                + " of " + client.MovementSession.SnapshotRateHz + " configured");
            AddMetric(
                "Snapshot Packets / Frames",
                WorldDebugDiagnostics.Rate(telemetry.SnapshotPacketsPerSecond, "pkt/s")
                + " / "
                + WorldDebugDiagnostics.Rate(telemetry.SnapshotFramesPerSecond, "frames/s"));
            AddMetric(
                "App Packets In / Out",
                WorldDebugDiagnostics.Rate(telemetry.ReceivedPacketsPerSecond, "pkt/s")
                + " / " + WorldDebugDiagnostics.Rate(telemetry.SentPacketsPerSecond, "pkt/s"));
            AddMetric(
                "Payload In / Out",
                WorldDebugDiagnostics.Rate(telemetry.ReceivedKibibytesPerSecond, "KiB/s")
                + " / " + WorldDebugDiagnostics.Rate(telemetry.SentKibibytesPerSecond, "KiB/s"));
            var estimatedLoss = WorldDebugDiagnostics.EstimateSnapshotLossPercent(
                client.SnapshotFramesReceived,
                client.EstimatedMissingSnapshotFrames);
            AddMetric(
                "Estimated Snapshot Loss",
                WorldDebugDiagnostics.Percent(estimatedLoss)
                + " (" + client.EstimatedMissingSnapshotFrames + " missing)",
                WorldDebugDiagnostics.EvaluateLoss(estimatedLoss));
            AddMetric(
                "Snapshot Frames / Packets Total",
                client.SnapshotFramesReceived + " / " + client.SnapshotPacketsReceived);
            var knownEntities = client.SpawnedEntityCount;
            AddMetric(
                "Known / Remote Entities",
                knownEntities + " / " + Math.Max(0, knownEntities - 1));
            AddMetric(
                "Rate Sample Window",
                WorldDebugDiagnostics.Age(telemetry.NetworkSampleSeconds));
        }

        private void DrawPresentationSection()
        {
            DrawSectionHeader("Presentation");
            if (bodyTint == null)
            {
                GUILayout.Label("Body Tint: unavailable");
                return;
            }

            bodyTint.Enabled = GUILayout.Toggle(
                bodyTint.Enabled,
                " Tint bodies: you purple, players orange, bots blue");
            if (bodyLook == null || !bodyLook.Received)
            {
                return;
            }

            GUILayout.Label("Your look is stored on the server; only a Development GameService changes it here.");
            if (!ReferenceEquals(rowsLook, bodyLook.Look) || rowsModel != bodyLook.Model || rowsCatalog != bodyLook.Catalog)
            {
                BuildLookRows();
            }

            // A step changes the look, which builds the rows again, so it waits until every row
            // was drawn.
            LookRow pressed = null;
            var step = 0;
            foreach (var row in lookRows)
            {
                var rowStep = StepButtons(row.Label);
                if (rowStep != 0)
                {
                    pressed = row;
                    step = rowStep;
                }
            }

            pressed?.Step(step);
        }

        // The rows that step through the look of the local body: its body model, every appearance
        // slot, every category of its paint layers, and its hair.
        private void BuildLookRows()
        {
            rowsLook = bodyLook.Look;
            rowsModel = bodyLook.Model;
            rowsCatalog = bodyLook.Catalog;
            lookRows.Clear();
            AddBodyModelRow();
            AddLookRows();
        }

        // A row that steps through the body models of the World scene, such as Human_Male and
        // Human_Female, with the rule of when a player chooses one.
        private void AddBodyModelRow()
        {
            var models = bodyLook.Models;
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

            lookRows.Add(new LookRow(
                "Your body: " + (shown != null ? shown.Name : "none") + " (" + RuleText(AppearanceRules.BodyModel) + ")",
                step =>
                {
                    var chosen = models.Models[(current + step + models.Models.Count) % models.Models.Count];
                    if (chosen != null)
                    {
                        bodyLook.RequestDevelopmentLook(chosen.Name, bodyLook.Look);
                    }
                }));
        }

        // One row per appearance slot of the local body, such as Face and Eye, and per category of
        // its paint layers, such as its underwear and a tattoo, that steps through the options,
        // with the rule of when a player chooses it.
        private void AddLookRows()
        {
            var catalog = bodyLook.Catalog;
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
                lookRows.Add(new LookRow(
                    "Your " + slot.Name.ToLowerInvariant() + ": " + options[current].Name + rule,
                    step => bodyLook.RequestDevelopmentLook(
                        null,
                        bodyLook.Look.With(slot.Name, options[(current + step + options.Count) % options.Count].Name))));
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
                lookRows.Add(new LookRow(
                    "Your " + name + ": " + (choices[current] != null ? choices[current].Name : "none") + " (" + RuleText(rule) + ")",
                    step =>
                    {
                        if (choices.Count < 2)
                        {
                            return;
                        }

                        var look = bodyLook.Look;
                        foreach (var paint in group)
                        {
                            look = look.WithPaint(paint.Name, false);
                        }

                        var chosen = choices[(current + step + choices.Count) % choices.Count];
                        bodyLook.RequestDevelopmentLook(null, chosen != null ? look.WithPaint(chosen.Name, true) : look);
                    }));
            }

            // The hair is part of the look, a garment of the hair slot or none; every other garment
            // comes from the equipped items.
            var hairs = new List<BodyAppearanceCatalog.Garment> { null };
            hairs.AddRange(catalog.Garments.Where(garment => garment != null && garment.Slot == BodyAppearanceCatalog.Garment.HairSlot));
            if (hairs.Count > 1)
            {
                var current = Math.Max(hairs.FindIndex(hair => hair != null && bodyLook.Look.HasGarment(hair.Name)), 0);
                lookRows.Add(new LookRow(
                    "Your hair: " + (hairs[current] != null ? hairs[current].Name : "none") + " (" + RuleText(AppearanceRules.Hair) + ")",
                    step =>
                    {
                        var look = bodyLook.Look.WithoutGarments();
                        var chosen = hairs[(current + step + hairs.Count) % hairs.Count];
                        bodyLook.RequestDevelopmentLook(null, chosen != null ? look.WithGarment(chosen.Name, true) : look);
                    }));
            }
        }

        // When a player chooses a kind of look: in character creation, and for some kinds in the game.
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
            GUILayout.Label(label, StepLabelWidth);
            if (GUILayout.Button("<", StepButtonWidth))
            {
                step = -1;
            }

            if (GUILayout.Button(">", StepButtonWidth))
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
            if (GUILayout.Button("Leave Shard", LeaveButtonHeight))
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

        // A metric section's rows as text, and their heights once measured for the panel's width.
        private sealed class MetricSection
        {
            private readonly List<(string Label, string Value, Color Color)> rows = new List<(string Label, string Value, Color Color)>();
            private readonly List<float> heights = new List<float>();

            public string Title { get; private set; }

            public float Height { get; private set; }

            public void Begin(string title)
            {
                Title = title;
                rows.Clear();
                heights.Clear();
                Height = 0f;
            }

            public void Add(string label, string value, Color color)
            {
                rows.Add((label, value ?? string.Empty, color));
            }

            // Every row is as high as its label or its value, wrapped, needs, and rows keep the gap
            // that GUILayout keeps between labels.
            public void Measure(GUIStyle style, float labelWidth, float valueWidth, GUIContent content)
            {
                heights.Clear();
                Height = 0f;
                var gap = Mathf.Max(style.margin.top, style.margin.bottom);
                foreach (var row in rows)
                {
                    content.text = row.Label;
                    var labelHeight = style.CalcHeight(content, labelWidth);
                    content.text = row.Value;
                    var height = Mathf.Max(labelHeight, style.CalcHeight(content, Mathf.Max(1f, valueWidth))) + gap;
                    heights.Add(height);
                    Height += height;
                }
            }

            public void Draw(Rect rect, GUIStyle style, float labelWidth)
            {
                var previousColor = GUI.color;
                var y = rect.y;
                for (var index = 0; index < rows.Count && index < heights.Count; index++)
                {
                    var (label, value, color) = rows[index];
                    var height = heights[index];
                    GUI.color = previousColor;
                    GUI.Label(new Rect(rect.x, y, labelWidth, height), label, style);
                    GUI.color = color;
                    GUI.Label(new Rect(rect.x + labelWidth, y, rect.width - labelWidth, height), value, style);
                    y += height;
                }

                GUI.color = previousColor;
            }
        }

        // A row of the look: its text, and what a press of its buttons does, back or forth.
        private sealed class LookRow
        {
            public LookRow(string label, Action<int> step)
            {
                Label = label;
                Step = step;
            }

            public string Label { get; }

            public Action<int> Step { get; }
        }
    }
}
