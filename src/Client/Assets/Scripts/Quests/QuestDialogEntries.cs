using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.Client.Quests
{
    // What a quest NPC can do for the player now, in the order the quest dialog lists it.
    public enum QuestDialogEntryKind
    {
        // A ready quest that this NPC completes.
        TurnIn,

        // A talk-to objective of a current step that names this NPC.
        TalkTo,

        // A deliver objective of a current step that names this NPC.
        Deliver,

        // A quest this NPC gives that the character may accept now.
        Offer,

        // An active quest this NPC gave whose start items the character no longer carries.
        LostStartItems,

        // An active quest this NPC completes once it is ready.
        InProgress
    }

    public sealed class QuestDialogEntry
    {
        public QuestDialogEntry(
            QuestDialogEntryKind kind,
            QuestDefinition quest,
            QuestClientActiveQuest active,
            QuestObjectiveDefinition objective = null)
        {
            Kind = kind;
            Quest = quest;
            Active = active;
            Objective = objective;
        }

        public QuestDialogEntryKind Kind { get; }

        public QuestDefinition Quest { get; }

        // The active quest, or null for an offer.
        public QuestClientActiveQuest Active { get; }

        // The talk-to or deliver objective, or null.
        public QuestObjectiveDefinition Objective { get; }
    }

    // The quest dialog's entries for one NPC, derived from the catalog, the quest log, and the
    // carried items with the shared rules. The servers decide every action.
    public static class QuestDialogEntries
    {
        // ownedCount counts the quest-granted items the character owns anywhere, for start items
        // to ask for again; without it, the carried items count.
        public static IReadOnlyList<QuestDialogEntry> For(
            string npcActorId,
            QuestClientState state,
            Func<string, int> carriedCount,
            Func<string, int> ownedCount = null)
        {
            var entries = new List<QuestDialogEntry>();
            if (string.IsNullOrEmpty(npcActorId) || state == null || !state.IsReady)
            {
                return entries;
            }

            var active = state.ActiveQuests.Values
                .Select(quest => state.Catalog.TryGet(quest.QuestId, out var definition)
                    ? new KeyValuePair<QuestDefinition, QuestClientActiveQuest>(definition, quest)
                    : default)
                .Where(pair => pair.Key != null)
                .OrderBy(pair => pair.Key.Title, StringComparer.Ordinal)
                .ToArray();
            foreach (var pair in active)
            {
                if (IsNpc(pair.Key.TurnInActorId, npcActorId)
                    && QuestProgress.IsReadyToTurnIn(pair.Key, pair.Value, carriedCount))
                {
                    entries.Add(new QuestDialogEntry(QuestDialogEntryKind.TurnIn, pair.Key, pair.Value));
                }
            }

            foreach (var pair in active)
            {
                foreach (var line in QuestProgress.CurrentObjectives(pair.Key, pair.Value, carriedCount))
                {
                    var objective = line.Objective;
                    if (line.IsComplete || !IsNpc(objective.NpcActorId, npcActorId))
                    {
                        continue;
                    }

                    if (string.Equals(objective.Kind, QuestObjectiveKindIds.TalkTo, StringComparison.Ordinal))
                    {
                        entries.Add(new QuestDialogEntry(QuestDialogEntryKind.TalkTo, pair.Key, pair.Value, objective));
                    }
                    else if (string.Equals(objective.Kind, QuestObjectiveKindIds.Deliver, StringComparison.Ordinal))
                    {
                        entries.Add(new QuestDialogEntry(QuestDialogEntryKind.Deliver, pair.Key, pair.Value, objective));
                    }
                }
            }

            foreach (var quest in state.Catalog.QuestsGivenBy(npcActorId))
            {
                if (state.AvailabilityOf(quest) == QuestAvailability.Available)
                {
                    entries.Add(new QuestDialogEntry(QuestDialogEntryKind.Offer, quest, null));
                }
            }

            foreach (var pair in active)
            {
                if (IsNpc(pair.Key.GiverActorId, npcActorId) && MissesStartItems(pair.Key, pair.Value, ownedCount ?? carriedCount))
                {
                    entries.Add(new QuestDialogEntry(QuestDialogEntryKind.LostStartItems, pair.Key, pair.Value));
                }
            }

            foreach (var pair in active)
            {
                if (IsNpc(pair.Key.TurnInActorId, npcActorId)
                    && !QuestProgress.IsReadyToTurnIn(pair.Key, pair.Value, carriedCount))
                {
                    entries.Add(new QuestDialogEntry(QuestDialogEntryKind.InProgress, pair.Key, pair.Value));
                }
            }

            return entries;
        }

        // A delivery hands over what the objective still lacks at once, so the player must carry
        // all of it; a count raised after a part was handed over asks only for the rest.
        public static bool CanHandOver(QuestDialogEntry entry, Func<string, int> carriedCount)
        {
            return entry != null
                && entry.Kind == QuestDialogEntryKind.Deliver
                && entry.Objective != null
                && carriedCount != null
                && carriedCount(entry.Objective.ItemId) >= RemainingDelivery(entry);
        }

        // What the delivery of the entry still hands over.
        public static int RemainingDelivery(QuestDialogEntry entry)
        {
            return QuestRules.RemainingDeliveryQuantity(
                entry.Objective,
                entry.Active != null ? entry.Active.ProgressOf(entry.Objective.Id) : 0);
        }

        // A start item that the quest still needs after its deliveries and that the character
        // owns fewer of, which GameService grants again, by the same shared rule.
        public static bool MissesStartItems(QuestDefinition quest, QuestClientActiveQuest active, Func<string, int> ownedCount)
        {
            if (quest.StartItems == null || active == null || ownedCount == null)
            {
                return false;
            }

            var stepIndex = QuestProgress.StepIndexOf(quest, active.StepId);
            return stepIndex >= 0
                && quest.StartItems.Any(item => item != null
                    && QuestRules.MissingStartItemQuantity(
                        quest,
                        item,
                        stepIndex,
                        active.ProgressOf,
                        ownedCount(item.DefinitionId)) > 0);
        }

        private static bool IsNpc(string actorId, string npcActorId)
        {
            return !string.IsNullOrEmpty(actorId) && string.Equals(actorId, npcActorId, StringComparison.Ordinal);
        }
    }
}
