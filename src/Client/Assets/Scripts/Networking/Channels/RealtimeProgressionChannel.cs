using System;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // The level and experience of the controlled character, and the level-ups of the characters
    // the client sees. The worker sends every value as GameService computed it; the client never
    // computes a level or an experience value itself.
    public interface IRealtimeProgressionChannel
    {
        // The worker sent the controlled character's progression: after every join, and after
        // every change, before the quest log change and the result of the operation that caused it.
        event Action<RealtimeOwnerProgressionChanged> ProgressionChanged;

        // A character the client sees in detail reached a higher level; never the controlled one.
        event Action<RealtimeEntityLeveledUp> EntityLeveledUp;

        // The controlled character's latest progression, or null before the first one of a joined
        // session.
        RealtimeOwnerProgressionChanged Progression { get; }
    }

    internal sealed class RealtimeProgressionChannel : IRealtimeProgressionChannel, IRealtimeSessionScoped
    {
        private readonly RealtimeSession session;

        public RealtimeProgressionChannel(RealtimeSession session, RealtimeMessageTable messages)
        {
            this.session = session;
            session.Track(this);
            messages.Register(
                RealtimeMessageType.OwnerProgressionChanged,
                HandleOwnerProgressionChanged,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_owner_progression",
                "An owner progression arrived without an active simulation session.");
            messages.Register(
                RealtimeMessageType.EntityLeveledUp,
                HandleEntityLeveledUp,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_entity_leveled_up",
                "A level-up arrived without an active simulation session.");
        }

        public event Action<RealtimeOwnerProgressionChanged> ProgressionChanged;

        public event Action<RealtimeEntityLeveledUp> EntityLeveledUp;

        public RealtimeOwnerProgressionChanged Progression { get; private set; }

        public void ClearSessionState()
        {
            Progression = null;
        }

        private void HandleOwnerProgressionChanged(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeOwnerProgressionChanged(packet, out var change, out var error))
            {
                session.FailProtocol("invalid_owner_progression", error);
                return;
            }

            Progression = change;
            ProgressionChanged?.Invoke(change);
        }

        private void HandleEntityLeveledUp(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeEntityLeveledUp(packet, out var levelUp, out var error))
            {
                session.FailProtocol("invalid_entity_leveled_up", error);
                return;
            }

            EntityLeveledUp?.Invoke(levelUp);
        }
    }
}
