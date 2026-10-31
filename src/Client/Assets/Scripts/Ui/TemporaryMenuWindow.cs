using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // A code-built window of the temporary Debug and Admin menus: a header with the title, the
    // key that toggles it, and a close button; a row of tabs; one scrolling page per tab; and a
    // footer with a status line and optional buttons. Pages are built once and keep the Text,
    // Toggle, and Button components that change, so an update sets text and lays out nothing.
    public sealed class TemporaryMenuWindow
    {
        // Above the inventory, below the world context menu.
        public const int CanvasSortingOrder = TemporaryInventoryPanel.CanvasSortingOrder + 20;

        public const float LabelWidth = 150f;

        private const float HeaderHeight = 40f;
        private const float TabsHeight = 34f;
        private const float StatusHeight = 38f;
        private const float FooterButtonHeight = 30f;
        private const float Padding = 10f;
        private const float RowHeight = 22f;

        private readonly List<Button> tabButtons = new List<Button>();
        private readonly List<RectTransform> pages = new List<RectTransform>();
        private GameObject canvasObject;
        private RectTransform window;
        private RectTransform tabs;
        private RectTransform footerButtons;
        private ScrollRect scroll;
        private Text status;

        private TemporaryMenuWindow()
        {
        }

        // Raised when a tab is chosen, with its index.
        public event Action<int> TabSelected;

        public int SelectedTab { get; private set; } = -1;

        public int TabCount
        {
            get { return pages.Count; }
        }

        public bool IsVisible
        {
            get { return canvasObject != null && canvasObject.activeSelf; }
        }

        public RectTransform Root
        {
            get { return window; }
        }

        public string StatusText
        {
            get { return status != null ? status.text : string.Empty; }
        }

        // A window anchored at a corner of the screen, such as (1, 1) for the top right; the
        // offset moves it from that corner. The close button calls close.
        public static TemporaryMenuWindow Create(
            Transform parent,
            string name,
            string title,
            string keyHint,
            Vector2 anchor,
            Vector2 offset,
            Vector2 size,
            bool withFooterButtons,
            Action close)
        {
            TemporaryUiKit.EnsureEventSystem();
            var menu = new TemporaryMenuWindow();
            menu.canvasObject = new GameObject(
                name + "Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            menu.canvasObject.transform.SetParent(parent, false);
            var canvas = menu.canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = CanvasSortingOrder;
            var scaler = menu.canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            menu.window = TemporaryUiKit.CreateUiObject(name, menu.canvasObject.transform).GetComponent<RectTransform>();
            menu.window.anchorMin = anchor;
            menu.window.anchorMax = anchor;
            menu.window.pivot = anchor;
            menu.window.anchoredPosition = offset;
            menu.window.sizeDelta = size;
            var background = menu.window.gameObject.AddComponent<Image>();
            background.color = TemporaryUiKit.PanelColor;
            TemporaryUiKit.AddOutline(background, TemporaryUiKit.PanelBorderColor);

            menu.BuildHeader(title, keyHint, close);
            menu.BuildTabs();
            var footerHeight = menu.BuildFooter(withFooterButtons);
            menu.BuildScroll(footerHeight);
            return menu;
        }

        public void SetVisible(bool visible)
        {
            if (canvasObject != null)
            {
                canvasObject.SetActive(visible);
            }
        }

        public void Destroy()
        {
            if (canvasObject != null)
            {
                UnityEngine.Object.Destroy(canvasObject);
                canvasObject = null;
            }
        }

        // Adds a tab and returns its page, a vertical layout that grows with its rows.
        public RectTransform AddTab(string label)
        {
            var index = pages.Count;
            var button = TemporaryUiKit.CreateButton(
                tabs,
                label,
                () => SelectTab(index),
                true,
                TemporaryButtonTone.Quiet);
            tabButtons.Add(button);

            var page = TemporaryUiKit.CreateUiObject(label + "Page", scroll.viewport).GetComponent<RectTransform>();
            page.anchorMin = new Vector2(0f, 1f);
            page.anchorMax = new Vector2(1f, 1f);
            page.pivot = new Vector2(0.5f, 1f);
            page.offsetMin = Vector2.zero;
            page.offsetMax = Vector2.zero;
            var layout = page.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(2, 2, 4, 8);
            layout.spacing = 4f;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            page.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            page.gameObject.SetActive(false);
            pages.Add(page);
            if (SelectedTab < 0)
            {
                SelectTab(0);
            }

            return page;
        }

        public void SelectTab(int index)
        {
            if (index < 0 || index >= pages.Count)
            {
                return;
            }

            var changed = index != SelectedTab;
            SelectedTab = index;
            for (var tab = 0; tab < pages.Count; tab++)
            {
                pages[tab].gameObject.SetActive(tab == index);
                TemporaryUiKit.SetButtonTone(
                    tabButtons[tab],
                    tab == index ? TemporaryButtonTone.Active : TemporaryButtonTone.Quiet);
            }

            scroll.content = pages[index];
            scroll.verticalNormalizedPosition = 1f;
            if (changed)
            {
                TabSelected?.Invoke(index);
            }
        }

        public void SetStatus(string text, Color color)
        {
            if (status == null)
            {
                return;
            }

            status.text = text ?? string.Empty;
            status.color = color;
        }

        // A button in the footer, beside the status line.
        public Button AddFooterButton(string label, Action onClick, TemporaryButtonTone tone, float width = -1f)
        {
            return footerButtons != null
                ? TemporaryUiKit.CreateButton(footerButtons, label, onClick, true, tone, width)
                : null;
        }

        // A small heading that groups the rows below it.
        public static Text AddSection(RectTransform page, string title)
        {
            var label = TemporaryUiKit.CreateLabel(
                page,
                title.ToUpperInvariant(),
                12,
                FontStyle.Bold,
                TemporaryUiKit.AccentColor,
                RowHeight);
            label.alignment = TextAnchor.LowerLeft;
            label.GetComponent<LayoutElement>().minHeight = RowHeight + 6f;
            return label;
        }

        // A row with a label and a value beside it; returns the value text to update.
        public static Text AddValue(RectTransform page, string label)
        {
            var row = TemporaryUiKit.CreateRow(page, RowHeight, 6f);
            row.GetComponent<LayoutElement>().minHeight = RowHeight;
            var name = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Label", row),
                label,
                14,
                FontStyle.Normal,
                TextAnchor.UpperLeft,
                TemporaryUiKit.TextSecondary);
            var nameLayout = name.gameObject.AddComponent<LayoutElement>();
            nameLayout.preferredWidth = LabelWidth;
            nameLayout.flexibleWidth = 0f;
            var value = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Value", row),
                "-",
                14,
                FontStyle.Normal,
                TextAnchor.UpperLeft,
                TemporaryUiKit.TextPrimary);
            value.horizontalOverflow = HorizontalWrapMode.Wrap;
            value.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            // Wrapped values make the row taller.
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childForceExpandHeight = false;
            row.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return value;
        }

        // A checkbox row; changed hears every click.
        public static Toggle AddToggle(RectTransform page, string label, bool value, Action<bool> changed)
        {
            var row = TemporaryUiKit.CreateRow(page, 26f, 8f);
            var toggle = row.gameObject.AddComponent<Toggle>();
            var box = TemporaryUiKit.CreateUiObject("Box", row);
            var boxImage = box.AddComponent<Image>();
            boxImage.color = TemporaryUiKit.InputColor;
            TemporaryUiKit.AddOutline(boxImage, TemporaryUiKit.PanelBorderColor);
            var boxLayout = box.AddComponent<LayoutElement>();
            boxLayout.preferredWidth = 18f;
            boxLayout.flexibleWidth = 0f;
            var check = TemporaryUiKit.CreateUiObject("Check", box.transform).AddComponent<Image>();
            check.color = TemporaryUiKit.AccentColor;
            check.raycastTarget = false;
            TemporaryUiKit.Stretch(check.rectTransform, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -4f));
            var text = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Label", row),
                label,
                14,
                FontStyle.Normal,
                TextAnchor.MiddleLeft,
                TemporaryUiKit.TextPrimary);
            text.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            // The label takes clicks too, so the whole row toggles.
            text.raycastTarget = true;
            row.GetComponent<HorizontalLayoutGroup>().childForceExpandHeight = false;
            boxLayout.preferredHeight = 18f;
            toggle.targetGraphic = boxImage;
            toggle.graphic = check;
            toggle.SetIsOnWithoutNotify(value);
            if (changed != null)
            {
                toggle.onValueChanged.AddListener(isOn => changed(isOn));
            }

            return toggle;
        }

        // A row that steps a value back and forth; returns the value text to update.
        public static Text AddStepper(RectTransform page, string label, Action<int> step)
        {
            var row = TemporaryUiKit.CreateRow(page, 28f, 6f);
            var name = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Label", row),
                label,
                14,
                FontStyle.Normal,
                TextAnchor.MiddleLeft,
                TemporaryUiKit.TextSecondary);
            var nameLayout = name.gameObject.AddComponent<LayoutElement>();
            nameLayout.preferredWidth = LabelWidth;
            nameLayout.flexibleWidth = 0f;
            TemporaryUiKit.CreateButton(row, "<", () => step(-1), true, TemporaryButtonTone.Normal, 30f);
            var value = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Value", row),
                "-",
                14,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TemporaryUiKit.TextPrimary);
            value.horizontalOverflow = HorizontalWrapMode.Wrap;
            value.resizeTextForBestFit = true;
            value.resizeTextMinSize = 10;
            value.resizeTextMaxSize = 14;
            value.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            TemporaryUiKit.CreateButton(row, ">", () => step(1), true, TemporaryButtonTone.Normal, 30f);
            return value;
        }

        // A row for buttons that share its width; add them with TemporaryUiKit.CreateButton.
        public static RectTransform AddButtonRow(RectTransform page)
        {
            return TemporaryUiKit.CreateRow(page, 30f, 6f);
        }

        // A wrapping line of quiet text, such as how to use a tool.
        public static Text AddNote(RectTransform page, string text)
        {
            return TemporaryUiKit.CreateLabel(page, text, 13, FontStyle.Italic, TemporaryUiKit.TextMuted, 18f);
        }

        private void BuildHeader(string title, string keyHint, Action close)
        {
            var header = TemporaryUiKit.CreateUiObject("Header", window).GetComponent<RectTransform>();
            TemporaryUiKit.Stretch(header, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -HeaderHeight), Vector2.zero);
            var image = header.gameObject.AddComponent<Image>();
            image.color = TemporaryUiKit.HeaderColor;
            var titleText = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Title", header),
                title,
                18,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                TemporaryUiKit.TextPrimary);
            TemporaryUiKit.Stretch(titleText.rectTransform, Vector2.zero, Vector2.one, new Vector2(12f, 0f), new Vector2(-80f, 0f));
            var hint = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Key", header),
                keyHint,
                13,
                FontStyle.Normal,
                TextAnchor.MiddleRight,
                TemporaryUiKit.TextMuted);
            TemporaryUiKit.Stretch(hint.rectTransform, Vector2.zero, Vector2.one, new Vector2(12f, 0f), new Vector2(-44f, 0f));
            var closeButton = TemporaryUiKit.CreateButton(header, "X", close, true, TemporaryButtonTone.Quiet);
            var closeRect = closeButton.GetComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(1f, 0.5f);
            closeRect.anchorMax = new Vector2(1f, 0.5f);
            closeRect.pivot = new Vector2(1f, 0.5f);
            closeRect.anchoredPosition = new Vector2(-8f, 0f);
            closeRect.sizeDelta = new Vector2(28f, 26f);
        }

        private void BuildTabs()
        {
            tabs = TemporaryUiKit.CreateUiObject("Tabs", window).GetComponent<RectTransform>();
            TemporaryUiKit.Stretch(
                tabs,
                new Vector2(0f, 1f),
                Vector2.one,
                new Vector2(Padding, -(HeaderHeight + 6f + TabsHeight)),
                new Vector2(-Padding, -(HeaderHeight + 6f)));
            var layout = tabs.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 4f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
        }

        private float BuildFooter(bool withButtons)
        {
            var height = StatusHeight + (withButtons ? FooterButtonHeight + 6f : 0f) + 2f * 8f;
            var footer = TemporaryUiKit.CreateUiObject("Footer", window).GetComponent<RectTransform>();
            TemporaryUiKit.Stretch(footer, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, height));
            var image = footer.gameObject.AddComponent<Image>();
            image.color = TemporaryUiKit.HeaderColor;
            image.raycastTarget = false;
            status = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Status", footer),
                string.Empty,
                13,
                FontStyle.Normal,
                TextAnchor.UpperLeft,
                TemporaryUiKit.TextSecondary);
            status.horizontalOverflow = HorizontalWrapMode.Wrap;
            status.verticalOverflow = VerticalWrapMode.Truncate;
            TemporaryUiKit.Stretch(
                status.rectTransform,
                new Vector2(0f, 1f),
                Vector2.one,
                new Vector2(Padding, -(8f + StatusHeight)),
                new Vector2(-Padding, -8f));
            if (withButtons)
            {
                footerButtons = TemporaryUiKit.CreateRow(footer, FooterButtonHeight, 6f);
                TemporaryUiKit.Stretch(
                    footerButtons,
                    Vector2.zero,
                    new Vector2(1f, 0f),
                    new Vector2(Padding, 8f),
                    new Vector2(-Padding, 8f + FooterButtonHeight));
            }

            return height;
        }

        private void BuildScroll(float footerHeight)
        {
            var scrollObject = TemporaryUiKit.CreateUiObject("Scroll", window);
            TemporaryUiKit.Stretch(
                scrollObject.GetComponent<RectTransform>(),
                Vector2.zero,
                Vector2.one,
                new Vector2(Padding, footerHeight + 6f),
                new Vector2(-Padding, -(HeaderHeight + 6f + TabsHeight + 6f)));
            scroll = scrollObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;
            var viewport = TemporaryUiKit.CreateUiObject("Viewport", scrollObject.transform).GetComponent<RectTransform>();
            TemporaryUiKit.Stretch(viewport, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            // The viewport takes the scroll wheel and keeps clicks from reaching the world.
            viewport.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
            viewport.gameObject.AddComponent<RectMask2D>();
            scroll.viewport = viewport;
        }
    }
}
