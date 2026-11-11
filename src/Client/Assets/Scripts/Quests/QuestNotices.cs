using System;
using System.Collections.Generic;
using System.Globalization;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Quests;
using Mmorpg.WorldData.Text;

namespace Mmorpg.Client.Quests
{
    // The game messages for the quests that one committed change touched: an accepted quest, the
    // progress of a counted objective such as "Feral Wolves slain 3/5", a step that moved on after
    // the counts it finished, and a completed or abandoned quest, and for a quest item that a Mob
    // left in its corpse.
    // QuestProgressWatcher tells the collect progress and when a quest is ready to turn in.
    public static class QuestNotices
    {
        public static IReadOnlyList<string> For(
            IReadOnlyList<QuestClientChange> changes,
            ClientQuestCatalog catalog)
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
                        notices.Add(catalog.Texts.Format(
                            change.CompletionsAfter > change.CompletionsBefore
                                ? "ui/quests/notice-completed"
                                : "ui/quests/notice-abandoned",
                            MessageArgument.Text("quest", quest.Title)));
                    }

                    continue;
                }

                if (change.Before == null)
                {
                    notices.Add(catalog.Texts.Format(
                        "ui/quests/notice-accepted",
                        MessageArgument.Text("quest", quest.Title)));
                }
                else if (!string.Equals(change.Before.StepId, change.After.StepId, StringComparison.Ordinal))
                {
                    // A step moves on once it is done, so the counts the change finished show in
                    // full first, such as "Iron Ore mined 5/5".
                    foreach (var line in QuestProgress.CurrentObjectives(quest, change.Before, null))
                    {
                        if (QuestRules.StoresProgress(line.Objective) && !line.IsComplete)
                        {
                            notices.Add(new QuestObjectiveLine(line.Objective, line.Objective.Count).Text);
                        }
                    }

                    notices.Add(catalog.Texts.Format(
                        "ui/quests/notice-updated",
                        MessageArgument.Text("quest", quest.Title)));
                }
                else
                {
                    foreach (var line in QuestProgress.CurrentObjectives(quest, change.After, null))
                    {
                        if (QuestRules.StoresProgress(line.Objective)
                            && line.Progress > change.Before.ProgressOf(line.Objective.Id))
                        {
                            notices.Add(line.Text);
                        }
                    }
                }
            }

            return notices;
        }

        // Such as "Feral Wolf dropped 2 x Wolf Fang for Pelts and Fangs."
        public static string ForDrop(
            RealtimeQuestDropInCorpse drop,
            ClientQuestCatalog catalog,
            Func<string, string> itemName)
        {
            // Without the quest catalog there are no texts to say it with.
            if (drop == null || catalog == null)
            {
                return string.Empty;
            }

            var quest = catalog.TryGet(drop.QuestId, out var definition)
                ? definition.Title
                : drop.QuestId;
            var item = itemName?.Invoke(drop.DefinitionId);
            if (string.IsNullOrEmpty(item))
            {
                item = drop.DefinitionId;
            }

            return catalog.Texts.Format(
                drop.Quantity > 1 ? "ui/quests/notice-drop-many" : "ui/quests/notice-drop",
                MessageArgument.Text("source", catalog.ActorName(drop.SourceActorDefinitionId)),
                MessageArgument.Text("quantity", drop.Quantity.ToString(CultureInfo.InvariantCulture)),
                MessageArgument.Text("item", item),
                MessageArgument.Text("quest", quest));
        }
    }
}
