using System;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // The combat of the joined session: the intents of the controlled character, its combat
    // state, and the state and events of every combatant it sees.
    public interface IRealtimeCombatChannel
    {
        event Action<RealtimeCombatIntentResult> CombatIntentCompleted;

        // The state of a combatant with reliable presence: a spawned player, a spawned world
        // actor, or the controlled entity.
        event Action<RealtimeCombatantState> CombatantStateReceived;

        event Action<RealtimeCombatEventBatch> CombatEventBatchReceived;

        event Action<RealtimeOwnerCombatState> OwnerCombatStateReceived;

        bool TrySendCombatIntent(RealtimeCombatIntent intent);
    }

    internal sealed class RealtimeCombatChannel : IRealtimeCombatChannel
    {
        private static readonly Func<RealtimeCombatIntent, byte[]> EncodeIntent =
            RealtimeProtocol.EncodeCombatIntent;

        private readonly RealtimeSession session;
        private readonly RealtimePresenceRegistry presence;
        private readonly RealtimeSender sender;

        public RealtimeCombatChannel(
            RealtimeSession session,
            RealtimePresenceRegistry presence,
            RealtimeSender sender,
            RealtimeMessageTable messages)
        {
            this.session = session;
            this.presence = presence;
            this.sender = sender;
            messages.Register(
                RealtimeMessageType.CombatIntentResult,
                HandleCombatIntentResult,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_combat_intent_result",
                "A combat intent result arrived without an active simulation session.");
            messages.Register(
                RealtimeMessageType.CombatantState,
                HandleCombatantState,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_combatant_state",
                "Combatant state arrived without an active simulation session.");
            messages.Register(
                RealtimeMessageType.CombatEventBatch,
                HandleCombatEventBatch,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_combat_event_batch",
                "Combat events arrived without an active simulation session.");
            messages.Register(
                RealtimeMessageType.OwnerCombatState,
                HandleOwnerCombatState,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_owner_combat_state",
                "Owner combat state arrived without an active simulation session.");
        }

        public event Action<RealtimeCombatIntentResult> CombatIntentCompleted;

        public event Action<RealtimeCombatantState> CombatantStateReceived;

        public event Action<RealtimeCombatEventBatch> CombatEventBatchReceived;

        public event Action<RealtimeOwnerCombatState> OwnerCombatStateReceived;

        public bool TrySendCombatIntent(RealtimeCombatIntent intent)
        {
            return sender.TrySendIntent(intent, EncodeIntent, "Combat intent");
        }

        private void HandleCombatIntentResult(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeCombatIntentResult(
                    packet,
                    out var result,
                    out var error))
            {
                session.FailProtocol("invalid_combat_intent_result", error);
                return;
            }

            CombatIntentCompleted?.Invoke(result);
        }

        // The worker sends a combatant's state only after its reliable presence.
        private void HandleCombatantState(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeCombatantState(
                    packet,
                    out var state,
                    out var error))
            {
                session.FailProtocol("invalid_combatant_state", error);
                return;
            }

            if (!presence.HasCombatantPresence(state.EntityId, session.MovementSession.ControlledEntityId))
            {
                session.FailProtocol(
                    "combatant_state_without_spawn",
                    "Combatant state arrived before reliable entity presence.");
                return;
            }

            CombatantStateReceived?.Invoke(state);
        }

        private void HandleCombatEventBatch(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeCombatEventBatch(
                    packet,
                    out var batch,
                    out var error))
            {
                session.FailProtocol("invalid_combat_event_batch", error);
                return;
            }

            CombatEventBatchReceived?.Invoke(batch);
        }

        private void HandleOwnerCombatState(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeOwnerCombatState(
                    packet,
                    out var state,
                    out var error))
            {
                session.FailProtocol("invalid_owner_combat_state", error);
                return;
            }

            OwnerCombatStateReceived?.Invoke(state);
        }
    }
}
