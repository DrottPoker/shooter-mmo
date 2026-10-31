using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    [DisallowMultipleComponent]
    public sealed class TemporaryWorldInteractionPanel : MonoBehaviour
    {
        // Above the combat HUD and below the inventory.
        public const int CanvasSortingOrder = 40;

        private const float PanelWidth = 460f;
        private WorldInteractionClientController interactionController;
        private WorldInteractionTargetingController targetingController;
        private RectTransform panel;
        private RectTransform content;
        private RectTransform gatheringProgressFill;
        private Guid requestedInventorySessionId;
        private InventoryClientController inventoryController;

        // Called by the composition root before the component is activated.
        public void Initialize(
            WorldInteractionClientController worldInteraction,
            InventoryClientController inventory)
        {
            interactionController = worldInteraction;
            inventoryController = inventory;
        }

        private void Start()
        {
            targetingController = FindAnyObjectByType<WorldInteractionTargetingController>();
            if (interactionController != null)
            {
                interactionController.State.Changed += OnInteractionStateChanged;
                interactionController.Gathering.Changed += Rebuild;
            }

            if (targetingController != null)
            {
                targetingController.Changed += Rebuild;
            }

            if (inventoryController != null)
            {
                inventoryController.State.Changed += Rebuild;
            }

            BuildUi();
            Rebuild();
        }

        private void Update()
        {
            if (targetingController == null)
            {
                targetingController = FindAnyObjectByType<WorldInteractionTargetingController>();
                if (targetingController != null)
                {
                    targetingController.Changed += Rebuild;
                    Rebuild();
                }
            }

            UpdateGatheringProgress();
        }

        private void UpdateGatheringProgress()
        {
            if (gatheringProgressFill == null || interactionController == null)
            {
                return;
            }

            var progress = interactionController.Gathering.CycleProgress(Time.unscaledTime);
            gatheringProgressFill.anchorMax = new Vector2(progress, 1f);
        }

        // Insurance and quest actions need the committed inventory. It is requested once
        // per opened interaction, never while drawing, because drawing runs on every
        // inventory change.
        private void OnInteractionStateChanged()
        {
            var active = interactionController.State.ActiveInteraction;
            var activeSessionId = active?.InteractionSessionId ?? Guid.Empty;
            if (activeSessionId != Guid.Empty && activeSessionId != requestedInventorySessionId)
            {
                requestedInventorySessionId = activeSessionId;
                inventoryController?.EnsureFullState();
            }
            else if (activeSessionId == Guid.Empty)
            {
                requestedInventorySessionId = Guid.Empty;
            }

            Rebuild();
        }

        private void OnDestroy()
        {
            if (interactionController != null)
            {
                interactionController.State.Changed -= OnInteractionStateChanged;
                interactionController.Gathering.Changed -= Rebuild;
            }

            if (targetingController != null)
            {
                targetingController.Changed -= Rebuild;
            }

            if (inventoryController != null)
            {
                inventoryController.State.Changed -= Rebuild;
            }
        }

        private void BuildUi()
        {
            TemporaryUiKit.EnsureEventSystem();
            var canvasObject = new GameObject(
                "TemporaryWorldInteractionCanvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = CanvasSortingOrder;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var panelObject = TemporaryUiKit.CreateUiObject("InteractionPanel", canvasObject.transform);
            panel = panelObject.GetComponent<RectTransform>();
            panel.anchorMin = new Vector2(0.5f, 0f);
            panel.anchorMax = new Vector2(0.5f, 0f);
            panel.pivot = new Vector2(0.5f, 0f);
            panel.anchoredPosition = new Vector2(0f, 28f);
            panel.sizeDelta = new Vector2(PanelWidth, 0f);
            var background = panelObject.AddComponent<Image>();
            background.color = TemporaryUiKit.PanelColor;
            TemporaryUiKit.AddOutline(background, TemporaryUiKit.PanelBorderColor);
            var layout = panelObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 14, 14);
            layout.spacing = 6f;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            // The panel grows upward with its content.
            panelObject.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;
            content = panel;
        }

        private void Rebuild()
        {
            if (panel == null || interactionController == null)
            {
                return;
            }

            var approaching = targetingController?.ApproachTarget;
            gatheringProgressFill = null;

            // Hidden children leave the fitted layout at once; destruction waits for frame end.
            for (var index = content.childCount - 1; index >= 0; index--)
            {
                var child = content.GetChild(index).gameObject;
                child.SetActive(false);
                Destroy(child);
            }

            var state = interactionController.State;
            var active = state.ActiveInteraction;
            var showPanel = active != null
                || approaching != null
                || state.PendingOperationId != Guid.Empty
                || !string.IsNullOrWhiteSpace(state.LastMessage);
            panel.gameObject.SetActive(showPanel);
            if (!showPanel)
            {
                return;
            }

            if (approaching != null)
            {
                AddText(
                    "Walking to " + approaching.TargetDisplayName + "...",
                    16,
                    FontStyle.Normal,
                    TemporaryUiKit.TextSecondary);
            }

            if (active != null)
            {
                AddText(active.DisplayName, 22, FontStyle.Bold, TemporaryUiKit.TextPrimary);
                AddText(
                    "Capabilities revision " + active.CapabilitySummaryRevision,
                    13,
                    FontStyle.Normal,
                    TemporaryUiKit.TextMuted);
                for (var index = 0; index < active.Capabilities.Length; index++)
                {
                    var capability = active.Capabilities[index];
                    if (capability.Kind == RealtimeWorldActorCapabilityKind.Insurance)
                    {
                        DrawInsuranceCapability(capability);
                        continue;
                    }

                    if (capability.Kind == RealtimeWorldActorCapabilityKind.QuestOffer)
                    {
                        DrawQuestCapability(capability);
                        continue;
                    }

                    if (capability.Kind == RealtimeWorldActorCapabilityKind.Gathering)
                    {
                        DrawGatheringCapability(active, capability);
                        continue;
                    }

                    // Storage capabilities open the complete inventory view.
                    var opensStorage = capability.Kind
                        is RealtimeWorldActorCapabilityKind.Bank
                        or RealtimeWorldActorCapabilityKind.RecoveryStorage;
                    var label = (opensStorage ? "Open " : string.Empty)
                        + capability.DisplayName
                        + (capability.IsAvailable ? string.Empty : " (Unavailable)");
                    AddButton(
                        label,
                        capability.IsAvailable,
                        () => SubmitCapability(capability.Id));
                }

                AddButton(
                    "Close",
                    state.PendingOperationId == Guid.Empty,
                    SubmitClose,
                    TemporaryButtonTone.Quiet);
            }
            else if (state.PendingOperationId != Guid.Empty)
            {
                AddText("Opening interaction...", 18, FontStyle.Normal, TemporaryUiKit.TextSecondary);
            }

            if (!string.IsNullOrWhiteSpace(state.LastMessage))
            {
                AddText(
                    state.LastMessage,
                    14,
                    FontStyle.Normal,
                    string.IsNullOrEmpty(state.LastErrorCode)
                        ? TemporaryUiKit.TextSecondary
                        : TemporaryUiKit.DangerColor);
                if (active == null && state.PendingOperationId == Guid.Empty)
                {
                    AddButton("Dismiss", true, state.ClearMessage, TemporaryButtonTone.Quiet);
                }
            }
        }

        private void DrawInsuranceCapability(RealtimeWorldActorCapability capability)
        {
            AddSectionTitle("Insurance");
            var state = inventoryController?.State;
            if (!capability.IsAvailable || state?.FullSnapshot == null || state.Catalog == null)
            {
                AddText(
                    capability.IsAvailable
                        ? "Authoritative inventory state is loading."
                        : "Insurance is currently unavailable.",
                    14,
                    FontStyle.Normal,
                    TemporaryUiKit.TextSecondary);
                return;
            }

            var eligible = CollectOwnedItems(state.FullSnapshot)
                .Where(item => state.Catalog.TryGetDefinition(item.DefinitionId, out var definition)
                    && ItemPolicyRules.CanApplyInsurance(definition))
                .OrderBy(item => state.Catalog.GetDisplayName(item.DefinitionId), StringComparer.Ordinal)
                .ThenBy(item => item.ItemInstanceId)
                .ToArray();
            if (eligible.Length == 0)
            {
                AddText(
                    "No insurance-eligible owned items.",
                    14,
                    FontStyle.Normal,
                    TemporaryUiKit.TextSecondary);
                return;
            }

            foreach (var item in eligible)
            {
                var insured = item.Policies.Any(policy =>
                    string.Equals(policy.Kind, ItemPolicyIds.Insured, StringComparison.Ordinal)
                    && string.Equals(policy.Status, ItemPolicyRules.ActiveStatus, StringComparison.Ordinal));
                var action = insured
                    ? RealtimeCapabilityActionKind.RemoveInsurance
                    : RealtimeCapabilityActionKind.ApplyInsurance;
                var label = (insured ? "Insured, remove: " : "Insure: ")
                    + state.Catalog.GetDisplayName(item.DefinitionId);
                AddButton(
                    label,
                    state.CanMutate,
                    () => SubmitLifecycle(
                        capability.Id,
                        action,
                        state.KnownItemStateRevision,
                        item.ItemInstanceId,
                        item.Revision,
                        Guid.Empty,
                        -1));
            }
        }

        // Gathering runs while the interaction stays open. The worker owns every cycle; the
        // bar only shows the time until the next yield.
        private void DrawGatheringCapability(
            RealtimeWorldInteractionOpened active,
            RealtimeWorldActorCapability capability)
        {
            var gathering = interactionController.Gathering;
            var here = gathering.InteractionSessionId == active.InteractionSessionId;
            if (here && gathering.Status == GatheringClientStatus.Gathering)
            {
                var yielded = gathering.YieldedQuantity > 0
                    ? "  Gathered " + gathering.YieldedQuantity + " "
                        + DisplayName(gathering.YieldDefinitionId) + "."
                    : string.Empty;
                AddText(capability.DisplayName + "..." + yielded, 16, FontStyle.Bold, TemporaryUiKit.TextPrimary);
                AddProgressBar();
                AddText(
                    "Moving or closing the interaction stops gathering.",
                    13,
                    FontStyle.Normal,
                    TemporaryUiKit.TextMuted);
                return;
            }

            if (here && gathering.Status == GatheringClientStatus.Stopped)
            {
                AddText(
                    StoppedText(gathering),
                    14,
                    FontStyle.Normal,
                    TemporaryUiKit.TextSecondary);
            }

            AddButton(
                capability.DisplayName + (capability.IsAvailable ? string.Empty : " (Unavailable)"),
                capability.IsAvailable && interactionController.State.PendingOperationId == Guid.Empty,
                () => SubmitStartGathering(capability.Id));
        }

        private string StoppedText(GatheringClientState gathering)
        {
            var gathered = gathering.YieldedQuantity > 0
                ? "Gathered " + gathering.YieldedQuantity + " "
                    + DisplayName(gathering.YieldDefinitionId) + ". "
                : string.Empty;
            return gathered + gathering.StopMessage;
        }

        private string DisplayName(string definitionId)
        {
            var catalog = inventoryController?.State.Catalog;
            return catalog != null ? catalog.GetDisplayName(definitionId) : definitionId;
        }

        private void AddProgressBar()
        {
            var track = TemporaryUiKit.CreateUiObject("GatheringProgress", content);
            var trackImage = track.AddComponent<Image>();
            trackImage.color = TemporaryUiKit.InputColor;
            trackImage.raycastTarget = false;
            TemporaryUiKit.AddOutline(trackImage, TemporaryUiKit.PanelBorderColor);
            track.AddComponent<LayoutElement>().preferredHeight = 12f;
            var fill = TemporaryUiKit.CreateUiObject("Fill", track.transform).AddComponent<Image>();
            fill.color = TemporaryUiKit.AccentColor;
            fill.raycastTarget = false;
            gatheringProgressFill = fill.rectTransform;
            TemporaryUiKit.Stretch(
                gatheringProgressFill,
                Vector2.zero,
                new Vector2(0f, 1f),
                Vector2.zero,
                Vector2.zero);
            UpdateGatheringProgress();
        }

        private void SubmitStartGathering(string capabilityId)
        {
            if (!WorldInteractionClientController.HasEquippedGatheringTool(inventoryController?.State))
            {
                interactionController.State.SetLocalError(
                    "gathering_tool_required",
                    "Equip a gathering tool in the Tool slot to gather here.");
                return;
            }

            if (!interactionController.TryStartGathering(capabilityId, out var error))
            {
                interactionController.State.SetLocalError(
                    "world_interaction_client_rejected",
                    error);
            }
        }

        private void DrawQuestCapability(RealtimeWorldActorCapability capability)
        {
            AddSectionTitle("Quest item lifecycle");
            var state = inventoryController?.State;
            if (!capability.IsAvailable || state?.FullSnapshot == null)
            {
                AddText(
                    capability.IsAvailable
                        ? "Authoritative inventory state is loading."
                        : "Quest lifecycle is currently unavailable.",
                    14,
                    FontStyle.Normal,
                    TemporaryUiKit.TextSecondary);
                return;
            }

            AddButton(
                "Accept quest and grant required item",
                state.CanMutate,
                () => SubmitLifecycle(
                    capability.Id,
                    RealtimeCapabilityActionKind.AcceptQuest,
                    state.KnownItemStateRevision,
                    Guid.Empty,
                    0,
                    state.FullSnapshot.PermanentInventory.ContainerId,
                    -1));
            AddButton(
                "Abandon quest and remove its grant lineage",
                state.CanMutate,
                () => SubmitLifecycle(
                    capability.Id,
                    RealtimeCapabilityActionKind.AbandonQuest,
                    state.KnownItemStateRevision,
                    Guid.Empty,
                    0,
                    Guid.Empty,
                    -1));
        }

        private void SubmitLifecycle(
            string capabilityId,
            RealtimeCapabilityActionKind action,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            Guid destinationContainerId,
            int destinationSlotIndex)
        {
            if (!interactionController.TryExecuteCapability(
                    capabilityId,
                    action,
                    expectedCharacterRevision,
                    itemInstanceId,
                    expectedItemRevision,
                    destinationContainerId,
                    destinationSlotIndex,
                    out var error))
            {
                interactionController.State.SetLocalError(
                    "world_interaction_client_rejected",
                    error);
            }
        }

        private static IReadOnlyCollection<InventoryItem> CollectOwnedItems(
            CharacterInventorySnapshot snapshot)
        {
            var items = new Dictionary<Guid, InventoryItem>();
            AddContainerItems(snapshot.PermanentInventory, items);
            AddContainerItems(snapshot.Bank, items);
            AddContainerItems(snapshot.SecureContainer?.Contents, items);
            if (snapshot.EquippedBag != null)
            {
                items[snapshot.EquippedBag.Item.ItemInstanceId] = snapshot.EquippedBag.Item;
                AddContainerItems(snapshot.EquippedBag.Contents, items);
            }

            foreach (var slot in snapshot.Equipment)
            {
                if (slot.Item != null)
                {
                    items[slot.Item.ItemInstanceId] = slot.Item;
                }
            }

            return items.Values;
        }

        private static void AddContainerItems(
            InventoryContainer container,
            IDictionary<Guid, InventoryItem> items)
        {
            if (container == null)
            {
                return;
            }

            foreach (var slot in container.Slots)
            {
                if (slot.Item != null)
                {
                    items[slot.Item.ItemInstanceId] = slot.Item;
                }
            }
        }

        private void SubmitCapability(string capabilityId)
        {
            if (!interactionController.TryExecuteCapability(capabilityId, out var error))
            {
                interactionController.State.SetLocalError(
                    "world_interaction_client_rejected",
                    error);
            }
        }

        private void SubmitClose()
        {
            if (!interactionController.TryClose(out var error))
            {
                interactionController.State.SetLocalError(
                    "world_interaction_client_rejected",
                    error);
            }
        }

        private void AddText(string value, int size, FontStyle style, Color color)
        {
            TemporaryUiKit.CreateLabel(content, value, size, style, color, size + 10f);
        }

        private void AddSectionTitle(string value)
        {
            var title = TemporaryUiKit.CreateLabel(
                content,
                value,
                16,
                FontStyle.Bold,
                TemporaryUiKit.TextPrimary,
                28f);
            title.alignment = TextAnchor.LowerLeft;
        }

        private void AddButton(
            string label,
            bool interactable,
            Action action,
            TemporaryButtonTone tone = TemporaryButtonTone.Normal)
        {
            var button = TemporaryUiKit.CreateButton(content, label, action, interactable, tone);
            button.GetComponent<LayoutElement>().preferredHeight = 36f;
        }
    }
}
