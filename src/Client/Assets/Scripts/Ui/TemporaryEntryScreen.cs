using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // A code-built full screen of the account entry flow, LoginMenu and CharacterSelect: cards on
    // a dark backdrop. It keeps the keyboard rules of a form: Tab and Shift+Tab move between the
    // visible text fields in the order they were added, and Enter runs the main action of the
    // screen. A clicked button never keeps the selection, so Enter cannot click it a second time.
    public sealed class TemporaryEntryScreen
    {
        public const int CanvasSortingOrder = 10;
        public const string GameTitle = "MMORPG";

        private const float FieldHeight = 36f;
        private const float MainButtonHeight = 44f;

        private readonly List<InputField> fields = new List<InputField>();
        private GameObject canvasObject;

        private TemporaryEntryScreen()
        {
        }

        public RectTransform Root { get; private set; }

        public static TemporaryEntryScreen Create(Transform parent, string name)
        {
            TemporaryUiKit.EnsureEventSystem();
            var screen = new TemporaryEntryScreen();
            screen.canvasObject = new GameObject(
                name + "Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            screen.canvasObject.transform.SetParent(parent, false);
            var canvas = screen.canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = CanvasSortingOrder;
            var scaler = screen.canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            screen.Root = TemporaryUiKit.CreateUiObject(name, screen.canvasObject.transform).GetComponent<RectTransform>();
            TemporaryUiKit.Stretch(screen.Root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            screen.Root.gameObject.AddComponent<Image>().color = TemporaryUiKit.BackdropColor;
            return screen;
        }

        public void Destroy()
        {
            if (canvasObject != null)
            {
                UnityEngine.Object.Destroy(canvasObject);
                canvasObject = null;
            }
        }

        // Applies the keyboard rules for this frame; true when Enter was pressed.
        public bool ReadKeys()
        {
            var eventSystem = EventSystem.current;
            var selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            if (selected != null && selected.GetComponent<InputField>() == null)
            {
                eventSystem.SetSelectedGameObject(null);
            }

            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return false;
            }

            if (keyboard.tabKey.wasPressedThisFrame)
            {
                FocusNext(keyboard.shiftKey.isPressed ? -1 : 1);
            }

            return keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame;
        }

        // Puts the caret in a field and selects its text, so typing replaces it.
        public static void Focus(InputField field)
        {
            if (field == null || !field.isActiveAndEnabled || !field.IsInteractable())
            {
                return;
            }

            field.Select();
            field.ActivateInputField();
        }

        // A bordered card whose rows stack from the top, placed from the middle of the screen. A
        // card that fits its content grows downward from its top edge at the position, so a page
        // change does not move the rows above it; otherwise the position is its middle, and a row
        // with a flexible height takes the space left.
        public static RectTransform AddCard(
            RectTransform parent,
            string name,
            Vector2 position,
            Vector2 size,
            bool fitContent)
        {
            var card = TemporaryUiKit.CreateUiObject(name, parent).GetComponent<RectTransform>();
            card.anchorMin = new Vector2(0.5f, 0.5f);
            card.anchorMax = new Vector2(0.5f, 0.5f);
            card.pivot = new Vector2(0.5f, fitContent ? 1f : 0.5f);
            card.anchoredPosition = position;
            card.sizeDelta = size;
            var image = card.gameObject.AddComponent<Image>();
            image.color = TemporaryUiKit.PanelColor;
            TemporaryUiKit.AddOutline(image, TemporaryUiKit.PanelBorderColor);
            var layout = AddStackLayout(card.gameObject, 8f);
            layout.padding = new RectOffset(24, 24, 22, 22);
            if (fitContent)
            {
                card.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }

            return card;
        }

        // A part of a card that is shown or hidden as one, such as a tab's page; it takes the
        // height its parent has left.
        public static RectTransform AddPage(RectTransform parent, string name)
        {
            var page = TemporaryUiKit.CreateUiObject(name, parent).GetComponent<RectTransform>();
            AddStackLayout(page.gameObject, 8f);
            page.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1f;
            return page;
        }

        // Empty space; a flexible one pushes the rows below it to the bottom.
        public static void AddSpace(RectTransform parent, float height, bool flexible)
        {
            var layout = TemporaryUiKit.CreateUiObject("Space", parent).AddComponent<LayoutElement>();
            layout.minHeight = height;
            layout.preferredHeight = height;
            layout.flexibleHeight = flexible ? 1f : 0f;
        }

        public static Text AddHeading(RectTransform parent, string text, int fontSize)
        {
            return TemporaryUiKit.CreateLabel(
                parent,
                text,
                fontSize,
                FontStyle.Bold,
                TemporaryUiKit.TextPrimary,
                fontSize + 10f);
        }

        // A wrapping line of secondary or quiet text.
        public static Text AddText(RectTransform parent, string text, int fontSize, Color color)
        {
            return TemporaryUiKit.CreateLabel(parent, text, fontSize, FontStyle.Normal, color, fontSize + 6f);
        }

        // A labeled text field with an optional quiet hint below it; Tab reaches it in the order
        // the fields were added.
        public InputField AddField(
            RectTransform parent,
            string label,
            string value,
            string placeholder,
            InputField.ContentType contentType,
            int characterLimit,
            string hint)
        {
            var group = TemporaryUiKit.CreateUiObject(label + "Field", parent);
            AddStackLayout(group, 4f);
            TemporaryUiKit.CreateLabel(group.transform, label, 13, FontStyle.Bold, TemporaryUiKit.TextSecondary, 18f);
            var field = TemporaryUiKit.CreateTextField(
                group.transform,
                value,
                placeholder,
                contentType,
                characterLimit,
                FieldHeight);
            if (!string.IsNullOrEmpty(hint))
            {
                TemporaryUiKit.CreateLabel(group.transform, hint, 13, FontStyle.Normal, TemporaryUiKit.TextMuted, 17f);
            }

            fields.Add(field);
            return field;
        }

        // The large button of a card's main action.
        public static Button AddMainButton(RectTransform parent, string label, Action onClick)
        {
            var button = TemporaryUiKit.CreateButton(parent, label, onClick, true, TemporaryButtonTone.Active);
            var layout = button.GetComponent<LayoutElement>();
            layout.minHeight = MainButtonHeight;
            layout.preferredHeight = MainButtonHeight;
            button.GetComponentInChildren<Text>().fontSize = 17;
            return button;
        }

        // A row of buttons that share its width, such as tabs.
        public static RectTransform AddButtonRow(RectTransform parent, float height)
        {
            var row = TemporaryUiKit.CreateRow(parent, height, 6f);
            var layout = row.GetComponent<LayoutElement>();
            layout.minHeight = height;
            // The row's layout group would report a flexible height and take a card's spare space.
            layout.flexibleHeight = 0f;
            return row;
        }

        // A scrolling list that takes the height its parent has left; returns the content to add
        // rows to.
        public static RectTransform AddScrollList(RectTransform parent, string name)
        {
            var scrollObject = TemporaryUiKit.CreateUiObject(name, parent);
            var scrollLayout = scrollObject.AddComponent<LayoutElement>();
            scrollLayout.minHeight = 64f;
            scrollLayout.flexibleHeight = 1f;
            var scroll = scrollObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;
            var viewport = TemporaryUiKit.CreateUiObject("Viewport", scrollObject.transform).GetComponent<RectTransform>();
            TemporaryUiKit.Stretch(viewport, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            // The viewport takes the scroll wheel between the rows.
            viewport.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
            viewport.gameObject.AddComponent<RectMask2D>();
            var content = TemporaryUiKit.CreateUiObject("Content", viewport).GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            AddStackLayout(content.gameObject, 6f);
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewport;
            scroll.content = content;
            return content;
        }

        // A row of a list that a click chooses: a title, a detail line below it, and a note on
        // the right, such as whether a shard is online.
        public static Button AddListRow(
            RectTransform list,
            string title,
            string detail,
            string note,
            Color noteColor,
            Action onClick)
        {
            var rowObject = TemporaryUiKit.CreateUiObject("ListRow", list);
            var image = rowObject.AddComponent<Image>();
            image.color = TemporaryUiKit.ButtonColor;
            TemporaryUiKit.AddOutline(image, TemporaryUiKit.PanelBorderColor);
            var button = rowObject.AddComponent<Button>();
            button.targetGraphic = image;
            if (onClick != null)
            {
                button.onClick.AddListener(() => onClick());
            }

            var layout = rowObject.AddComponent<LayoutElement>();
            layout.minHeight = 58f;
            layout.preferredHeight = 58f;
            layout.flexibleWidth = 1f;
            var noteWidth = string.IsNullOrEmpty(note) ? 0f : 130f;
            var titleText = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Title", button.transform),
                title,
                16,
                FontStyle.Bold,
                TextAnchor.LowerLeft,
                TemporaryUiKit.TextPrimary);
            TemporaryUiKit.Stretch(
                titleText.rectTransform,
                new Vector2(0f, 0.5f),
                Vector2.one,
                new Vector2(14f, 2f),
                new Vector2(-(14f + noteWidth), -6f));
            var detailText = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Detail", button.transform),
                detail,
                13,
                FontStyle.Normal,
                TextAnchor.UpperLeft,
                TemporaryUiKit.TextSecondary);
            TemporaryUiKit.Stretch(
                detailText.rectTransform,
                Vector2.zero,
                new Vector2(1f, 0.5f),
                new Vector2(14f, 6f),
                new Vector2(-(14f + noteWidth), -2f));
            if (noteWidth > 0f)
            {
                var noteText = TemporaryUiKit.AddText(
                    TemporaryUiKit.CreateUiObject("Note", button.transform),
                    note,
                    13,
                    FontStyle.Bold,
                    TextAnchor.MiddleRight,
                    noteColor);
                TemporaryUiKit.Stretch(
                    noteText.rectTransform,
                    new Vector2(1f, 0f),
                    Vector2.one,
                    new Vector2(-(14f + noteWidth), 0f),
                    new Vector2(-14f, 0f));
            }

            return button;
        }

        // A status line for the result of the last action, as tall as two lines so the card does
        // not move when a message wraps.
        public static Text AddStatus(RectTransform parent)
        {
            var status = TemporaryUiKit.CreateLabel(parent, string.Empty, 14, FontStyle.Normal, TemporaryUiKit.TextSecondary, 40f);
            status.alignment = TextAnchor.UpperLeft;
            status.gameObject.name = "Status";
            return status;
        }

        private void FocusNext(int step)
        {
            var visible = new List<InputField>(fields.Count);
            foreach (var field in fields)
            {
                if (field != null && field.isActiveAndEnabled && field.IsInteractable())
                {
                    visible.Add(field);
                }
            }

            if (visible.Count == 0)
            {
                return;
            }

            var current = -1;
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            for (var index = 0; index < visible.Count; index++)
            {
                if (visible[index].isFocused || visible[index].gameObject == selected)
                {
                    current = index;
                }
            }

            var next = current < 0
                ? (step > 0 ? 0 : visible.Count - 1)
                : (current + step + visible.Count) % visible.Count;
            Focus(visible[next]);
        }

        private static VerticalLayoutGroup AddStackLayout(GameObject target, float spacing)
        {
            var layout = target.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return layout;
        }
    }

    // Reports a double click with the left button, such as on a character to enter the World.
    public sealed class TemporaryDoubleClick : MonoBehaviour, IPointerClickHandler
    {
        public event Action DoubleClicked;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left && eventData.clickCount == 2)
            {
                DoubleClicked?.Invoke();
            }
        }
    }
}
