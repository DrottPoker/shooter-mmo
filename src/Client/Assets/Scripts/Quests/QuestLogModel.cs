using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.Client.Quests
{
    // An active quest as the quest log and the tracker show it: the current step's objectives
    // with their progress, and whether it is ready to turn in.
    public sealed class QuestLogActiveEntry
    {
        public QuestLogActiveEntry(
            QuestDefinition quest,
            QuestClientActiveQuest active,
            IReadOnlyList<QuestObjectiveLine> objectives,
            bool isReady)
        {
            Quest = quest;
            Active = active;
            Objectives = objectives;
            IsReady = isReady;
        }

        public QuestDefinition Quest { get; }

        public QuestClientActiveQuest Active { get; }

        public IReadOnlyList<QuestObjectiveLine> Objectives { get; }

        public bool IsReady { get; }
    }

    public sealed class QuestLogCompletedEntry
    {
        public QuestLogCompletedEntry(QuestDefinition quest, int completionCount)
        {
            Quest = quest;
            CompletionCount = completionCount;
        }

        public QuestDefinition Quest { get; }

        public int CompletionCount { get; }
    }

    // What the quest log and the tracker list, by quest title.
    public static class QuestLogModel
    {
        public static IReadOnlyList<QuestLogActiveEntry> Active(QuestClientState state, Func<string, int> carriedCount)
        {
            if (state == null || !state.IsReady)
            {
                return Array.Empty<QuestLogActiveEntry>();
            }

            return state.ActiveQuests.Values
                .Select(active => state.Catalog.TryGet(active.QuestId, out var quest)
                    ? new QuestLogActiveEntry(
                        quest,
                        active,
                        QuestProgress.CurrentObjectives(quest, active, carriedCount),
                        QuestProgress.IsReadyToTurnIn(quest, active, carriedCount))
                    : null)
                .Where(entry => entry != null)
                .OrderBy(entry => entry.Quest.Title, StringComparer.Ordinal)
                .ToArray();
        }

        public static IReadOnlyList<QuestLogCompletedEntry> Completed(QuestClientState state)
        {
            if (state == null || !state.IsReady)
            {
                return Array.Empty<QuestLogCompletedEntry>();
            }

            return state.CompletionCounts
                .Select(pair => state.Catalog.TryGet(pair.Key, out var quest)
                    ? new QuestLogCompletedEntry(quest, pair.Value)
                    : null)
                .Where(entry => entry != null)
                .OrderBy(entry => entry.Quest.Title, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
