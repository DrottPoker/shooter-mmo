using System;
using System.Linq;
using Mmorpg.Client.Quests;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Quests;
using UnityEngine;

namespace Mmorpg.Client.Ui
{
    // The Quests tab of the Admin menu: every quest of the catalog with the character's state,
    // and the development quest tools that a Development worker carries out: accepting any quest,
    // completing an objective, the current step, or the whole quest up to its turn-in, and
    // resetting a quest, a quest with its follow-ups, or every quest. The quest log follows from
    // the committed changes, so the tab only rebuilds from the quest state, and only while the
    // menu shows it; a change while it is hidden rebuilds it once it shows.
    public sealed class TemporaryAdminQuestTab : IDisposable
    {
        private readonly RectTransform page;
        private readonly QuestClientController quests;
        private readonly Action<string, Color> showResult;
        private string pendingDescription;
        private bool shown;
        private bool stale = true;

        public TemporaryAdminQuestTab(RectTransform page, QuestClientController quests, Action<string, Color> showResult)
        {
            this.page = page ?? throw new ArgumentNullException(nameof(page));
            this.quests = quests;
            this.showResult = showResult ?? throw new ArgumentNullException(nameof(showResult));
            if (quests != null)
            {
                quests.State.Changed += OnQuestStateChanged;
                quests.IntentChanged += OnIntentChanged;
            }
        }

        public void Dispose()
        {
            if (quests != null)
            {
                quests.State.Changed -= OnQuestStateChanged;
                quests.IntentChanged -= OnIntentChanged;
            }
        }

        // Whether the menu shows the tab now: open, on the Quests tab.
        public void SetShown(bool value)
        {
            shown = value;
            if (shown && stale)
            {
                Rebuild();
            }
        }

        private void OnQuestStateChanged()
        {
            if (shown)
            {
                Rebuild();
            }
            else
            {
                stale = true;
            }
        }

        // Such as "active, step 2 of 3", "completed 2 times", or "needs level 5".
        public static string StateText(QuestClientState state, QuestDefinition quest)
        {
            if (state.ActiveQuests.TryGetValue(quest.Id, out var active))
            {
                var stepIndex = QuestProgress.StepIndexOf(quest, active.StepId);
                return "active, step " + (stepIndex + 1) + " of " + quest.Steps.Length;
            }

            var completions = state.CompletionCountOf(quest.Id);
            var completed = completions == 1 ? "completed" : "completed " + completions + " times";
            switch (state.AvailabilityOf(quest))
            {
                case QuestAvailability.Available:
                    return completions > 0 ? completed + ", available again" : "available";
                case QuestAvailability.BelowMinimumLevel:
                    return "needs level " + quest.MinimumLevel;
                case QuestAvailability.MissingRequiredQuest:
                    return "needs an earlier quest";
                case QuestAvailability.QuestLogFull:
                    return "the quest log is full";
                case QuestAvailability.AlreadyCompleted:
                    return completed;
                default:
                    return string.Empty;
            }
        }

        public void Rebuild()
        {
            stale = false;
            UiFactory.ClearChildren(page);
            TemporaryMenuWindow.AddSection(page, "Quests");
            var state = quests != null ? quests.State : null;
            if (state == null || !state.IsReady)
            {
                TemporaryMenuWindow.AddNote(page, "Your quests show once they are loaded in a World.");
                return;
            }

            TemporaryMenuWindow.AddNote(
                page,
                "Accept goes around the giver, the level, and earlier quests. Complete gives the items a collect objective lacks. Reset forgets a quest and takes its quest items.");
            foreach (var quest in state.Catalog.Quests.OrderBy(quest => quest.Title, StringComparer.Ordinal))
            {
                AddQuest(state, quest);
            }

            TemporaryMenuWindow.AddSection(page, "Every quest");
            var row = TemporaryMenuWindow.AddButtonRow(page);
            TemporaryUiKit.CreateButton(
                row,
                "Reset all quests",
                () => Run(RealtimeQuestIntent.CreateAdminResetAll(Guid.NewGuid()), "Reset all quests"),
                true,
                TemporaryButtonTone.Danger);
        }

        private void AddQuest(QuestClientState state, QuestDefinition quest)
        {
            TemporaryMenuWindow.AddValue(page, quest.Title).text = StateText(state, quest);
            var held = state.IsActive(quest.Id) || state.CompletionCountOf(quest.Id) > 0;
            if (state.ActiveQuests.TryGetValue(quest.Id, out var active))
            {
                var progress = TemporaryMenuWindow.AddButtonRow(page);
                TemporaryUiKit.CreateButton(
                    progress,
                    "Complete step",
                    () => Run(RealtimeQuestIntent.CreateAdminCompleteStep(Guid.NewGuid(), quest.Id), "Complete the step of " + quest.Title),
                    true,
                    TemporaryButtonTone.Normal);
                TemporaryUiKit.CreateButton(
                    progress,
                    "Make ready",
                    () => Run(RealtimeQuestIntent.CreateAdminMakeReady(Guid.NewGuid(), quest.Id), "Make " + quest.Title + " ready"),
                    true,
                    TemporaryButtonTone.Normal);
                foreach (var line in QuestProgress.CurrentObjectives(quest, active, null))
                {
                    var objective = line.Objective;
                    var objectiveRow = TemporaryMenuWindow.AddButtonRow(page);
                    TemporaryUiKit.CreateButton(
                        objectiveRow,
                        "Complete: " + objective.Text,
                        () => Run(
                            RealtimeQuestIntent.CreateAdminCompleteObjective(Guid.NewGuid(), quest.Id, objective.Id),
                            "Complete " + objective.Text),
                        !QuestRules.StoresProgress(objective) || !line.IsComplete,
                        TemporaryButtonTone.Quiet);
                }
            }
            else if (state.AvailabilityOf(quest) != QuestAvailability.AlreadyCompleted)
            {
                var accept = TemporaryMenuWindow.AddButtonRow(page);
                TemporaryUiKit.CreateButton(
                    accept,
                    "Accept",
                    () => Run(RealtimeQuestIntent.CreateAdminAccept(Guid.NewGuid(), quest.Id), "Accept " + quest.Title),
                    true,
                    TemporaryButtonTone.Normal);
            }

            if (held)
            {
                var reset = TemporaryMenuWindow.AddButtonRow(page);
                TemporaryUiKit.CreateButton(
                    reset,
                    "Reset",
                    () => Run(RealtimeQuestIntent.CreateAdminReset(Guid.NewGuid(), quest.Id), "Reset " + quest.Title),
                    true,
                    TemporaryButtonTone.Danger);
                TemporaryUiKit.CreateButton(
                    reset,
                    "Reset with follow-ups",
                    () => Run(
                        RealtimeQuestIntent.CreateAdminResetChain(Guid.NewGuid(), quest.Id),
                        "Reset " + quest.Title + " and its follow-ups"),
                    true,
                    TemporaryButtonTone.Danger);
            }
        }

        private void Run(RealtimeQuestIntent intent, string description)
        {
            var error = "quests are unavailable.";
            if (quests == null || !quests.TryRunAdminTool(intent, out error))
            {
                showResult(description + ": " + error, TemporaryUiKit.DangerColor);
                return;
            }

            pendingDescription = description;
            showResult(description + "...", TemporaryUiKit.TextSecondary);
        }

        // The controller tells when a quest intent is sent and when it is answered.
        private void OnIntentChanged()
        {
            if (pendingDescription == null || quests.HasPendingIntent)
            {
                return;
            }

            var description = pendingDescription;
            pendingDescription = null;
            if (string.IsNullOrEmpty(quests.LastIntentError))
            {
                showResult(description + ": done.", TemporaryUiKit.SuccessColor);
            }
            else
            {
                showResult(description + " refused: " + quests.LastIntentError, TemporaryUiKit.DangerColor);
            }
        }
    }
}
