using System;
using System.Globalization;
using Mmorpg.WorldData.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // A small framed box that asks one thing, such as how many items to split or whether to
    // destroy an item. One dialog shows at a time; Enter confirms and Escape cancels.
    [DisallowMultipleComponent]
    public sealed class UiDialog : MonoBehaviour
    {
        private const float Width = 300f;

        private UiTheme theme;
        private TextCatalog texts;
        private RectTransform rect;
        private RectTransform bounds;
        private TextMeshProUGUI title;
        private TextMeshProUGUI message;
        private RectTransform quantityRow;
        private TMP_InputField quantityField;
        private Button confirmButton;
        private Action<int> confirmed;
        private int minimum;
        private int maximum;

        public bool IsOpen
        {
            get { return gameObject.activeSelf; }
        }

        // The asked question's heading as given; it shows in capitals.
        public string Title { get; private set; } = string.Empty;

        public string Message
        {
            get { return message.text; }
        }

        // The quantity the field holds, clamped to the asked range.
        public int Quantity
        {
            get { return Clamp(ParseQuantity()); }
        }

        public static UiDialog Create(UiTheme theme, RectTransform parent, TextCatalog texts)
        {
            var root = UiFactory.Create("Dialog", parent);
            var dialog = root.gameObject.AddComponent<UiDialog>();
            dialog.Build(theme, parent, texts ?? throw new ArgumentNullException(nameof(texts)));
            return dialog;
        }

        // Asks for a quantity from minimum to maximum, starting at the initial value, near a
        // screen position.
        public void AskQuantity(
            string heading,
            string text,
            int minimumQuantity,
            int maximumQuantity,
            int initialQuantity,
            Vector2 screenPosition,
            Action<int> onConfirmed)
        {
            minimum = Math.Max(1, minimumQuantity);
            maximum = Math.Max(minimum, maximumQuantity);
            quantityRow.gameObject.SetActive(true);
            quantityField.text = Clamp(initialQuantity).ToString(CultureInfo.InvariantCulture);
            Open(heading, text, texts.Get("ui/common/ok"), UiButtonTone.Primary, onConfirmed);
            PlaceNear(screenPosition);
            quantityField.ActivateInputField();
            quantityField.selectionAnchorPosition = 0;
            quantityField.selectionFocusPosition = quantityField.text.Length;
        }

        // Asks to confirm an action, such as destroying an item, in the middle of the screen.
        public void AskConfirmation(string heading, string text, string confirmLabel, Action onConfirmed)
        {
            minimum = 1;
            maximum = 1;
            quantityRow.gameObject.SetActive(false);
            Open(heading, text, confirmLabel, UiButtonTone.Danger, _ => onConfirmed?.Invoke());
            rect.anchoredPosition = new Vector2(-Width / 2f, rect.rect.height / 2f);
        }

        public void Confirm()
        {
            if (!IsOpen)
            {
                return;
            }

            var callback = confirmed;
            var value = Quantity;
            Close();
            callback?.Invoke(value);
        }

        public void Close()
        {
            confirmed = null;
            if (gameObject.activeSelf)
            {
                gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
            {
                Confirm();
            }
        }

        private void Open(string heading, string text, string confirmLabel, UiButtonTone tone, Action<int> onConfirmed)
        {
            confirmed = onConfirmed;
            Title = heading ?? string.Empty;
            UiFactory.SetHeading(title, Title);
            message.text = text ?? string.Empty;
            message.gameObject.SetActive(!string.IsNullOrEmpty(message.text));
            UiFactory.SetButtonLabel(confirmButton, confirmLabel);
            confirmButton.targetGraphic.color = tone == UiButtonTone.Danger
                ? new Color(1f, 0.78f, 0.74f, 1f)
                : new Color(1f, 0.95f, 0.82f, 1f);
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        }

        private void PlaceNear(Vector2 screenPosition)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(bounds, screenPosition, null, out var pointer))
            {
                return;
            }

            var size = rect.rect.size;
            var area = bounds.rect;
            var position = pointer + new Vector2(12f, -12f);
            position.x = Mathf.Clamp(position.x, area.xMin, Mathf.Max(area.xMin, area.xMax - size.x));
            position.y = Mathf.Clamp(position.y, Mathf.Min(area.yMax, area.yMin + size.y), area.yMax);
            rect.anchoredPosition = position;
        }

        private int ParseQuantity()
        {
            return int.TryParse(quantityField.text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : minimum;
        }

        private int Clamp(int value)
        {
            return Mathf.Clamp(value, minimum, maximum);
        }

        private void Step(int delta)
        {
            quantityField.text = Clamp(ParseQuantity() + delta).ToString(CultureInfo.InvariantCulture);
        }

        private void Build(UiTheme dialogTheme, RectTransform parent, TextCatalog dialogTexts)
        {
            theme = dialogTheme;
            texts = dialogTexts;
            bounds = parent;
            rect = (RectTransform)transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(Width, 0f);
            var frame = gameObject.AddComponent<Image>();
            if (theme.DialogFrame != null)
            {
                UiFactory.SetSprite(frame, theme.DialogFrame, theme.WindowTint);
            }
            else
            {
                frame.color = new Color(0.06f, 0.09f, 0.10f, 0.98f);
            }

            frame.raycastTarget = true;
            var layout = gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(18, 18, 14, 16);
            layout.spacing = 10f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            title = UiFactory.Heading(theme, transform, "Title", theme.SectionSize + 2f, TextAlignmentOptions.MidlineLeft);
            UiFactory.Layout(title, preferredHeight: theme.SectionSize + 10f);
            message = UiFactory.BodyText(theme, transform, "Message", theme.BodySize, theme.TextColor, TextAlignmentOptions.TopLeft);
            message.textWrappingMode = TextWrappingModes.Normal;

            var row = UiFactory.Row(transform, "QuantityRow", theme.FieldHeight, 8f);
            quantityRow = row.GetComponent<RectTransform>();
            UiFactory.Button(theme, row.transform, "-", () => Step(-1), UiButtonTone.Normal, theme.FieldHeight);
            quantityField = UiFactory.Field(
                theme,
                row.transform,
                "QuantityField",
                "1",
                null,
                null,
                TMP_InputField.ContentType.IntegerNumber,
                TextAlignmentOptions.Center);
            quantityField.onSubmit.AddListener(_ => Confirm());
            UiFactory.Button(theme, row.transform, "+", () => Step(1), UiButtonTone.Normal, theme.FieldHeight);

            var buttons = UiFactory.Row(transform, "Buttons", theme.ButtonHeight, 8f);
            confirmButton = UiFactory.Button(theme, buttons.transform, texts.Get("ui/common/ok"), Confirm, UiButtonTone.Primary);
            UiFactory.Button(theme, buttons.transform, texts.Get("ui/common/cancel"), Close);
            gameObject.SetActive(false);
        }
    }
}
