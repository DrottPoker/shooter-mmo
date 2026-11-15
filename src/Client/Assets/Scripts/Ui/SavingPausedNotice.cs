using Mmorpg.Client.Networking;
using Mmorpg.WorldData.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The one line over the World while the worker says that GameService does not answer, so
    // items, trade, and quests are refused and saving is paused. It goes when GameService answers
    // again or the session ends. It sits where the reconnect notice does, which shows only while
    // no session is joined, so the two never show together. It has no raycaster, so it never
    // takes a click. The bootstrap adds it to every World scene.
    [DisallowMultipleComponent]
    public sealed class SavingPausedNotice : MonoBehaviour
    {
        public const string PanelName = "SavingPausedNotice";
        public const string LineKey = "ui/world/saving-paused";

        // Over the HUD and the interaction panel, under every window the player opens.
        public const int CanvasSortingOrder = QuestWindows.CanvasSortingOrder - 5;

        private const float Width = 640f;
        private const float TopOffset = 150f;

        private UiTheme theme;
        private IRealtimeSession session;
        private TextCatalog texts = TextCatalog.Empty(TextTableFormat.SourceLocale);
        private RectTransform panel;
        private TextMeshProUGUI line;

        public bool IsVisible
        {
            get { return panel != null && panel.gameObject.activeSelf; }
        }

        public string LineText
        {
            get { return line != null ? line.text : string.Empty; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(UiTheme uiTheme, IRealtimeSession realtimeSession, TextCatalog textCatalog)
        {
            theme = uiTheme != null ? uiTheme : throw new System.ArgumentNullException(nameof(uiTheme));
            session = realtimeSession;
            texts = textCatalog ?? TextCatalog.Empty(TextTableFormat.SourceLocale);
        }

        // Shown only inside a joined session whose worker says that GameService does not answer.
        public static bool ShouldShow(IRealtimeSession session)
        {
            return session != null && session.IsJoined && !session.IsGameServiceAvailable;
        }

        private void Awake()
        {
            Refresh();
        }

        private void Update()
        {
            Refresh();
        }

        // Shows the line exactly while saving is paused. Update calls it every frame; the first
        // call builds the panel.
        public void Refresh()
        {
            if (panel == null)
            {
                BuildUi();
            }

            var active = ShouldShow(session);
            if (panel.gameObject.activeSelf != active)
            {
                panel.gameObject.SetActive(active);
            }
        }

        private void BuildUi()
        {
            var canvasObject = new GameObject(
                PanelName + "Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = CanvasSortingOrder;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            panel = UiFactory.Create(PanelName, canvasObject.transform);
            panel.anchorMin = new Vector2(0.5f, 1f);
            panel.anchorMax = new Vector2(0.5f, 1f);
            panel.pivot = new Vector2(0.5f, 1f);
            panel.anchoredPosition = new Vector2(0f, -TopOffset);
            panel.sizeDelta = new Vector2(Width, 0f);
            var frame = panel.gameObject.AddComponent<Image>();
            UiFactory.SetSprite(
                frame,
                theme.DialogFrame != null ? theme.DialogFrame : theme.WindowFrame,
                theme.WindowTint);
            if (frame.sprite == null)
            {
                frame.color = new Color(0f, 0f, 0f, 0.7f);
            }

            frame.raycastTarget = false;
            var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            var padding = Mathf.RoundToInt(theme.WindowPadding);
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            panel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            line = UiFactory.BodyText(
                theme,
                panel,
                "Line",
                theme.BodySize,
                theme.TextColor,
                TextAlignmentOptions.Center);
            line.textWrappingMode = TextWrappingModes.Normal;
            line.text = texts.Get(LineKey);
            panel.gameObject.SetActive(false);
        }
    }
}
