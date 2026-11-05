using System;
using System.Linq;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.Client.Quests
{
    // What waits for the player at an NPC, as the marker over it shows. A later value is more
    // urgent, so an NPC shows the most urgent of what waits there.
    public enum QuestMarker
    {
        None,

        // A quest that only needs a higher level: a grey !.
        HigherLevelQuest,

        // An active quest that this NPC completes once it is ready, or a delivery for which the
        // player does not carry enough yet: a grey ?.
        InProgress,

        // A quest to accept, or start items to ask for again: a yellow !.
        Available,

        // A quest to turn in, an objective to talk about, or items to hand over: a yellow ?.
        Ready
    }

    // The marker over a quest NPC, derived from the quest dialog's entries for that NPC and the
    // quests it gives that only need a higher level. The servers decide every action.
    public static class QuestMarkers
    {
        public static QuestMarker For(
            string npcActorId,
            QuestClientState state,
            Func<string, int> carriedCount,
            Func<string, int> ownedCount = null)
        {
            var marker = QuestMarker.None;
            foreach (var entry in QuestDialogEntries.For(npcActorId, state, carriedCount, ownedCount))
            {
                var entryMarker = MarkerOf(entry, carriedCount);
                if (entryMarker > marker)
                {
                    marker = entryMarker;
                }
            }

            if (marker == QuestMarker.None
                && state != null
                && state.IsReady
                && state.Catalog.Quests.Any(quest =>
                    string.Equals(quest.GiverActorId, npcActorId, StringComparison.Ordinal)
                    && state.AvailabilityOf(quest) == QuestAvailability.BelowMinimumLevel))
            {
                marker = QuestMarker.HigherLevelQuest;
            }

            return marker;
        }

        private static QuestMarker MarkerOf(QuestDialogEntry entry, Func<string, int> carriedCount)
        {
            switch (entry.Kind)
            {
                case QuestDialogEntryKind.TurnIn:
                case QuestDialogEntryKind.TalkTo:
                    return QuestMarker.Ready;
                case QuestDialogEntryKind.Deliver:
                    return QuestDialogEntries.CanHandOver(entry, carriedCount)
                        ? QuestMarker.Ready
                        : QuestMarker.InProgress;
                case QuestDialogEntryKind.Offer:
                case QuestDialogEntryKind.LostStartItems:
                    return QuestMarker.Available;
                case QuestDialogEntryKind.InProgress:
                    return QuestMarker.InProgress;
                default:
                    return QuestMarker.None;
            }
        }
    }
}
