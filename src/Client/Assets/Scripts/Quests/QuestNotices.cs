using System;
using System.Collections.Generic;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.Client.Quests
{
    // The game messages for the quests that one committed change touched: an accepted quest, the
    // progress of a counted objective such as "Feral Wolves slain 3/5", a step that moved on, a
    // quest ready to turn in, and a completed or abandoned quest.
    public static class QuestNotices
    {
        public static IReadOnlyList<string> For(
            IReadOnlyList<QuestClientChange> changes,
            ClientQuestCatalog catalog,
            Func<string, int> carriedCount)
        {
            var notices = new List<string>();
            if (changes == null || catalog == null)
            {
                return notices;
            }

            foreach (var change in changes)
            {
                if (!catalog.TryGet(change.QuestId, out var quest))
                {
                    continue;
                }

                if (change.After == null)
                {
                    if (change.Before != null)
                    {
                        notices.Add(change.CompletionsAfter > change.CompletionsBefore
                            ? "Quest completed: " + quest.Title
                            : "Quest abandoned: " + quest.Title);
                    }

                    continue;
                }

                if (change.Before == null)
                {
                    notices.Add("Quest accepted: " + quest.Title);
                }
                else if (!string.Equals(change.Before.StepId, change.After.StepId, StringComparison.Ordinal))
                {
                    notices.Add("Quest updated: " + quest.Title);
                }
                else
                {
                    foreach (var line in QuestProgress.CurrentObjectives(quest, change.After, carriedCount))
                    {
                        if (QuestRules.StoresProgress(line.Objective)
                            && line.Progress > change.Before.ProgressOf(line.Objective.Id))
                        {
                            notices.Add(line.Text);
                        }
                    }
                }

                if (QuestProgress.IsReadyToTurnIn(quest, change.After, carriedCount)
                    && (change.Before == null || !QuestProgress.IsReadyToTurnIn(quest, change.Before, carriedCount)))
                {
                    notices.Add(quest.Title + " is ready to turn in.");
                }
            }

            return notices;
        }
    }
}
