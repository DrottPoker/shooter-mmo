using System;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // The development admin tools of the controlled character, while the worker allows them.
    public interface IRealtimeAdminChannel
    {
        // The worker sent the development admin state of the controlled character; the movement
        // session walks at its speed already.
        event Action AdminStateChanged;

        event Action<RealtimeAdminIntentResult> AdminIntentCompleted;

        // The development admin state of the controlled character, or null while the worker
        // allows no admin tools or outside a joined session.
        RealtimeAdminStateChanged AdminState { get; }

        bool TrySendAdminIntent(RealtimeAdminIntent intent);
    }

    internal sealed class RealtimeAdminChannel : IRealtimeAdminChannel, IRealtimeSessionScoped
    {
        private static readonly Func<RealtimeAdminIntent, byte[]> EncodeIntent =
            RealtimeProtocol.EncodeAdminIntent;

        private readonly RealtimeSession session;
        private readonly RealtimeSender sender;

        public RealtimeAdminChannel(
            RealtimeSession session,
            RealtimeSender sender,
            RealtimeMessageTable messages)
        {
            this.session = session;
            this.sender = sender;
            session.Track(this);
            messages.Register(
                RealtimeMessageType.AdminIntentResult,
                HandleAdminIntentResult,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_admin_intent_result",
                "An admin intent result arrived without an active simulation session.");
            messages.Register(
                RealtimeMessageType.AdminStateChanged,
                HandleAdminStateChanged,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_admin_state",
                "An admin state arrived without an active movement session.");
        }

        public event Action AdminStateChanged;

        public event Action<RealtimeAdminIntentResult> AdminIntentCompleted;

        public RealtimeAdminStateChanged AdminState { get; private set; }

        public bool TrySendAdminIntent(RealtimeAdminIntent intent)
        {
            return sender.TrySendIntent(intent, EncodeIntent, "Admin intent", RealtimeIntentKinds.Admin, intent?.OperationId ?? Guid.Empty);
        }

        public void ClearSessionState()
        {
            AdminState = null;
        }

        private void HandleAdminIntentResult(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeAdminIntentResult(packet, out var result, out var error))
            {
                session.FailProtocol("invalid_admin_intent_result", error);
                return;
            }

            sender.RecordIntentAnswered(result.OperationId);
            AdminIntentCompleted?.Invoke(result);
        }

        private void HandleAdminStateChanged(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeAdminStateChanged(packet, out var state, out var error))
            {
                session.FailProtocol("invalid_admin_state", error);
                return;
            }

            AdminState = state;
            session.MovementSession.ApplyWalkSpeedPercent(state.SpeedPercent);
            AdminStateChanged?.Invoke();
        }
    }
}
