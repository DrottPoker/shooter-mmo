using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    public enum UiButtonTone
    {
        Normal,
        Primary,
        Danger
    }

    // Builders for the themed widgets of the UI kit. Every element draws its sprites, fonts,
    // and colors from a UiTheme, and every text is TextMesh Pro.
    public static class UiFactory
    {
        public static RectTransform Create(string name, Transform parent)
        {
            var value = new GameObject(name, typeof(RectTransform));
            value.transform.SetParent(parent, false);
            return (RectTransform)value.transform;
        }

        public static void Stretch(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        // Fills the parent, inset by the given distance on every side.
        public static void Fill(RectTransform rect, float inset = 0f)
        {
            Stretch(rect, Vector2.zero, Vector2.one, new Vector2(inset, inset), new Vector2(-inset, -inset));
        }

        // A rect of a fixed size, placed at a position from the parent's top-left corner.
        public static void Place(RectTransform rect, Vector2 topLeft, Vector2 size)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(topLeft.x, -topLeft.y);
            rect.sizeDelta = size;
        }

        // An image of a sprite; a sprite with borders draws nine-sliced. Images take no clicks
        // unless asked, so text and decoration never block the slot below them.
        public static Image Image(
            Transform parent,
            string name,
            Sprite sprite,
            Color color,
            bool raycastTarget = false)
        {
            var image = Create(name, parent).gameObject.AddComponent<Image>();
            SetSprite(image, sprite, color);
            image.raycastTarget = raycastTarget;
            return image;
        }

        public static void SetSprite(Image image, Sprite sprite, Color color)
        {
            image.sprite = sprite;
            image.color = sprite != null ? color : new Color(color.r, color.g, color.b, 0f);
            image.type = sprite != null && sprite.border != Vector4.zero
                ? UnityEngine.UI.Image.Type.Sliced
                : UnityEngine.UI.Image.Type.Simple;
            image.preserveAspect = sprite != null && sprite.border == Vector4.zero;
        }

        public static TextMeshProUGUI Text(
            Transform parent,
            string name,
            TMP_FontAsset font,
            float size,
            Color color,
            TextAlignmentOptions alignment,
            string value = "")
        {
            var text = Create(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null)
            {
                text.font = font;
            }

            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.richText = true;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            text.text = value ?? string.Empty;
            return text;
        }

        public static TextMeshProUGUI BodyText(
            UiTheme theme,
            Transform parent,
            string name,
            float size,
            Color color,
            TextAlignmentOptions alignment,
            string value = "")
        {
            return Text(parent, name, theme.BodyFont, size, color, alignment, value);
        }

        // A window title or section heading: the title font in capitals with wide spacing.
        public static TextMeshProUGUI Heading(
            UiTheme theme,
            Transform parent,
            string name,
            float size,
            TextAlignmentOptions alignment,
            string value = "")
        {
            var text = Text(parent, name, theme.TitleFont, size, theme.TitleColor, alignment);
            text.characterSpacing = theme.TitleCharacterSpacing;
            SetHeading(text, value);
            return text;
        }

        // The text itself holds the capitals, so layout measures the width it draws; a capitals
        // style would be measured in the font's narrower small capitals.
        public static void SetHeading(TextMeshProUGUI text, string value)
        {
            text.text = (value ?? string.Empty).ToUpperInvariant();
        }

        public static LayoutElement Layout(
            Component target,
            float preferredWidth = -1f,
            float preferredHeight = -1f,
            float flexibleWidth = -1f)
        {
            if (!target.TryGetComponent<LayoutElement>(out var layout))
            {
                layout = target.gameObject.AddComponent<LayoutElement>();
            }

            layout.preferredWidth = preferredWidth;
            layout.preferredHeight = preferredHeight;
            layout.minHeight = preferredHeight;
            layout.flexibleWidth = flexibleWidth;
            return layout;
        }

        public static HorizontalLayoutGroup Row(Transform parent, string name, float height, float spacing)
        {
            var row = Create(name, parent);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            Layout(row, preferredHeight: height);
            return layout;
        }

        public static VerticalLayoutGroup Column(Transform parent, string name, float spacing)
        {
            var column = Create(name, parent);
            var layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return layout;
        }

        // A flexible spacer in a row or column.
        public static RectTransform Spacer(Transform parent)
        {
            var spacer = Create("Spacer", parent);
            var layout = spacer.gameObject.AddComponent<LayoutElement>();
            layout.flexibleWidth = 1f;
            layout.flexibleHeight = 1f;
            return spacer;
        }

        // A section heading with a line that runs to the detail on the right, such as
        // "PERMANENT INVENTORY -------- 8 / 24".
        public static TextMeshProUGUI SectionHeading(
            UiTheme theme,
            Transform parent,
            string title,
            Sprite icon,
            out TextMeshProUGUI detail)
        {
            var row = Row(parent, "Section " + title, theme.SectionSize + 12f, 8f);
            if (icon != null)
            {
                var image = Image(row.transform, "Icon", icon, theme.TitleColor);
                Layout(image, preferredWidth: theme.SectionSize + 4f, preferredHeight: theme.SectionSize + 4f);
            }

            var heading = Heading(theme, row.transform, "Title", theme.SectionSize, TextAlignmentOptions.MidlineLeft, title);
            Layout(heading, preferredWidth: -1f);
            var line = Image(row.transform, "Line", theme.Separator, theme.SeparatorColor);
            line.type = UnityEngine.UI.Image.Type.Simple;
            line.preserveAspect = false;
            // The line takes only the room left between title and detail; its sprite's own width
            // would squeeze the title.
            var lineLayout = Layout(line, 0f, UiWindow.SeparatorHeight(theme), 1f);
            lineLayout.minWidth = 0f;
            lineLayout.preferredHeight = UiWindow.SeparatorHeight(theme);
            lineLayout.minHeight = lineLayout.preferredHeight;
            row.childForceExpandHeight = false;
            row.childAlignment = TextAnchor.MiddleLeft;
            detail = BodyText(theme, row.transform, "Detail", theme.LabelSize, theme.TextColor, TextAlignmentOptions.MidlineRight);
            return heading;
        }

        public static Button Button(
            UiTheme theme,
            Transform parent,
            string label,
            Action onClick,
            UiButtonTone tone = UiButtonTone.Normal,
            float preferredWidth = -1f)
        {
            var root = Create(label + " Button", parent);
            var image = root.gameObject.AddComponent<Image>();
            SetSprite(image, theme.ButtonFrame, ButtonTint(theme, tone));
            image.raycastTarget = true;
            var button = root.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.15f, 1.12f, 1.05f, 1f);
            colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.8f);
            colors.colorMultiplier = 1f;
            button.colors = colors;
            if (onClick != null)
            {
                button.onClick.AddListener(() => onClick());
            }

            var text = BodyText(
                theme,
                root,
                "Label",
                theme.LabelSize,
                tone == UiButtonTone.Danger ? theme.DangerColor : theme.TextColor,
                TextAlignmentOptions.Center,
                label);
            text.fontStyle = FontStyles.Bold;
            Fill(text.rectTransform, 4f);
            var layout = Layout(root, preferredWidth, theme.ButtonHeight, preferredWidth > 0f ? 0f : 1f);
            layout.minWidth = preferredWidth > 0f ? preferredWidth : 60f;
            return button;
        }

        public static void SetButtonLabel(Button button, string label)
        {
            var text = button != null ? button.GetComponentInChildren<TextMeshProUGUI>(true) : null;
            if (text != null)
            {
                text.text = label ?? string.Empty;
            }
        }

        // A square button that shows only an icon, such as a window's close button.
        public static Button IconButton(UiTheme theme, Transform parent, string name, Sprite icon, float size, Action onClick)
        {
            var root = Create(name, parent);
            var hit = root.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);
            hit.raycastTarget = true;
            var image = Image(root, "Icon", icon, theme.TitleColor);
            Fill(image.rectTransform, size * 0.18f);
            var button = root.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.2f, 1.1f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            button.colors = colors;
            if (onClick != null)
            {
                button.onClick.AddListener(() => onClick());
            }

            Layout(root, size, size, 0f);
            root.sizeDelta = new Vector2(size, size);
            return button;
        }

        // A single-line field: its text and placeholder sit in a masked text area inset from
        // the edges, which also bounds the caret and the selection. An icon sits at its left.
        public static TMP_InputField Field(
            UiTheme theme,
            Transform parent,
            string name,
            string value,
            string placeholder,
            Sprite icon,
            TMP_InputField.ContentType contentType,
            TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft)
        {
            var root = Create(name, parent);
            var frame = root.gameObject.AddComponent<Image>();
            SetSprite(frame, theme.FieldFrame, theme.WindowTint);
            frame.raycastTarget = true;
            var left = 10f;
            if (icon != null)
            {
                var image = Image(root, "Icon", icon, theme.MutedTextColor);
                var side = theme.FieldHeight - 14f;
                Stretch(image.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(10f, -side / 2f), new Vector2(10f + side, side / 2f));
                left = 18f + side;
            }

            var area = Create("Text Area", root);
            Stretch(area, Vector2.zero, Vector2.one, new Vector2(left, 2f), new Vector2(-10f, -2f));
            area.gameObject.AddComponent<RectMask2D>();
            var input = root.gameObject.AddComponent<TMP_InputField>();
            input.textViewport = area;
            if (placeholder != null)
            {
                var hint = BodyText(theme, area, "Placeholder", theme.BodySize, theme.MutedTextColor, alignment, placeholder);
                Fill(hint.rectTransform);
                hint.richText = false;
                input.placeholder = hint;
            }

            var text = BodyText(theme, area, "Text", theme.BodySize, theme.TextColor, alignment);
            Fill(text.rectTransform);
            text.richText = false;
            input.textComponent = text;
            input.fontAsset = theme.BodyFont;
            input.pointSize = theme.BodySize;
            input.contentType = contentType;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.customCaretColor = true;
            input.caretColor = theme.TextColor;
            input.caretWidth = 2;
            input.selectionColor = new Color(theme.AccentColor.r, theme.AccentColor.g, theme.AccentColor.b, 0.35f);
            input.richText = false;
            input.text = value ?? string.Empty;
            Layout(root, preferredHeight: theme.FieldHeight, flexibleWidth: 1f);
            return input;
        }

        // Hidden children leave layout at once; destruction itself happens at frame end.
        public static void ClearChildren(Transform parent)
        {
            if (parent == null)
            {
                return;
            }

            for (var index = parent.childCount - 1; index >= 0; index--)
            {
                var child = parent.GetChild(index).gameObject;
                child.SetActive(false);
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(child);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(child);
                }
            }
        }

        // A rich-text color tag, such as <color=#45D9DBFF>.
        public static string ColorTag(Color color)
        {
            return "#" + ColorUtility.ToHtmlStringRGBA(color);
        }

        private static Color ButtonTint(UiTheme theme, UiButtonTone tone)
        {
            return tone switch
            {
                UiButtonTone.Primary => new Color(1f, 0.95f, 0.82f, 1f),
                UiButtonTone.Danger => new Color(1f, 0.78f, 0.74f, 1f),
                _ => theme.WindowTint
            };
        }
    }
}
