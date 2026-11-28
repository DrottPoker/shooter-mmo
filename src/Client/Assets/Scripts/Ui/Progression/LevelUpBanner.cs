using System;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Text;
using TMPro;
using UnityEngine;

namespace Mmorpg.Client.Ui
{
    // The banner of a level-up in the upper third of the screen: a small "Level up" over the new
    // level, both outlined. It fades in, holds, and fades out; a newer level-up starts it over
    // with its level. A snapshot and a level that an admin tool lowered show none. Nothing on it
    // takes a raycast.
    [DisallowMultipleComponent]
    public sealed class LevelUpBanner : MonoBehaviour
    {
        public const string BannerName = "LevelUpBanner";

        // The banner's centre, as a share of the screen height from the bottom.
        private const float CentreHeight = 0.75f;
        private const float LevelSize = 46f;
        private const float Width = 640f;
        private const float CaptionHeight = 26f;
        private const float LevelHeight = 58f;

        private UiTheme theme;
        private ProgressionClientState progression;
        private TextCatalog texts = TextCatalog.Empty(TextTableFormat.SourceLocale);
        private RectTransform root;
        private CanvasGroup group;
        private TextMeshProUGUI captionText;
        private TextMeshProUGUI levelText;
        private float startedAt = float.NegativeInfinity;

        public bool IsVisible
        {
            get { return root != null && root.gameObject.activeSelf; }
        }

        public string CaptionLabel
        {
            get { return captionText != null ? captionText.text : string.Empty; }
        }

        public string LevelLabel
        {
            get { return levelText != null ? levelText.text : string.Empty; }
        }

        // The banner's opacity now.
        public float Alpha
        {
            get { return group != null ? group.alpha : 0f; }
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

        // Start builds the banner and follows the state; a test that runs it by hand calls it
        // itself.
        internal void Begin()
        {
            Build();
            if (progression != null)
            {
                progression.Changed += OnProgressionChanged;
            }
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
            Tick(Time.unscaledTime);
        }

        // Fades the banner by its age and hides it once its whole time passed; it starts at an
        // opacity of 0, so the opacity alone never hides it.
        internal void Tick(float now)
        {
            if (root == null || !root.gameObject.activeSelf)
            {
                return;
            }

            var age = now - startedAt;
            if (ExperienceBarPresentation.BannerFinished(age))
            {
                root.gameObject.SetActive(false);
                return;
            }

            group.alpha = ExperienceBarPresentation.BannerAlpha(age);
        }

        private void OnProgressionChanged(RealtimeOwnerProgressionChanged change)
        {
            if (root == null)
            {
                return;
            }

            // A session that ends takes the banner with it.
            if (change == null && !progression.HasProgression)
            {
                root.gameObject.SetActive(false);
                return;
            }

            if (!ExperienceBarPresentation.ShowsLevelUp(change))
            {
                return;
            }

            Show(change.Level, Time.unscaledTime);
        }

        // Starts the banner over with a level at a time.
        internal void Show(int level, float now)
        {
            levelText.text = ExperienceBarPresentation.LevelText(texts, level);
            startedAt = now;
            group.alpha = 0f;
            root.gameObject.SetActive(true);
        }

        private void Build()
        {
            var canvas = UiCanvas.Create(transform, "LevelUpBanner", UiLayer.Hud, false);
            root = UiFactory.Create(BannerName, canvas.transform);
            root.anchorMin = new Vector2(0.5f, CentreHeight);
            root.anchorMax = new Vector2(0.5f, CentreHeight);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.sizeDelta = new Vector2(Width, CaptionHeight + LevelHeight);
            group = root.gameObject.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;

            captionText = UiFactory.BodyText(
                theme,
                root,
                "Caption",
                theme.BodySize + 3f,
                theme.TextColor,
                TextAlignmentOptions.Bottom,
                texts.Get("ui/progression/level-up"));
            HudText.Outline(captionText);
            UiFactory.Place(captionText.rectTransform, Vector2.zero, new Vector2(Width, CaptionHeight));

            levelText = UiFactory.Text(root, "Level", theme.TitleFont, LevelSize, theme.TitleColor, TextAlignmentOptions.Top);
            levelText.characterSpacing = theme.TitleCharacterSpacing;
            HudText.Outline(levelText);
            UiFactory.Place(levelText.rectTransform, new Vector2(0f, CaptionHeight), new Vector2(Width, LevelHeight));
            root.gameObject.SetActive(false);
        }
    }
}
