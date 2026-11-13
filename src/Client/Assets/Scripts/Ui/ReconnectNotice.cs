using System.Globalization;
using Mmorpg.Client.Networking;
using Mmorpg.WorldData.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The notice over the World while a lost connection is restored: that the connection was lost,
    // that the client reconnects, and the seconds left. It has no raycaster, so it never takes a
    // click and the camera still turns below it. The bootstrap adds it to every World scene.
    [DisallowMultipleComponent]
    public sealed class ReconnectNotice : MonoBehaviour
    {
        public const string PanelName = "ReconnectNotice";

        // Above every panel of the World but the context menu and the death overlay.
        public const int CanvasSortingOrder = 130;

        private const float Width = 460f;
        private const float TopOffset = 150f;

        private UiTheme theme;
        private IWorldReconnect reconnect;
        private TextCatalog texts = TextCatalog.Empty(TextTableFormat.SourceLocale);
        private RectTransform panel;
        private TextMeshProUGUI line;
        private int shownSeconds = -1;

        public bool IsVisible
        {
            get { return panel != null && panel.gameObject.activeSelf; }
        }

        public string LineText
        {
            get { return line != null ? line.text : string.Empty; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(UiTheme uiTheme, IWorldReconnect worldReconnect, TextCatalog textCatalog)
        {
            theme = uiTheme != null ? uiTheme : throw new System.ArgumentNullException(nameof(uiTheme));
            reconnect = worldReconnect;
            texts = textCatalog ?? TextCatalog.Empty(TextTableFormat.SourceLocale);
        }

        // The line for the seconds left of the window.
        public static string Line(TextCatalog texts, int secondsLeft)
        {
            return texts.Format(
                "ui/world/reconnecting",
                MessageArgument.Text("seconds", secondsLeft.ToString(CultureInfo.InvariantCulture)));
        }

        private void Awake()
        {
            BuildUi();
            Refresh();
        }

        private void Update()
        {
            Refresh();
        }

        private void Refresh()
        {
            var active = reconnect != null && reconnect.IsReconnecting;
            if (panel.gameObject.activeSelf != active)
            {
                panel.gameObject.SetActive(active);
            }

            if (!active)
            {
                shownSeconds = -1;
                return;
            }

            var seconds = reconnect.SecondsLeft;
            if (seconds != shownSeconds)
            {
                shownSeconds = seconds;
                line.text = Line(texts, seconds);
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
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            panel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            UiFactory.Heading(
                theme,
                panel,
                "Title",
                theme.SectionSize + 3f,
                TextAlignmentOptions.Center,
                texts.Get("ui/world/connection-lost"));
            line = UiFactory.BodyText(
                theme,
                panel,
                "Line",
                theme.BodySize,
                theme.TextColor,
                TextAlignmentOptions.Center);
            line.textWrappingMode = TextWrappingModes.Normal;
            panel.gameObject.SetActive(false);
        }
    }
}
