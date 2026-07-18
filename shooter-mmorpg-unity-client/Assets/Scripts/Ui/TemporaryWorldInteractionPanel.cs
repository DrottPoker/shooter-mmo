using System;
using ShooterMmo.Gameplay;
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
        private bool cursorReleased;

        private void Start()
        {
            interactionController = ShooterMmoClientBootstrap.WorldInteractionController;
            targetingController = FindAnyObjectByType<WorldInteractionTargetingController>();
            if (interactionController != null)
            {
                interactionController.State.Changed += Rebuild;
            }

            if (targetingController != null)
            {
                targetingController.Changed += Rebuild;
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
