using System;
using System.Text;
using Mmorpg.Client.Quests;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The tracker at the right edge of the screen: the active quests with the current step's
    // objectives, such as "Feral Wolves slain 3/5", or "Ready to turn in" for a quest that is
    // done. It shows the first quests by title and names how many more the quest log holds. It
    // takes no clicks.
    public sealed class QuestTracker
    {
        public const string PanelName = "QuestTracker";

        // As many quests as fit beside the inventory windows; the quest log lists the rest.
        public const int MaximumShownQuests = 6;

        private const float Width = 300f;

        private readonly UiTheme theme;
        private readonly RectTransform panel;
        private readonly TextMeshProUGUI text;
        private readonly StringBuilder builder = new StringBuilder();

        public QuestTracker(UiTheme theme, RectTransform parent)
        {
            this.theme = theme;
            panel = UiFactory.Create(PanelName, parent);
            panel.anchorMin = new Vector2(1f, 0.5f);
            panel.anchorMax = new Vector2(1f, 0.5f);
            panel.pivot = new Vector2(1f, 0.5f);
            panel.anchoredPosition = new Vector2(-theme.ScreenMargin, 0f);
            var background = panel.gameObject.AddComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.35f);
            background.raycastTarget = false;
            var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 8, 10);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = panel.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            panel.sizeDelta = new Vector2(Width, 0f);
            text = UiFactory.BodyText(theme, panel, "Lines", theme.SmallSize + 1f, theme.TextColor, TextAlignmentOptions.TopLeft);
            text.textWrappingMode = TextWrappingModes.Normal;
            panel.gameObject.SetActive(false);
        }

        public RectTransform Panel
        {
            get { return panel; }
        }

        public string Text
        {
            get { return text.text; }
        }

        // A read of the same character's log again keeps the lines until it answers, so the
        // tracker does not blink.
        public void Refresh(QuestClientState state, Func<string, int> carriedCount)
        {
            if (state != null && state.Status == QuestClientStatus.Loading && state.Revision >= 0)
            {
                return;
            }

            var active = QuestLogModel.Active(state, carriedCount);
            panel.gameObject.SetActive(active.Count > 0);
            if (active.Count == 0)
            {
                return;
            }

            var title = ColorUtility.ToHtmlStringRGB(theme.TitleColor);
            var done = ColorUtility.ToHtmlStringRGB(theme.MutedTextColor);
            var ready = ColorUtility.ToHtmlStringRGB(theme.GoldTextColor);
            builder.Clear();
            for (var index = 0; index < active.Count && index < MaximumShownQuests; index++)
            {
                var entry = active[index];
                if (index > 0)
                {
                    builder.Append('\n');
                }

                builder.Append("<color=#").Append(title).Append("><b>").Append(Escape(entry.Quest.Title)).Append("</b></color>");
                if (entry.IsReady)
                {
                    builder.Append("\n  <color=#").Append(ready).Append(">Ready to turn in</color>");
                    continue;
                }

                foreach (var line in entry.Objectives)
                {
                    builder.Append("\n  ");
                    if (line.IsComplete)
                    {
                        builder.Append("<color=#").Append(done).Append('>').Append(Escape(line.Text)).Append("</color>");
                    }
                    else
                    {
                        builder.Append(Escape(line.Text));
                    }
                }
            }

            if (active.Count > MaximumShownQuests)
            {
                builder.Append("\n<color=#").Append(done).Append(">+")
                    .Append(active.Count - MaximumShownQuests)
                    .Append(" more in the quest log (L)</color>");
            }

            text.text = builder.ToString();
        }

        // Quest texts are content, so a tag in one never styles the tracker.
        private static string Escape(string value)
        {
            return "<noparse>" + (value ?? string.Empty).Replace("</noparse>", string.Empty) + "</noparse>";
        }
    }
}
