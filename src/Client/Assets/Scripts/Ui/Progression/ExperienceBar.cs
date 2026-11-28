using System;
using System.Collections.Generic;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The experience bar at the bottom centre of the screen: the character's level left of the
    // bar, the experience of the level in it, and its numbers right of it, all from the worker's
    // progression, on one dark plate that hugs them. The fill slides to every new value and runs
    // over the top on a level-up, and outlined gained experience rises above the bar's right
    // end. It shows once the state has a progression, so only in a joined World, and nothing on
    // it takes a raycast.
    [DisallowMultipleComponent]
    public sealed class ExperienceBar : MonoBehaviour
    {
        public const string BarName = "ExperienceBar";

        // The height at the bottom of the screen that the bar keeps for itself with the default
        // theme measures: the margin and the row on its plate. The interaction panel and the
        // game messages stand above it.
        public const float ReservedBottom = 64f;

        // Between a text and the bar, and the plate's padding around the row.
        private const float SideGap = 10f;
        private const float PlatePaddingX = 10f;
        private const float PlatePaddingY = 6f;
        private const float GainLift = 2f;
        private const float GainWidth = 160f;
        private const float GainHeight = 28f;

        private readonly ExperienceBarFill fill = new ExperienceBarFill();
        private readonly List<Gain> gains = new List<Gain>();
        private readonly Stack<TextMeshProUGUI> idleGains = new Stack<TextMeshProUGUI>();
        private UiTheme theme;
        private ProgressionClientState progression;
        private TextCatalog texts = TextCatalog.Empty(TextTableFormat.SourceLocale);
        private RectTransform root;
        private RectTransform plate;
        private RectTransform fillRect;
        private TextMeshProUGUI levelText;
        private TextMeshProUGUI numbersText;
        private float shownFraction = -1f;

        public bool IsVisible
        {
            get { return root != null && root.gameObject.activeSelf; }
        }

        public string LevelLabel
        {
            get { return levelText != null ? levelText.text : string.Empty; }
        }

        public string NumbersLabel
        {
            get { return numbersText != null ? numbersText.text : string.Empty; }
        }

        // The plate behind the row in the bar's space: x is its left edge from the bar's left
        // end, y its middle from the bar's middle, and the size its width and height.
        public Rect PlateRect
        {
            get
            {
                return plate != null
                    ? new Rect(plate.anchoredPosition.x, plate.anchoredPosition.y, plate.sizeDelta.x, plate.sizeDelta.y)
                    : default(Rect);
            }
        }

        // The top of the plate above the bottom of the screen, which ReservedBottom covers.
        public float PlateTop
        {
            get { return plate != null ? root.anchoredPosition.y + (root.sizeDelta.y * 0.5f) + (plate.sizeDelta.y * 0.5f) : 0f; }
        }

        // The widths the level and the numbers draw at now.
        public float LevelWidth
        {
            get { return levelText != null ? levelText.rectTransform.sizeDelta.x : 0f; }
        }

        public float NumbersWidth
        {
            get { return numbersText != null ? numbersText.rectTransform.sizeDelta.x : 0f; }
        }

        // The shown fill now, from 0 to 1.
        public float ShownFraction
        {
            get { return Mathf.Max(0f, shownFraction); }
        }

        // The texts of the gained experience that shows now, oldest first.
        public IReadOnlyList<string> GainTexts()
        {
            var shown = new List<string>(gains.Count);
            foreach (var gain in gains)
            {
                shown.Add(gain.Text.text);
            }

            return shown;
        }

        // Called by the composition root before the component is activated.
        public void Initialize(UiTheme uiTheme, ProgressionClientState progressionState, TextCatalog uiTexts)
        {
            theme = uiTheme != null ? uiTheme : throw new ArgumentNullException(nameof(uiTheme));
            progression = progressionState;
            texts = uiTexts ?? throw new ArgumentNullException(nameof(uiTexts));
        }

        private void Start()
        {
            Begin();
        }

        // Start builds the bar and follows the state; a test that runs it by hand calls it itself.
        internal void Begin()
        {
            Build();
            if (progression != null)
            {
                progression.Changed += OnProgressionChanged;
            }

            Show(progression != null ? progression.Progression : null, false);
        }

        private void OnDestroy()
        {
            if (progression != null)
            {
                progression.Changed -= OnProgressionChanged;
            }
        }

        private void LateUpdate()
        {
            if (root == null || !root.gameObject.activeSelf)
            {
                return;
            }

            var now = Time.unscaledTime;
            SetFill(fill.Fraction(now));
            for (var index = gains.Count - 1; index >= 0; index--)
            {
                var gain = gains[index];
                var age = now - gain.StartedAt;
                if (age >= ExperienceBarPresentation.GainSeconds)
                {
                    Release(index);
                    continue;
                }

                PlaceGain(gain.Text, age);
            }
        }

        private void OnProgressionChanged(RealtimeOwnerProgressionChanged change)
        {
            Show(progression.Progression, true);
            if (ExperienceBarPresentation.ShowsGain(change))
            {
                AddGain(change.ExperienceGained);
            }
        }

        private void Show(RealtimeOwnerProgressionChanged current, bool changed)
        {
            if (root == null)
            {
                return;
            }

            if (current == null)
            {
                root.gameObject.SetActive(false);
                fill.Reset();
                ReleaseAll();
                return;
            }

            root.gameObject.SetActive(true);
            var level = ExperienceBarPresentation.LevelText(texts, current.Level);
            var numbers = ExperienceBarPresentation.ExperienceText(texts, current.Experience, current.ExperienceToNextLevel);
            if (!string.Equals(level, levelText.text, StringComparison.Ordinal)
                || !string.Equals(numbers, numbersText.text, StringComparison.Ordinal))
            {
                levelText.text = level;
                numbersText.text = numbers;
                FitRow();
            }

            var levelUp = changed && ExperienceBarPresentation.ShowsLevelUp(current);
            fill.Set(ExperienceBarPresentation.Fraction(current), levelUp, Time.unscaledTime);
            SetFill(fill.Fraction(Time.unscaledTime));
        }

        private void SetFill(float fraction)
        {
            if (Mathf.Approximately(fraction, shownFraction))
            {
                return;
            }

            shownFraction = fraction;
            fillRect.anchorMax = new Vector2(fraction, 1f);
            fillRect.gameObject.SetActive(fraction > 0f);
        }

        // A fifth gain replaces the oldest.
        private void AddGain(long experienceGained)
        {
            if (gains.Count >= ExperienceBarPresentation.MaximumGains)
            {
                Release(0);
            }

            var text = idleGains.Count > 0 ? idleGains.Pop() : CreateGainText();
            text.text = ExperienceBarPresentation.GainText(texts, experienceGained);
            text.gameObject.SetActive(true);
            PlaceGain(text, 0f);
            gains.Add(new Gain(text, Time.unscaledTime));
        }

        private void PlaceGain(TextMeshProUGUI text, float age)
        {
            var plateAbove = (plate.sizeDelta.y - root.sizeDelta.y) * 0.5f;
            text.rectTransform.anchoredPosition = new Vector2(0f, plateAbove + GainLift + ExperienceBarPresentation.GainOffset(age));
            var color = theme.ExperienceColor;
            color.a *= ExperienceBarPresentation.GainAlpha(age);
            text.color = color;
        }

        private void Release(int index)
        {
            var text = gains[index].Text;
            gains.RemoveAt(index);
            text.gameObject.SetActive(false);
            idleGains.Push(text);
        }

        private void ReleaseAll()
        {
            for (var index = gains.Count - 1; index >= 0; index--)
            {
                Release(index);
            }
        }

        // The root is the bar's place, centred at the bottom of the screen; the plate lies behind
        // the row, the level hangs off the bar's left end and the numbers off its right end, all
        // centred on it.
        private void Build()
        {
            var look = theme;
            var canvas = UiCanvas.Create(transform, "ExperienceBar", UiLayer.ExperienceBar, false);
            root = UiFactory.Create(BarName, canvas.transform);
            root.anchorMin = new Vector2(0.5f, 0f);
            root.anchorMax = new Vector2(0.5f, 0f);
            root.pivot = new Vector2(0.5f, 0f);
            root.anchoredPosition = new Vector2(0f, look.ScreenMargin);
            root.sizeDelta = new Vector2(look.ExperienceBarWidth, look.ExperienceBarHeight);

            var plateImage = UiFactory.Create("Plate", root).gameObject.AddComponent<Image>();
            plateImage.color = look.HudPlateColor;
            plateImage.raycastTarget = false;
            plate = plateImage.rectTransform;
            plate.anchorMin = new Vector2(0f, 0.5f);
            plate.anchorMax = new Vector2(0f, 0.5f);
            plate.pivot = new Vector2(0f, 0.5f);

            var trackRect = UiFactory.Create("Track", root);
            UiFactory.Fill(trackRect);
            var track = trackRect.gameObject.AddComponent<Image>();
            UiFactory.SetSprite(track, look.MeterTrack, look.WindowTint);
            if (look.MeterTrack == null)
            {
                track.color = new Color(0.02f, 0.03f, 0.03f, 0.85f);
            }

            track.raycastTarget = false;
            var fillImage = UiFactory.Image(trackRect, "Fill", look.MeterFill, look.ExperienceColor);
            if (look.MeterFill == null)
            {
                fillImage.color = look.ExperienceColor;
            }

            fillRect = fillImage.rectTransform;
            UiFactory.Stretch(fillRect, Vector2.zero, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(-1f, -1f));

            levelText = UiFactory.Text(root, "Level", look.TitleFont, look.ExperienceLevelSize, look.TitleColor, TextAlignmentOptions.MidlineRight);
            levelText.characterSpacing = look.TitleCharacterSpacing;
            HangOff(levelText.rectTransform, 0f, new Vector2(1f, 0.5f), new Vector2(-SideGap, 0f));

            numbersText = UiFactory.BodyText(look, root, "Numbers", look.LabelSize, look.TextColor, TextAlignmentOptions.MidlineLeft);
            HangOff(numbersText.rectTransform, 1f, new Vector2(0f, 0.5f), new Vector2(SideGap, 0f));

            FitRow();
            root.gameObject.SetActive(false);
        }

        private static void HangOff(RectTransform rect, float anchorX, Vector2 pivot, Vector2 position)
        {
            rect.anchorMin = new Vector2(anchorX, 0.5f);
            rect.anchorMax = new Vector2(anchorX, 0.5f);
            rect.pivot = pivot;
            rect.anchoredPosition = position;
        }

        // The texts take the size they draw at, and the plate hugs the row: the level, the bar,
        // and the numbers with their padding. The bar stays where it is, so a longer level or
        // longer numbers widen the plate on their side only. TextMesh Pro measures a text only
        // while it is active, which Show makes it before it sets a text.
        private void FitRow()
        {
            var level = Measure(levelText);
            var numbers = Measure(numbersText);
            var rowHeight = Mathf.Max(root.sizeDelta.y, Mathf.Max(level.y, numbers.y));
            levelText.rectTransform.sizeDelta = new Vector2(level.x, rowHeight);
            numbersText.rectTransform.sizeDelta = new Vector2(numbers.x, rowHeight);
            var left = level.x > 0f ? level.x + SideGap : 0f;
            var right = numbers.x > 0f ? numbers.x + SideGap : 0f;
            plate.anchoredPosition = new Vector2(-(left + PlatePaddingX), 0f);
            plate.sizeDelta = new Vector2(
                left + root.sizeDelta.x + right + (2f * PlatePaddingX),
                rowHeight + (2f * PlatePaddingY));
        }

        private static Vector2 Measure(TextMeshProUGUI text)
        {
            if (string.IsNullOrEmpty(text.text))
            {
                return Vector2.zero;
            }

            var size = text.GetPreferredValues(text.text);
            return new Vector2(Mathf.Ceil(size.x), Mathf.Ceil(size.y));
        }

        // A gained experience label stands centred over the bar's right end and rises from there.
        private TextMeshProUGUI CreateGainText()
        {
            var look = theme;
            var text = UiFactory.BodyText(look, root, "ExperienceGained", look.LabelSize + 3f, look.ExperienceColor, TextAlignmentOptions.Bottom);
            text.fontStyle = FontStyles.Bold;
            HudText.Outline(text);
            var rect = text.rectTransform;
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(GainWidth, GainHeight);
            text.gameObject.SetActive(false);
            return text;
        }

        private readonly struct Gain
        {
            public Gain(TextMeshProUGUI text, float startedAt)
            {
                Text = text;
                StartedAt = startedAt;
            }

            public TextMeshProUGUI Text { get; }

            public float StartedAt { get; }
        }
    }
}
