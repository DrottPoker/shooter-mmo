using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    public enum TemporaryButtonTone
    {
        Normal,
        Active,
        Danger,
        Quiet
    }

    // Shared colors and widget builders for the temporary code-built uGUI panels.
    public static class TemporaryUiKit
    {
        public static readonly Color BackdropColor = new Color(0.03f, 0.035f, 0.045f, 1f);
        public static readonly Color PanelColor = new Color(0.055f, 0.065f, 0.08f, 0.95f);
        public static readonly Color PanelBorderColor = new Color(0.24f, 0.28f, 0.34f, 0.9f);
        public static readonly Color HeaderColor = new Color(0.085f, 0.10f, 0.125f, 1f);
        public static readonly Color TextPrimary = new Color(0.93f, 0.94f, 0.96f, 1f);
        public static readonly Color TextSecondary = new Color(0.68f, 0.73f, 0.80f, 1f);
        public static readonly Color TextMuted = new Color(0.47f, 0.52f, 0.59f, 1f);
        public static readonly Color AccentColor = new Color(0.40f, 0.66f, 0.95f, 1f);
        public static readonly Color DangerColor = new Color(0.98f, 0.48f, 0.40f, 1f);
        public static readonly Color WarningColor = new Color(0.98f, 0.77f, 0.34f, 1f);
        public static readonly Color SuccessColor = new Color(0.46f, 0.80f, 0.52f, 1f);
        public static readonly Color ButtonColor = new Color(0.15f, 0.17f, 0.21f, 1f);
        public static readonly Color ButtonActiveColor = new Color(0.20f, 0.37f, 0.58f, 1f);
        public static readonly Color ButtonDangerColor = new Color(0.47f, 0.17f, 0.16f, 1f);
        public static readonly Color ButtonQuietColor = new Color(0.10f, 0.115f, 0.14f, 1f);
        public static readonly Color InputColor = new Color(0.025f, 0.03f, 0.04f, 1f);

        private static Font font;

        public static Font Font
        {
            get
            {
                if (font == null)
                {
                    font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                }

                return font;
            }
        }

        public static GameObject CreateUiObject(string name, Transform parent)
        {
            var value = new GameObject(name, typeof(RectTransform));
            value.transform.SetParent(parent, false);
            return value;
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

        public static Text AddText(
            GameObject target,
            string value,
            int fontSize,
            FontStyle style,
            TextAnchor alignment,
            Color color)
        {
            var text = target.AddComponent<Text>();
            text.font = Font;
            text.text = value ?? string.Empty;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.alignment = alignment;
            text.color = color;
            text.supportRichText = true;
            text.raycastTarget = false;
            return text;
        }

        // A wrapping text line inside a vertical layout.
        public static Text CreateLabel(
            Transform parent,
            string value,
            int fontSize,
            FontStyle style,
            Color color,
            float minHeight)
        {
            var label = AddText(
                CreateUiObject("Label", parent),
                value,
                fontSize,
                style,
                TextAnchor.MiddleLeft,
                color);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.gameObject.AddComponent<LayoutElement>().minHeight = minHeight;
            return label;
        }

        public static Button CreateButton(
            Transform parent,
            string label,
            Action onClick,
            bool interactable,
            TemporaryButtonTone tone,
            float preferredWidth = -1f)
        {
            var buttonObject = CreateUiObject(label + "Button", parent);
            var image = buttonObject.AddComponent<Image>();
            image.color = ButtonFill(tone);
            AddOutline(image, PanelBorderColor);
            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.interactable = interactable;
            if (onClick != null)
            {
                button.onClick.AddListener(() => onClick());
            }

            var text = AddText(
                CreateUiObject("Label", buttonObject.transform),
                label,
                14,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                interactable ? TextPrimary : TextMuted);
            Stretch(
                text.rectTransform,
                Vector2.zero,
                Vector2.one,
                new Vector2(6f, 2f),
                new Vector2(-6f, -2f));
            // In a row, a button without a width shares the remaining space.
            var layout = buttonObject.AddComponent<LayoutElement>();
            layout.preferredWidth = preferredWidth > 0f ? preferredWidth : -1f;
            layout.flexibleWidth = preferredWidth > 0f ? 0f : 1f;
            return button;
        }

        // Recolors a button built by CreateButton, such as the chosen one of a group.
        public static void SetButtonTone(Button button, TemporaryButtonTone tone)
        {
            if (button != null && button.targetGraphic != null)
            {
                button.targetGraphic.color = ButtonFill(tone);
            }
        }

        public static InputField CreateIntegerField(
            Transform parent,
            string value,
            float preferredWidth)
        {
            var inputObject = CreateUiObject("QuantityField", parent);
            var image = inputObject.AddComponent<Image>();
            image.color = InputColor;
            AddOutline(image, PanelBorderColor);
            var input = inputObject.AddComponent<InputField>();
            input.contentType = InputField.ContentType.IntegerNumber;
            input.lineType = InputField.LineType.SingleLine;
            input.customCaretColor = true;
            input.caretColor = TextPrimary;
            input.selectionColor = new Color(0.30f, 0.50f, 0.80f, 0.6f);
            var text = AddText(
                CreateUiObject("Text", inputObject.transform),
                value,
                15,
                FontStyle.Normal,
                TextAnchor.MiddleCenter,
                TextPrimary);
            Stretch(
                text.rectTransform,
                Vector2.zero,
                Vector2.one,
                new Vector2(6f, 2f),
                new Vector2(-6f, -2f));
            text.supportRichText = false;
            input.textComponent = text;
            input.text = value;
            var layout = inputObject.AddComponent<LayoutElement>();
            layout.preferredWidth = preferredWidth;
            layout.flexibleWidth = 0f;
            return input;
        }

        // A single-line text field; the placeholder shows while it is empty.
        public static InputField CreateTextField(
            Transform parent,
            string value,
            string placeholder,
            InputField.ContentType contentType,
            int characterLimit,
            float height)
        {
            var inputObject = CreateUiObject("TextField", parent);
            var image = inputObject.AddComponent<Image>();
            image.color = InputColor;
            AddOutline(image, PanelBorderColor);
            var input = inputObject.AddComponent<InputField>();
            input.contentType = contentType;
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = characterLimit;
            input.customCaretColor = true;
            input.caretColor = TextPrimary;
            input.caretWidth = 2;
            input.selectionColor = new Color(0.30f, 0.50f, 0.80f, 0.6f);
            var hint = AddText(
                CreateUiObject("Placeholder", inputObject.transform),
                placeholder,
                15,
                FontStyle.Italic,
                TextAnchor.MiddleLeft,
                TextMuted);
            Stretch(hint.rectTransform, Vector2.zero, Vector2.one, new Vector2(10f, 2f), new Vector2(-10f, -2f));
            hint.supportRichText = false;
            var text = AddText(
                CreateUiObject("Text", inputObject.transform),
                value,
                15,
                FontStyle.Normal,
                TextAnchor.MiddleLeft,
                TextPrimary);
            Stretch(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(10f, 2f), new Vector2(-10f, -2f));
            text.supportRichText = false;
            input.textComponent = text;
            input.placeholder = hint;
            input.text = value ?? string.Empty;
            var layout = inputObject.AddComponent<LayoutElement>();
            layout.minHeight = height;
            layout.preferredHeight = height;
            layout.flexibleWidth = 1f;
            return input;
        }

        // Every code-built panel shares one EventSystem: the first panel that needs it makes it,
        // and it stays across scene loads, since no scene authors one.
        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null || UnityEngine.Object.FindAnyObjectByType<EventSystem>() != null)
            {
                return;
            }

            var eventSystem = new GameObject(
                "TemporaryUiEventSystem",
                typeof(EventSystem),
                typeof(InputSystemUIInputModule));
            if (Application.isPlaying)
            {
                UnityEngine.Object.DontDestroyOnLoad(eventSystem);
            }
        }

        public static RectTransform CreateRow(Transform parent, float height, float spacing)
        {
            var row = CreateUiObject("Row", parent);
            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            row.AddComponent<LayoutElement>().preferredHeight = height;
            return row.GetComponent<RectTransform>();
        }

        public static void AddOutline(Graphic graphic, Color color)
        {
            var outline = graphic.gameObject.AddComponent<Outline>();
            outline.effectColor = color;
            outline.effectDistance = new Vector2(1f, -1f);
        }

        public static string ColorTag(Color color)
        {
            return "#" + ColorUtility.ToHtmlStringRGB(color);
        }

        private static Color ButtonFill(TemporaryButtonTone tone)
        {
            return tone switch
            {
                TemporaryButtonTone.Active => ButtonActiveColor,
                TemporaryButtonTone.Danger => ButtonDangerColor,
                TemporaryButtonTone.Quiet => ButtonQuietColor,
                _ => ButtonColor
            };
        }
    }
}
