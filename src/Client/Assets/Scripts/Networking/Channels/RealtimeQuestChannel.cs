using System;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // The quests of the controlled character.
    public interface IRealtimeQuestChannel
    {
        // Every quest one committed change of the controlled character touched, sent before the
        // result of the operation that caused it.
        event Action<RealtimeQuestLogChanged> QuestLogChanged;

        event Action<RealtimeQuestIntentResult> QuestIntentCompleted;

        // A quest item that a dying Mob left in its corpse for the controlled character's quest.
        event Action<RealtimeQuestDropInCorpse> QuestDropInCorpse;

        bool TrySendQuestIntent(RealtimeQuestIntent intent);
    }

    internal sealed class RealtimeQuestChannel : IRealtimeQuestChannel
    {
        private static readonly Func<RealtimeQuestIntent, byte[]> EncodeIntent =
            RealtimeProtocol.EncodeQuestIntent;

        private readonly RealtimeSession session;
        private readonly RealtimeSender sender;

        public RealtimeQuestChannel(
            RealtimeSession session,
            RealtimeSender sender,
            RealtimeMessageTable messages)
        {
            this.session = session;
            this.sender = sender;
            messages.Register(
                RealtimeMessageType.QuestIntentResult,
                HandleQuestIntentResult,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_quest_intent_result",
                "A quest intent result arrived without an active simulation session.");
            messages.Register(
                RealtimeMessageType.QuestLogChanged,
                HandleQuestLogChanged,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_quest_log_change",
                "A quest log change arrived without an active simulation session.");
            messages.Register(
                RealtimeMessageType.QuestDropInCorpse,
                HandleQuestDropInCorpse,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_quest_drop",
                "A quest drop arrived without an active simulation session.");
        }

        public event Action<RealtimeQuestLogChanged> QuestLogChanged;

        public event Action<RealtimeQuestIntentResult> QuestIntentCompleted;

        public event Action<RealtimeQuestDropInCorpse> QuestDropInCorpse;

        public bool TrySendQuestIntent(RealtimeQuestIntent intent)
        {
            return sender.TrySendIntent(intent, EncodeIntent, "Quest intent", RealtimeIntentKinds.Quest, intent?.OperationId ?? Guid.Empty);
        }

        private void HandleQuestIntentResult(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeQuestIntentResult(packet, out var result, out var error))
            {
                session.FailProtocol("invalid_quest_intent_result", error);
                return;
            }

            sender.RecordIntentAnswered(result.OperationId);
            QuestIntentCompleted?.Invoke(result);
        }

        private void HandleQuestLogChanged(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeQuestLogChanged(packet, out var change, out var error))
            {
                session.FailProtocol("invalid_quest_log_change", error);
                return;
            }

            QuestLogChanged?.Invoke(change);
        }

        private void HandleQuestDropInCorpse(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeQuestDropInCorpse(packet, out var drop, out var error))
            {
                session.FailProtocol("invalid_quest_drop", error);
                return;
            }

            QuestDropInCorpse?.Invoke(drop);
        }
    }
}
