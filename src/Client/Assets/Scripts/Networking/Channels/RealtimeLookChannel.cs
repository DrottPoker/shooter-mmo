using System;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // How the players of the joined session look, and the look changes this client asks for.
    public interface IRealtimeLookChannel
    {
        // A spawned player, or the controlled one, changed how it looks; the look is stored
        // before the event.
        event Action<RealtimeEntityAppearanceChanged> EntityAppearanceChanged;

        event Action<RealtimeAppearanceIntentResult> AppearanceIntentCompleted;

        // The latest look of a spawned player or of the controlled one, which the worker sends
        // after the join.
        bool TryGetAppearance(ulong entityId, out RealtimeCharacterAppearance appearance);

        bool TrySendAppearanceIntent(RealtimeAppearanceIntent intent);
    }

    internal sealed class RealtimeLookChannel : IRealtimeLookChannel
    {
        private static readonly Func<RealtimeAppearanceIntent, byte[]> EncodeIntent =
            RealtimeProtocol.EncodeAppearanceIntent;

        private readonly RealtimeSession session;
        private readonly RealtimePresenceRegistry presence;
        private readonly RealtimeSender sender;

        public RealtimeLookChannel(
            RealtimeSession session,
            RealtimePresenceRegistry presence,
            RealtimeSender sender,
            RealtimeMessageTable messages)
        {
            this.session = session;
            this.presence = presence;
            this.sender = sender;
            messages.Register(
                RealtimeMessageType.EntityAppearanceChanged,
                HandleEntityAppearanceChanged,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_entity_appearance_changed",
                "An appearance change arrived without an active movement session.");
            messages.Register(
                RealtimeMessageType.AppearanceIntentResult,
                HandleAppearanceIntentResult,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_appearance_intent_result",
                "An appearance intent result arrived without an active simulation session.");
        }

        public event Action<RealtimeEntityAppearanceChanged> EntityAppearanceChanged;

        public event Action<RealtimeAppearanceIntentResult> AppearanceIntentCompleted;

        public bool TryGetAppearance(ulong entityId, out RealtimeCharacterAppearance appearance)
        {
            return presence.TryGetLook(entityId, out appearance);
        }

        public bool TrySendAppearanceIntent(RealtimeAppearanceIntent intent)
        {
            return sender.TrySendIntent(intent, EncodeIntent, "Appearance intent", RealtimeIntentKinds.Appearance, intent?.OperationId ?? Guid.Empty);
        }

        // A look for a player the client does not see, as one that despawned while the change
        // was on its way, is dropped.
        private void HandleEntityAppearanceChanged(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeEntityAppearanceChanged(packet, out var change, out var error))
            {
                session.FailProtocol("invalid_entity_appearance_changed", error);
                return;
            }

            if (change.EntityId != session.MovementSession.ControlledEntityId
                && !presence.IsPlayer(change.EntityId))
            {
                return;
            }

            presence.SetLook(change.EntityId, change.Appearance);
            EntityAppearanceChanged?.Invoke(change);
        }

        private void HandleAppearanceIntentResult(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeAppearanceIntentResult(packet, out var result, out var error))
            {
                session.FailProtocol("invalid_appearance_intent_result", error);
                return;
            }

            sender.RecordIntentAnswered(result.OperationId);
            AppearanceIntentCompleted?.Invoke(result);
        }
    }
}
