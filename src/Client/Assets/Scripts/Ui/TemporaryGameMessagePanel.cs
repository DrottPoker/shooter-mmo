using Mmorpg.Client.Gameplay;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // Temporary presentation of game messages, such as Examine descriptions, in the bottom
    // left corner where a chat box would sit. Each message fades out after a while and the
    // panel never takes a click.
    [DisallowMultipleComponent]
    public sealed class TemporaryGameMessagePanel : MonoBehaviour
    {
        // Below the world interaction panel.
        public const int CanvasSortingOrder = TemporaryWorldInteractionPanel.CanvasSortingOrder - 5;
        public const float VisibleSeconds = 12f;
        public const float FadeSeconds = 2f;

        private const int VisibleMessages = 5;

        private readonly Text[] lines = new Text[VisibleMessages];
        private GameMessageLog messages;
        private RectTransform panel;
        private bool dirty = true;

        // The texts of the messages that are visible now, oldest first.
        public string[] VisibleTexts()
        {
            var count = 0;
            for (var index = 0; index < lines.Length; index++)
            {
                if (lines[index] != null && lines[index].gameObject.activeSelf)
                {
                    count++;
                }
            }

            var texts = new string[count];
            var next = 0;
            for (var index = 0; index < lines.Length; index++)
            {
                if (lines[index] != null && lines[index].gameObject.activeSelf)
                {
                    texts[next++] = lines[index].text;
                }
            }

            return texts;
        }

        // Called by the composition root before the component is activated.
        public void Initialize(GameMessageLog gameMessages)
        {
            messages = gameMessages;
        }

        // How visible a message of this age is, from 1 to 0.
        public static float Opacity(float ageSeconds)
        {
            if (ageSeconds <= VisibleSeconds)
            {
                return 1f;
            }

            return Mathf.Clamp01(1f - ((ageSeconds - VisibleSeconds) / FadeSeconds));
        }

        private void Start()
        {
            BuildUi();
            if (messages != null)
            {
                messages.Changed += OnMessagesChanged;
            }
        }

        private void OnDestroy()
        {
            if (messages != null)
            {
                messages.Changed -= OnMessagesChanged;
            }
        }

        private void OnMessagesChanged()
        {
            dirty = true;
        }

        private void Update()
        {
            if (messages == null || panel == null)
            {
                return;
            }

            var all = messages.Messages;
            var first = Mathf.Max(0, all.Count - VisibleMessages);
            var now = Time.unscaledTime;
            for (var index = 0; index < lines.Length; index++)
            {
                var messageIndex = first + index;
                var line = lines[index];
                if (messageIndex >= all.Count)
                {
                    SetActive(line, false);
                    continue;
                }

                var message = all[messageIndex];
                var opacity = Opacity(now - message.Time);
                SetActive(line, opacity > 0f);
                if (dirty)
                {
                    line.text = message.Text;
                }

                var color = line.color;
                if (!Mathf.Approximately(color.a, opacity))
                {
                    color.a = opacity;
                    line.color = color;
                }
            }

            dirty = false;
        }

        private static void SetActive(Text line, bool active)
        {
            if (line.gameObject.activeSelf != active)
            {
                line.gameObject.SetActive(active);
            }
        }

        private void BuildUi()
        {
            var canvasObject = new GameObject(
                "TemporaryGameMessageCanvas",
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

            var panelObject = TemporaryUiKit.CreateUiObject("GameMessages", canvasObject.transform);
            panel = panelObject.GetComponent<RectTransform>();
            panel.anchorMin = Vector2.zero;
            panel.anchorMax = Vector2.zero;
            panel.pivot = Vector2.zero;
            panel.anchoredPosition = new Vector2(18f, 18f);
            panel.sizeDelta = new Vector2(620f, 0f);
            var layout = panelObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 2f;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.LowerLeft;
            panelObject.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;
            for (var index = 0; index < lines.Length; index++)
            {
                var line = TemporaryUiKit.CreateLabel(
                    panel,
                    string.Empty,
                    16,
                    FontStyle.Normal,
                    TemporaryUiKit.TextPrimary,
                    22f);
                line.supportRichText = false;
                var shadow = line.gameObject.AddComponent<Shadow>();
                shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
                shadow.effectDistance = new Vector2(1.5f, -1.5f);
                line.gameObject.SetActive(false);
                lines[index] = line;
            }
        }
    }
}
