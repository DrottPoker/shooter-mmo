using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Quests;
using Mmorpg.WorldData.Text;

namespace Mmorpg.Client.Quests
{
    // The notices of progress that the carried items make rather than a quest change: a collect
    // objective whose count grew, such as "Wolf Pelts 2/3", and a quest that became ready to turn
    // in, whatever made it ready. It remembers what it told for every active quest, so a quest
    // change or an inventory change tells each step once. A load of the quest log, another
    // character, and the first carried items of an inventory that had none loaded start over
    // without notices, since nothing changed for the player.
    public sealed class QuestProgressWatcher
    {
        private readonly Dictionary<string, WatchedQuest> watched =
            new Dictionary<string, WatchedQuest>(StringComparer.Ordinal);
        private int seenLoad = -1;

        // The notices since the last call, oldest first. carriedItemsKnown is false while the
        // inventory has not loaded the carried items that carriedCount counts.
        public IReadOnlyList<string> Update(
            QuestClientState state,
            Func<string, int> carriedCount,
            bool carriedItemsKnown = true)
        {
            var notices = new List<string>();
            if (state == null || !state.IsReady || !carriedItemsKnown)
            {
                watched.Clear();
                seenLoad = -1;
                return notices;
            }

            var silent = state.LoadCount != seenLoad;
            seenLoad = state.LoadCount;
            if (silent)
            {
                watched.Clear();
            }

            foreach (var gone in watched.Keys.Where(questId => !state.IsActive(questId)).ToArray())
            {
                watched.Remove(gone);
            }

            foreach (var active in state.ActiveQuests.Values.OrderBy(quest => quest.QuestId, StringComparer.Ordinal))
            {
                if (!state.Catalog.TryGet(active.QuestId, out var quest))
                {
                    continue;
                }

                var lines = QuestProgress.CurrentObjectives(quest, active, carriedCount);
                var ready = QuestProgress.IsReadyToTurnIn(quest, active, carriedCount);
                if (!watched.TryGetValue(active.QuestId, out var previous))
                {
                    // A quest that becomes active is announced by its quest change; it only adds
                    // that it is ready at once, such as a delivery whose letter it gave.
                    watched[active.QuestId] = WatchedQuest.Of(active, lines, ready);
                    if (ready && !silent)
                    {
                        notices.Add(ReadyText(state.Catalog, quest));
                    }

                    continue;
                }

                if (string.Equals(previous.StepId, active.StepId, StringComparison.Ordinal))
                {
                    foreach (var line in lines.Where(line => !QuestRules.StoresProgress(line.Objective)))
                    {
                        var before = previous.CountOf(line.Objective.Id);
                        if (!silent && line.Progress > before && before < line.Objective.Count)
                        {
                            notices.Add(line.Text);
                        }
                    }
                }

                if (ready && !previous.Ready && !silent)
                {
                    notices.Add(ReadyText(state.Catalog, quest));
                }

                watched[active.QuestId] = WatchedQuest.Of(active, lines, ready);
            }

            return notices;
        }

        private static string ReadyText(ClientQuestCatalog catalog, QuestDefinition quest)
        {
            return catalog.Texts.Format("ui/quests/notice-ready", MessageArgument.Text("quest", quest.Title));
        }

        private sealed class WatchedQuest
        {
            private readonly Dictionary<string, int> counts;

            private WatchedQuest(string stepId, Dictionary<string, int> counts, bool ready)
            {
                StepId = stepId;
                this.counts = counts;
                Ready = ready;
            }

            public string StepId { get; }

            public bool Ready { get; }

            public static WatchedQuest Of(QuestClientActiveQuest active, IReadOnlyList<QuestObjectiveLine> lines, bool ready)
            {
                return new WatchedQuest(
                    active.StepId,
                    lines.ToDictionary(line => line.Objective.Id, line => line.Progress, StringComparer.Ordinal),
                    ready);
            }

            public int CountOf(string objectiveId)
            {
                return counts.TryGetValue(objectiveId, out var count) ? count : 0;
            }
        }
    }
}
