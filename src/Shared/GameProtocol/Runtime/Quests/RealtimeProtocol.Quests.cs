namespace Mmorpg.GameProtocol
{
    // Cross-field quest rules. The schema generates the models and codecs, and the
    // generated validation calls these after every field rule passed.
    public static partial class RealtimeProtocol
    {
        // A change advances the quest log revision and lists its quests, unless it asks for a
        // reload. A quest that is no longer active has no step, which whitespace reads as, and no
        // step progress.
        private static bool SatisfiesRules(RealtimeQuestLogChanged change)
        {
            if (change.QuestLogRevision <= change.PreviousQuestLogRevision
                || change.ReloadRequired != (change.Quests.Length == 0))
            {
                return false;
            }

            foreach (var quest in change.Quests)
            {
                if (string.IsNullOrWhiteSpace(quest.StepId) && quest.Objectives.Length != 0)
                {
                    return false;
                }
            }

            return true;
        }

        // Whether the change fits one packet, so the worker sends it instead of a reload.
        public static bool CanEncodeQuestLogChanged(RealtimeQuestLogChanged change)
        {
            return IsValidQuestLogChanged(change)
                && HeaderSize + SizeOfQuestLogChanged(change) <= MaximumPacketSize;
        }
    }
}
