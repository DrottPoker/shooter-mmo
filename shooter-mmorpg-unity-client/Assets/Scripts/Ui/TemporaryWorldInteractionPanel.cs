using System;
using System.Collections.Generic;
using System.Linq;
using ShooterMmo.GameProtocol;
using ShooterMmo.Gameplay;
using ShooterMmo.Items;
using ShooterMmo.WorldData.Items;
using ShooterMmo.WorldActors;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace ShooterMmo.Ui
{
    [DisallowMultipleComponent]
    public sealed class TemporaryWorldInteractionPanel : MonoBehaviour
    {
        private static readonly Color PanelColor =
            new Color(0.055f, 0.065f, 0.08f, 0.96f);
        private WorldInteractionClientController interactionController;
        private WorldInteractionTargetingController targetingController;
        private RectTransform panel;
        private RectTransform content;
        private Text prompt;
        private Font font;
        private ThirdPersonCameraController playerCamera;
        private InventoryClientController inventoryController;
        private bool cursorReleased;

        private void Start()
        {
            interactionController = ShooterMmoClientBootstrap.WorldInteractionController;
            inventoryController = ShooterMmoClientBootstrap.InventoryController;
            targetingController = FindAnyObjectByType<WorldInteractionTargetingController>();
            if (interactionController != null)
            {
                interactionController.State.Changed += Rebuild;
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
        }

        private void OnDestroy()
        {
            if (interactionController != null)
            {
                interactionController.State.Changed -= Rebuild;
            }

            if (targetingController != null)
            {
                targetingController.Changed -= Rebuild;
            }

            if (inventoryController != null)
            {
                inventoryController.State.Changed -= Rebuild;
            }

            SetCursorReleased(false);
        }

        private void BuildUi()
        {
            EnsureEventSystem();
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var canvasObject = new GameObject(
                "TemporaryWorldInteractionCanvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 40;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var promptObject = CreateUiObject("InteractionPrompt", canvasObject.transform);
            var promptRect = promptObject.GetComponent<RectTransform>();
            promptRect.anchorMin = new Vector2(0.5f, 0.5f);
            promptRect.anchorMax = new Vector2(0.5f, 0.5f);
            promptRect.pivot = new Vector2(0.5f, 0f);
            promptRect.anchoredPosition = new Vector2(0f, 52f);
            promptRect.sizeDelta = new Vector2(600f, 32f);
            prompt = promptObject.AddComponent<Text>();
            ConfigureText(prompt, 20, TextAnchor.MiddleCenter);

            var panelObject = CreateUiObject("InteractionPanel", canvasObject.transform);
            panel = panelObject.GetComponent<RectTransform>();
            panel.anchorMin = new Vector2(0.5f, 0f);
            panel.anchorMax = new Vector2(0.5f, 0f);
            panel.pivot = new Vector2(0.5f, 0f);
            panel.anchoredPosition = new Vector2(0f, 28f);
            panel.sizeDelta = new Vector2(520f, 360f);
            panelObject.AddComponent<Image>().color = PanelColor;
            var layout = panelObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 16, 16);
            layout.spacing = 8f;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            content = panel;
        }

        private void Rebuild()
        {
            if (panel == null || interactionController == null)
            {
                return;
            }

            var target = targetingController?.CurrentTarget;
            prompt.text = target != null && target.IsTargetActive
                ? "E  Interact with " + target.TargetDisplayName
                : string.Empty;
            prompt.gameObject.SetActive(!string.IsNullOrWhiteSpace(prompt.text));

            for (var index = content.childCount - 1; index >= 0; index--)
            {
                Destroy(content.GetChild(index).gameObject);
            }

            var state = interactionController.State;
            var active = state.ActiveInteraction;
            var showPanel = active != null
                || state.PendingOperationId != Guid.Empty
                || !string.IsNullOrWhiteSpace(state.LastMessage);
            panel.gameObject.SetActive(showPanel);
            SetCursorReleased(showPanel);
            if (!showPanel)
            {
                return;
            }

            if (active != null)
            {
                AddText(active.DisplayName, 24, TextAnchor.MiddleLeft);
                AddText(
                    "Authoritative capabilities, revision "
                    + active.CapabilitySummaryRevision,
                    15,
                    TextAnchor.MiddleLeft);
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

                    var label = capability.DisplayName
                        + (capability.IsAvailable ? string.Empty : " (Unavailable)");
                    AddButton(
                        label,
                        capability.IsAvailable,
                        () => SubmitCapability(capability.Id));
                }

                AddButton(
                    "Close",
                    state.PendingOperationId == Guid.Empty,
                    SubmitClose);
            }
            else if (state.PendingOperationId != Guid.Empty)
            {
                AddText("Opening interaction...", 20, TextAnchor.MiddleCenter);
            }

            if (!string.IsNullOrWhiteSpace(state.LastMessage))
            {
                AddText(state.LastMessage, 15, TextAnchor.MiddleLeft);
                if (active == null && state.PendingOperationId == Guid.Empty)
                {
                    AddButton("Dismiss", true, state.ClearMessage);
                }
            }
        }

        private void DrawInsuranceCapability(RealtimeWorldActorCapability capability)
        {
            AddText("Insurance", 19, TextAnchor.MiddleLeft);
            var state = inventoryController?.State;
            if (!capability.IsAvailable || state?.FullSnapshot == null || state.Catalog == null)
            {
                AddText(
                    capability.IsAvailable
                        ? "Authoritative inventory state is loading."
                        : "Insurance is currently unavailable.",
                    14,
                    TextAnchor.MiddleLeft);
                inventoryController?.EnsureFullState();
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
                AddText("No insurance-eligible owned items.", 14, TextAnchor.MiddleLeft);
                return;
            }

            foreach (var item in eligible)
            {
                var insured = item.Policies.Any(policy =>
                    string.Equals(policy.Kind, ItemPolicyIds.Insured, StringComparison.Ordinal)
                    && string.Equals(policy.Status, ItemPolicyRules.ActiveStatus, StringComparison.Ordinal));
                var action = insured
                    ? RealtimeNpcLifecycleActionKind.RemoveInsurance
                    : RealtimeNpcLifecycleActionKind.ApplyInsurance;
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

        private void DrawQuestCapability(RealtimeWorldActorCapability capability)
        {
            AddText("Quest item lifecycle", 19, TextAnchor.MiddleLeft);
            var state = inventoryController?.State;
            if (!capability.IsAvailable || state?.FullSnapshot == null)
            {
                AddText(
                    capability.IsAvailable
                        ? "Authoritative inventory state is loading."
                        : "Quest lifecycle is currently unavailable.",
                    14,
                    TextAnchor.MiddleLeft);
                inventoryController?.EnsureFullState();
                return;
            }

            AddButton(
                "Accept quest and grant required item",
                state.CanMutate,
                () => SubmitLifecycle(
                    capability.Id,
                    RealtimeNpcLifecycleActionKind.AcceptQuest,
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
                    RealtimeNpcLifecycleActionKind.AbandonQuest,
                    state.KnownItemStateRevision,
                    Guid.Empty,
                    0,
                    Guid.Empty,
                    -1));
        }

        private void SubmitLifecycle(
            string capabilityId,
            RealtimeNpcLifecycleActionKind action,
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

        private void AddText(string value, int size, TextAnchor alignment)
        {
            var text = CreateUiObject("Text", content).AddComponent<Text>();
            ConfigureText(text, size, alignment);
            text.text = value;
            text.gameObject.AddComponent<LayoutElement>().preferredHeight = size + 12f;
        }

        private void AddButton(string label, bool interactable, Action action)
        {
            var buttonObject = CreateUiObject("CapabilityButton", content);
            buttonObject.AddComponent<Image>().color = new Color(0.12f, 0.14f, 0.17f, 1f);
            var button = buttonObject.AddComponent<Button>();
            button.interactable = interactable;
            button.onClick.AddListener(() => action());
            buttonObject.AddComponent<LayoutElement>().preferredHeight = 38f;
            var text = CreateUiObject("Label", buttonObject.transform).AddComponent<Text>();
            ConfigureText(text, 16, TextAnchor.MiddleCenter);
            text.text = label;
            var rect = text.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private void ConfigureText(Text text, int size, TextAnchor alignment)
        {
            text.font = font;
            text.fontSize = size;
            text.color = Color.white;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
        }

        private static GameObject CreateUiObject(string name, Transform parent)
        {
            var value = new GameObject(name, typeof(RectTransform));
            value.transform.SetParent(parent, false);
            return value;
        }

        private static void EnsureEventSystem()
        {
            if (FindAnyObjectByType<EventSystem>() != null)
            {
                return;
            }

            var eventSystem = new GameObject(
                "EventSystem",
                typeof(EventSystem),
                typeof(InputSystemUIInputModule));
            DontDestroyOnLoad(eventSystem);
        }

        private void SetCursorReleased(bool value)
        {
            if (cursorReleased == value)
            {
                return;
            }

            if (playerCamera == null)
            {
                var player = FindAnyObjectByType<LocalPlayerController>();
                playerCamera = player != null ? player.PlayerCamera : null;
            }

            if (playerCamera == null)
            {
                cursorReleased = false;
                return;
            }

            playerCamera.SetUiCursorReleased(this, value);
            cursorReleased = value;
        }
    }
}
