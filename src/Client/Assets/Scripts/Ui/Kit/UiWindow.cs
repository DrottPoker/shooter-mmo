using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // A themed window: a framed panel with a header (icon, title, detail, close button), a line,
    // a body that grows with its content up to a maximum and scrolls beyond it, and an optional
    // footer. It sits at a fixed place on the screen; its height follows its content. The frame
    // takes every click and scroll over the window, so nothing reaches the world below it. On the
    // window manager's stack it has a canvas of its own, which the manager sorts, and Escape
    // closes it the way its close button does.
    [DisallowMultipleComponent]
    public sealed class UiWindow : MonoBehaviour
    {
        // The smallest share of its size that a long title shrinks to before it is cut short.
        public const float TitleFitFloor = 0.7f;

        private static readonly Color FallbackPanelColor = new Color(0.06f, 0.09f, 0.10f, 0.96f);

        private UiTheme theme;
        private RectTransform scroll;
        private LayoutElement scrollLayout;
        private float maximumBodyHeight = float.MaxValue;
        private UiStackEntry entry;

        // Raised by the close button and by an Escape that closes the window; the owner closes it.
        public event Action CloseRequested;

        public RectTransform Rect { get; private set; }

        // Where the window's content goes, a vertical layout that grows with its children.
        public RectTransform Body { get; private set; }

        // A row below the body, empty and hidden until something is added.
        public RectTransform Footer { get; private set; }

        public TextMeshProUGUI Title { get; private set; }

        public TextMeshProUGUI Detail { get; private set; }

        public Image Icon { get; private set; }

        public bool IsOpen
        {
            get { return gameObject.activeSelf; }
        }

        public static UiWindow Create(
            UiTheme theme,
            Transform parent,
            string name,
            Sprite icon,
            string title,
            float width)
        {
            var root = UiFactory.Create(name, parent);
            var window = root.gameObject.AddComponent<UiWindow>();
            window.Build(theme, icon, title, width);
            return window;
        }

        // Anchors the window to a point of the screen, such as the top right (1, 1), with its
        // own corner there, then moves it by offset.
        public void Place(Vector2 anchor, Vector2 offset)
        {
            Rect.anchorMin = anchor;
            Rect.anchorMax = anchor;
            Rect.pivot = anchor;
            Rect.anchoredPosition = offset;
        }

        // Puts the window on the manager's stack in a layer, on a canvas of its own.
        public void Attach(UiWindowManager manager, UiLayer layer)
        {
            if (manager == null)
            {
                throw new ArgumentNullException(nameof(manager));
            }

            var canvas = UiCanvas.AddNested(gameObject, UiLayers.StackedOrder(layer, 0), true);
            entry = manager.Register(name, layer, canvas, () => CloseRequested?.Invoke());
            entry.SetOpen(IsOpen);
        }

        public void SetOpen(bool open)
        {
            if (gameObject.activeSelf != open)
            {
                gameObject.SetActive(open);
            }

            entry?.SetOpen(open);
        }

        // Brings the open window to the top of its layer, as a click on it does.
        public void Focus()
        {
            entry?.Focus();
        }

        private void OnDestroy()
        {
            entry?.Remove();
            entry = null;
        }

        public void SetTitle(string value)
        {
            UiFactory.SetHeading(Title, value);
        }

        public void SetDetail(string value)
        {
            Detail.text = value ?? string.Empty;
        }

        // The tallest the body grows before it scrolls.
        public void SetMaximumBodyHeight(float height)
        {
            maximumBodyHeight = Mathf.Max(theme.SlotSize, height);
        }

        // Sizes the body to its content, at most the maximum, after the content changed. Text
        // measures only while active, so the window must be open.
        public void FitBody()
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(Body);
            var contentHeight = LayoutUtility.GetPreferredHeight(Body);
            var height = Mathf.Min(contentHeight, maximumBodyHeight);
            scrollLayout.preferredHeight = height;
            scrollLayout.minHeight = height;
            // Cleared children stay until the frame ends, hidden; only shown ones count.
            var footerShown = false;
            foreach (Transform child in Footer)
            {
                footerShown |= child.gameObject.activeSelf;
            }

            Footer.gameObject.SetActive(footerShown);
            LayoutRebuilder.ForceRebuildLayoutImmediate(Rect);
        }

        // A separator line draws at its sprite's height, so its thin line keeps its thickness.
        public static float SeparatorHeight(UiTheme theme)
        {
            return theme.Separator != null ? theme.Separator.rect.height : 1f;
        }

        private void Build(UiTheme windowTheme, Sprite icon, string title, float width)
        {
            theme = windowTheme;
            Rect = (RectTransform)transform;
            Rect.sizeDelta = new Vector2(width, 0f);
            var frame = gameObject.AddComponent<Image>();
            if (theme.WindowFrame != null)
            {
                UiFactory.SetSprite(frame, theme.WindowFrame, theme.WindowTint);
            }
            else
            {
                frame.color = FallbackPanelColor;
            }

            frame.raycastTarget = true;
            var layout = gameObject.AddComponent<VerticalLayoutGroup>();
            var padding = Mathf.RoundToInt(theme.WindowPadding);
            layout.padding = new RectOffset(padding, padding, 4, padding);
            layout.spacing = 0f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var header = UiFactory.Row(transform, "Header", theme.HeaderHeight, 10f);
            header.childForceExpandHeight = false;
            Icon = UiFactory.Image(header.transform, "Icon", icon, theme.TitleColor);
            UiFactory.Layout(Icon, theme.HeaderHeight * 0.55f, theme.HeaderHeight * 0.55f, 0f);
            Icon.gameObject.SetActive(icon != null);
            // The title takes only the room that the icon, the detail, and the close button leave.
            // A long one shrinks to TitleFitFloor of its size and ends with an ellipsis beyond
            // that, so it never draws over the detail or the close button.
            Title = UiFactory.Heading(theme, header.transform, "Title", theme.TitleSize, TextAlignmentOptions.MidlineLeft, title);
            var titleLayout = UiFactory.Layout(Title, preferredWidth: 0f, flexibleWidth: 1f);
            titleLayout.minWidth = 0f;
            Title.enableAutoSizing = true;
            Title.fontSizeMax = theme.TitleSize;
            Title.fontSizeMin = Mathf.Round(theme.TitleSize * TitleFitFloor);
            Title.overflowMode = TextOverflowModes.Ellipsis;
            Detail = UiFactory.BodyText(theme, header.transform, "Detail", theme.BodySize, theme.TextColor, TextAlignmentOptions.MidlineRight);
            var close = UiFactory.IconButton(theme, header.transform, "Close", theme.CloseIcon, theme.HeaderHeight * 0.62f, () => CloseRequested?.Invoke());
            if (theme.CloseIcon == null)
            {
                var label = UiFactory.BodyText(theme, close.transform, "X", theme.BodySize, theme.TitleColor, TextAlignmentOptions.Center, "X");
                UiFactory.Fill(label.rectTransform);
            }

            var line = UiFactory.Image(transform, "Separator", theme.Separator, theme.SeparatorColor);
            line.type = Image.Type.Simple;
            line.preserveAspect = false;
            UiFactory.Layout(line, preferredHeight: SeparatorHeight(theme));

            var gap = UiFactory.Create("Gap", transform);
            UiFactory.Layout(gap, preferredHeight: theme.SectionSpacing);

            scroll = UiFactory.Create("Scroll", transform);
            scrollLayout = UiFactory.Layout(scroll, preferredHeight: 0f);
            var scrollRect = scroll.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 30f;
            var viewport = UiFactory.Create("Viewport", scroll);
            UiFactory.Fill(viewport);
            viewport.gameObject.AddComponent<RectMask2D>();
            var viewportImage = viewport.gameObject.AddComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0f);
            viewportImage.raycastTarget = true;
            scrollRect.viewport = viewport;
            Body = UiFactory.Column(viewport, "Body", theme.SectionSpacing).GetComponent<RectTransform>();
            Body.anchorMin = new Vector2(0f, 1f);
            Body.anchorMax = new Vector2(1f, 1f);
            Body.pivot = new Vector2(0.5f, 1f);
            Body.offsetMin = Vector2.zero;
            Body.offsetMax = Vector2.zero;
            var bodyFitter = Body.gameObject.AddComponent<ContentSizeFitter>();
            bodyFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollRect.content = Body;

            var footerRow = UiFactory.Row(transform, "Footer", theme.ButtonHeight + theme.SectionSpacing, 8f);
            footerRow.padding = new RectOffset(0, 0, Mathf.RoundToInt(theme.SectionSpacing), 0);
            Footer = footerRow.GetComponent<RectTransform>();
            Footer.gameObject.SetActive(false);
        }
    }
}
