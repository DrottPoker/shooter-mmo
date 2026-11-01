using System;
using System.Collections.Generic;
using System.Text;
using Mmorpg.Client.WorldActors;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // Temporary presentation of the world menu: the left-click option next to the pointer,
    // and the right-click menu with a row per option and Cancel last, as in RuneScape. Target
    // names are colored by kind. The menu closes when the pointer moves away from it.
    [DisallowMultipleComponent]
    public sealed class TemporaryWorldContextMenu : MonoBehaviour
    {
        // Above every panel and below the death overlay.
        public const int CanvasSortingOrder = TemporaryInventoryPanel.CanvasSortingOrder + 40;

        public static readonly Color NpcNameColor = new Color(1f, 1f, 0f, 1f);
        public static readonly Color ObjectNameColor = new Color(0f, 1f, 1f, 1f);
        public static readonly Color CorpseNameColor = new Color(1f, 0.56f, 0.25f, 1f);
        public static readonly Color PlayerNameColor = new Color(1f, 1f, 1f, 1f);

        private const float RowHeight = 26f;
        private const float CloseDistance = 24f;
        private static readonly Vector2 HoverPointerOffset = new Vector2(18f, 12f);

        private readonly Vector3[] panelCorners = new Vector3[4];
        private WorldMenuController menu;
        private RectTransform canvasRect;
        private RectTransform hoverRect;
        private TextMeshProUGUI hoverText;
        private RectTransform panel;
        private int shownCount;
        private WorldMenuOption shownTop;
        private bool rowsDirty;

        public bool IsMenuVisible
        {
            get { return panel != null && panel.gameObject.activeSelf; }
        }

        // The labels of the visible rows, Cancel included, without color tags.
        public string[] VisibleRowLabels()
        {
            if (!IsMenuVisible)
            {
                return new string[0];
            }

            var rows = panel.GetComponentsInChildren<Button>(false);
            var labels = new string[rows.Length];
            for (var index = 0; index < rows.Length; index++)
            {
                labels[index] = StripColorTags(rows[index].GetComponentInChildren<TextMeshProUGUI>().text);
            }

            return labels;
        }

        // Called by the composition root before the component is activated.
        public void Initialize(WorldMenuController worldMenu)
        {
            menu = worldMenu;
        }

        public static Color NameColor(WorldMenuTargetStyle style)
        {
            switch (style)
            {
                case WorldMenuTargetStyle.Npc:
                case WorldMenuTargetStyle.Mob:
                    return NpcNameColor;
                case WorldMenuTargetStyle.Object:
                    return ObjectNameColor;
                case WorldMenuTargetStyle.Corpse:
                    return CorpseNameColor;
                default:
                    return PlayerNameColor;
            }
        }

        // "Verb Target" with the target colored by kind.
        public static string RichLabel(WorldMenuOption option)
        {
            if (option.TargetName.Length == 0)
            {
                return option.Verb;
            }

            return option.Verb
                + " <color="
                + TemporaryUiKit.ColorTag(NameColor(option.Style))
                + ">"
                + option.TargetName
                + "</color>";
        }

        private void Start()
        {
            BuildUi();
            if (menu != null)
            {
                menu.MenuChanged += OnMenuChanged;
            }

            OnMenuChanged();
        }

        private void OnDestroy()
        {
            if (menu != null)
            {
                menu.MenuChanged -= OnMenuChanged;
            }
        }

        private void Update()
        {
            if (menu == null || panel == null)
            {
                return;
            }

            if (rowsDirty)
            {
                rowsDirty = false;
                RebuildRows();
            }

            var pointer = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
            if (menu.IsMenuOpen && IsPointerAway(pointer))
            {
                menu.CloseMenu();
            }

            UpdateHover(pointer);
        }

        private void OnMenuChanged()
        {
            rowsDirty = true;
        }

        // The left-click option and how many others there are, such as "Attack Feral Wolf /
        // 3 more options", or an empty string over bare ground, which offers only Walk here.
        public static string HoverLabel(IReadOnlyList<WorldMenuOption> options)
        {
            if (options == null
                || options.Count == 0
                || (options.Count == 1 && options[0].Group == WorldMenuOptionGroup.Walk))
            {
                return string.Empty;
            }

            var label = RichLabel(options[0]);
            if (options.Count == 1)
            {
                return label;
            }

            return label
                + " <color="
                + TemporaryUiKit.ColorTag(TemporaryUiKit.TextSecondary)
                + ">/ "
                + (options.Count - 1)
                + (options.Count == 2 ? " more option" : " more options")
                + "</color>";
        }

        // The label is rebuilt only when the left-click option or the option count changes,
        // so hovering allocates nothing.
        private void UpdateHover(Vector2 pointer)
        {
            var options = menu.IsMenuOpen ? null : menu.HoverOptions;
            var count = options?.Count ?? 0;
            var top = count > 0 ? options[0] : default(WorldMenuOption);
            if (count != shownCount
                || top.Group != shownTop.Group
                || top.Style != shownTop.Style
                || !string.Equals(top.Verb, shownTop.Verb, StringComparison.Ordinal)
                || !string.Equals(top.TargetName, shownTop.TargetName, StringComparison.Ordinal))
            {
                shownCount = count;
                shownTop = top;
                var text = HoverLabel(options);
                hoverText.text = text;
                hoverRect.gameObject.SetActive(text.Length > 0);
            }

            if (hoverRect.gameObject.activeSelf && TryToCanvasPoint(pointer, out var local))
            {
                hoverRect.anchoredPosition = local + HoverPointerOffset;
            }
        }

        private void RebuildRows()
        {
            for (var index = panel.childCount - 1; index >= 0; index--)
            {
                var child = panel.GetChild(index).gameObject;
                child.SetActive(false);
                Destroy(child);
            }

            if (!menu.IsMenuOpen)
            {
                panel.gameObject.SetActive(false);
                return;
            }

            // TextMesh Pro measures a text only once it is active, so the rows are built on the
            // active panel and sized from their own texts.
            panel.gameObject.SetActive(true);
            var header = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Header", panel),
                "Choose Option",
                14,
                FontStyles.Bold,
                TextAlignmentOptions.Left,
                TemporaryUiKit.TextSecondary);
            header.gameObject.AddComponent<LayoutElement>().preferredHeight = 22f;
            var options = menu.MenuOptions;
            for (var index = 0; index < options.Count; index++)
            {
                var choice = index;
                AddRow(RichLabel(options[index]), () => menu.Choose(choice));
            }

            AddRow("Cancel", menu.CloseMenu);
            LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
            PlacePanel(menu.MenuScreenPosition);
        }

        private void AddRow(string label, Action onClick)
        {
            var row = TemporaryUiKit.CreateUiObject("Option", panel);
            var background = row.AddComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0f);
            var button = row.AddComponent<Button>();
            button.targetGraphic = background;
            var colors = button.colors;
            colors.normalColor = new Color(1f, 1f, 1f, 0f);
            colors.highlightedColor = TemporaryUiKit.ButtonActiveColor;
            colors.pressedColor = TemporaryUiKit.AccentColor;
            colors.selectedColor = new Color(1f, 1f, 1f, 0f);
            colors.colorMultiplier = 1f;
            button.colors = colors;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(() => onClick());
            var text = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Label", row.transform),
                label,
                16,
                FontStyles.Normal,
                TextAlignmentOptions.Left,
                TemporaryUiKit.TextPrimary);
            text.textWrappingMode = TextWrappingModes.NoWrap;
            TemporaryUiKit.Stretch(
                text.rectTransform,
                Vector2.zero,
                Vector2.one,
                new Vector2(8f, 0f),
                new Vector2(-8f, 0f));
            var layout = row.AddComponent<LayoutElement>();
            layout.preferredHeight = RowHeight;
            layout.preferredWidth = text.preferredWidth + 16f;
        }

        // The menu opens with its header just above the pointer, so the top option sits under
        // it, and stays inside the screen.
        private void PlacePanel(Vector2 screenPosition)
        {
            if (!TryToCanvasPoint(screenPosition, out var local))
            {
                return;
            }

            var size = panel.rect.size;
            var bounds = canvasRect.rect;
            var x = Mathf.Clamp(local.x, bounds.xMin + (size.x * 0.5f), bounds.xMax - (size.x * 0.5f));
            var y = Mathf.Clamp(local.y + 20f, bounds.yMin + size.y, bounds.yMax);
            panel.anchoredPosition = new Vector2(x, y);
        }

        private bool IsPointerAway(Vector2 pointer)
        {
            if (!IsMenuVisible)
            {
                return false;
            }

            // An overlay canvas has its world corners in screen pixels.
            panel.GetWorldCorners(panelCorners);
            return pointer.x < panelCorners[0].x - CloseDistance
                || pointer.x > panelCorners[2].x + CloseDistance
                || pointer.y < panelCorners[0].y - CloseDistance
                || pointer.y > panelCorners[2].y + CloseDistance;
        }

        private bool TryToCanvasPoint(Vector2 screenPosition, out Vector2 local)
        {
            return RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRect,
                screenPosition,
                null,
                out local);
        }

        private void BuildUi()
        {
            var canvasObject = new GameObject(
                "TemporaryWorldMenuCanvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = CanvasSortingOrder;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            canvasRect = canvasObject.GetComponent<RectTransform>();

            var hoverObject = TemporaryUiKit.CreateUiObject("HoverOption", canvasObject.transform);
            hoverRect = hoverObject.GetComponent<RectTransform>();
            hoverRect.anchorMin = new Vector2(0.5f, 0.5f);
            hoverRect.anchorMax = new Vector2(0.5f, 0.5f);
            hoverRect.pivot = new Vector2(0f, 0f);
            hoverRect.sizeDelta = new Vector2(700f, 32f);
            hoverText = TemporaryUiKit.AddText(
                hoverObject,
                string.Empty,
                19,
                FontStyles.Bold,
                TextAlignmentOptions.Left,
                TemporaryUiKit.TextPrimary);
            hoverText.textWrappingMode = TextWrappingModes.NoWrap;
            // The hover text floats over the world, so it has the world labels' black outline.
            hoverText.fontSharedMaterial = WorldLabels.OutlinedMaterial;
            hoverObject.SetActive(false);

            var panelObject = TemporaryUiKit.CreateUiObject("WorldMenu", canvasObject.transform);
            panel = panelObject.GetComponent<RectTransform>();
            panel.anchorMin = new Vector2(0.5f, 0.5f);
            panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0.5f, 1f);
            var background = panelObject.AddComponent<Image>();
            background.color = TemporaryUiKit.PanelColor;
            TemporaryUiKit.AddOutline(background, TemporaryUiKit.PanelBorderColor);
            var layout = panelObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(4, 4, 2, 4);
            layout.spacing = 0f;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            var fitter = panelObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            panelObject.SetActive(false);
        }

        private static string StripColorTags(string value)
        {
            var builder = new StringBuilder(value.Length);
            var index = 0;
            while (index < value.Length)
            {
                if (value[index] == '<')
                {
                    var end = value.IndexOf('>', index);
                    if (end > index
                        && (string.CompareOrdinal(value, index, "<color", 0, 6) == 0
                            || string.CompareOrdinal(value, index, "</color", 0, 7) == 0))
                    {
                        index = end + 1;
                        continue;
                    }
                }

                builder.Append(value[index]);
                index++;
            }

            return builder.ToString();
        }
    }
}
