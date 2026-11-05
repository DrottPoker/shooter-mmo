using System;

namespace Mmorpg.Client.Quests
{
    // When the quest log is read again after a read failed: after a delay that starts at 2 s and
    // doubles up to 30 s, so a short GameService outage never leaves the quests unavailable for
    // the rest of the session. A quest change or a quest window that needs the log reads it at
    // once instead.
    public sealed class QuestLogRetry
    {
        public const float InitialDelaySeconds = 2f;
        public const float MaximumDelaySeconds = 30f;

        private float delaySeconds = InitialDelaySeconds;
        private float dueAt = float.PositiveInfinity;

        public void Failed(float now)
        {
            dueAt = now + delaySeconds;
            delaySeconds = Math.Min(delaySeconds * 2f, MaximumDelaySeconds);
        }

        // A read that succeeded, or another character, starts over.
        public void Reset()
        {
            delaySeconds = InitialDelaySeconds;
            dueAt = float.PositiveInfinity;
        }

        public bool IsDue(float now)
        {
            return now >= dueAt;
        }
    }
}
