using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mmorpg.Client.Quests;
using Mmorpg.WorldData.Text;
using TMPro;
using UnityEngine;

namespace Mmorpg.Client.Ui
{
    // The quest log that L opens: the active quests with the current step's objectives, the
    // selected quest's text with Abandon, and the completed quests. It reads the quest state and
    // abandons through the quest controller.
    internal sealed class QuestLogWindow
    {
        public const string WindowName = "QuestLogWindow";

        private const float Width = 440f;

        private readonly UiTheme theme;
        private readonly TextCatalog texts;
        private readonly Action<QuestLogActiveEntry> abandon;
        private readonly Action refresh;
        private string selectedQuestId = string.Empty;

        // refresh shows the log again with the current state, such as after a selection.
        public QuestLogWindow(
            UiTheme theme,
            TextCatalog texts,
            RectTransform parent,
            Action<QuestLogActiveEntry> abandon,
            Action refresh)
        {
            this.theme = theme;
            this.texts = texts ?? throw new ArgumentNullException(nameof(texts));
            this.abandon = abandon;
            this.refresh = refresh;
            Window = UiWindow.Create(theme, parent, WindowName, null, texts.Get("ui/quests/log-title"), Width);
            Window.SetMaximumBodyHeight(1080f - theme.WindowTop - theme.ScreenMargin - 220f);
            Window.SetOpen(false);
        }

        public UiWindow Window { get; }

        public bool IsOpen
        {
            get { return Window.IsOpen; }
        }

        // Shows the log; a quest that left the log loses its selection.
        public void Refresh(
            QuestClientState state,
            Func<string, int> carriedCount,
            bool abandonPending,
            string abandonError)
        {
            if (!Window.IsOpen)
            {
                return;
            }

            UiFactory.ClearChildren(Window.Body);
            UiFactory.ClearChildren(Window.Footer);
            if (state == null || !state.IsReady)
            {
                AddParagraph(StatusText(state, texts), theme.MutedTextColor);
                Window.FitBody();
                return;
            }

            var active = QuestLogModel.Active(state, carriedCount);
            var maximum = state.Catalog.MaximumActiveQuests;
            UiFactory.SectionHeading(theme, Window.Body, texts.Get("ui/quests/log-active"), null, out var count);
            count.text = active.Count + " / " + maximum;
            if (active.Count == 0)
            {
                AddParagraph(texts.Get("ui/quests/log-empty"), theme.MutedTextColor);
            }

            var selected = active.FirstOrDefault(entry =>
                string.Equals(entry.Quest.Id, selectedQuestId, StringComparison.Ordinal));
            foreach (var entry in active)
            {
                var chosen = entry;
                var label = entry.IsReady
                    ? texts.Format("ui/quests/label-ready", MessageArgument.Text("quest", entry.Quest.Title))
                    : entry.Quest.Title;
                UiFactory.Button(
                    theme,
                    Window.Body,
                    label,
                    () => Select(chosen.Quest.Id),
                    ReferenceEquals(entry, selected) ? UiButtonTone.Primary : UiButtonTone.Normal);
            }

            if (selected != null)
            {
                UiFactory.SectionHeading(theme, Window.Body, selected.Quest.Title, null, out _);
                foreach (var line in selected.Objectives)
                {
                    AddParagraph(line.Text, line.IsComplete ? theme.MutedTextColor : theme.TextColor);
                }

                if (selected.IsReady)
                {
                    AddParagraph(texts.Get("ui/quests/ready-to-turn-in"), theme.GoldTextColor);
                }

                AddParagraph(selected.Quest.OfferText, theme.MutedTextColor);
                var button = UiFactory.Button(
                    theme,
                    Window.Footer,
                    texts.Get("ui/quests/abandon"),
                    () => abandon?.Invoke(selected),
                    UiButtonTone.Danger);
                button.interactable = !abandonPending;
            }

            var completed = QuestLogModel.Completed(state);
            if (completed.Count > 0)
            {
                UiFactory.SectionHeading(theme, Window.Body, texts.Get("ui/quests/log-completed"), null, out var done);
                done.text = completed.Count.ToString();
                foreach (var entry in completed)
                {
                    AddParagraph(
                        entry.CompletionCount > 1
                            ? texts.Format(
                                "ui/quests/label-completed-times",
                                MessageArgument.Text("quest", entry.Quest.Title),
                                MessageArgument.Text("count", entry.CompletionCount.ToString(CultureInfo.InvariantCulture)))
                            : entry.Quest.Title,
                        theme.MutedTextColor);
                }
            }

            if (!string.IsNullOrWhiteSpace(abandonError))
            {
                AddParagraph(abandonError, theme.DangerColor);
            }

            Window.FitBody();
        }

        // What the quest windows show until the quest log is ready, with the reason a log or
        // the bundled catalog failed: the text of its code, such as an outdated client.
        internal static string StatusText(QuestClientState state, TextCatalog texts)
        {
            switch (state?.Status ?? QuestClientStatus.Unavailable)
            {
                case QuestClientStatus.Loading:
                case QuestClientStatus.Unavailable:
                    return texts.Get("ui/quests/status-reading");
                default:
                    return string.IsNullOrWhiteSpace(state.ErrorMessage)
                        ? texts.Get("ui/quests/status-unavailable")
                        : texts.Format(
                            "ui/quests/status-unavailable-reason",
                            MessageArgument.Text("reason", state.ErrorMessage));
            }
        }

        // Choosing the selected quest again hides its details.
        private void Select(string questId)
        {
            selectedQuestId = string.Equals(selectedQuestId, questId, StringComparison.Ordinal) ? string.Empty : questId;
            refresh?.Invoke();
        }

        private void AddParagraph(string text, Color color)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var paragraph = UiFactory.BodyText(theme, Window.Body, "Text", theme.BodySize, color, TextAlignmentOptions.TopLeft, text);
            paragraph.textWrappingMode = TextWrappingModes.Normal;
        }
    }
}
