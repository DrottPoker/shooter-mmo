using System;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // The world interactions of the controlled character and its gathering.
    public interface IRealtimeWorldInteractionChannel
    {
        event Action<RealtimeWorldInteractionOpened> WorldInteractionOpened;

        event Action<RealtimeWorldInteractionResult> WorldInteractionCompleted;

        event Action<RealtimeWorldInteractionClosed> WorldInteractionClosed;

        event Action<RealtimeGatheringState> GatheringStateReceived;

        bool TrySendWorldInteraction(RealtimeWorldInteractionIntent intent);
    }

    internal sealed class RealtimeWorldInteractionChannel : IRealtimeWorldInteractionChannel
    {
        private static readonly Func<RealtimeWorldInteractionIntent, byte[]> EncodeIntent =
            RealtimeProtocol.EncodeWorldInteractionIntent;

        private readonly RealtimeSession session;
        private readonly RealtimeSender sender;

        public RealtimeWorldInteractionChannel(
            RealtimeSession session,
            RealtimeSender sender,
            RealtimeMessageTable messages)
        {
            this.session = session;
            this.sender = sender;
            messages.Register(
                RealtimeMessageType.WorldInteractionOpened,
                HandleWorldInteractionOpened,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_world_interaction_opened",
                "World interaction opened state arrived without an active simulation session.");
            messages.Register(
                RealtimeMessageType.WorldInteractionResult,
                HandleWorldInteractionResult,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_world_interaction_result",
                "World interaction result arrived without an active simulation session.");
            messages.Register(
                RealtimeMessageType.WorldInteractionClosed,
                HandleWorldInteractionClosed,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_world_interaction_closed",
                "World interaction closure arrived without an active simulation session.");
            messages.Register(
                RealtimeMessageType.GatheringState,
                HandleGatheringState,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_gathering_state",
                "Gathering state arrived without an active simulation session.");
        }

        public event Action<RealtimeWorldInteractionOpened> WorldInteractionOpened;

        public event Action<RealtimeWorldInteractionResult> WorldInteractionCompleted;

        public event Action<RealtimeWorldInteractionClosed> WorldInteractionClosed;

        public event Action<RealtimeGatheringState> GatheringStateReceived;

        public bool TrySendWorldInteraction(RealtimeWorldInteractionIntent intent)
        {
            return sender.TrySendIntent(intent, EncodeIntent, "World interaction", RealtimeIntentKinds.WorldInteraction, intent?.OperationId ?? Guid.Empty);
        }

        private void HandleWorldInteractionOpened(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeWorldInteractionOpened(
                    packet,
                    out var opened,
                    out var error))
            {
                session.FailProtocol("invalid_world_interaction_opened", error);
                return;
            }

            sender.RecordIntentAnswered(opened.OperationId);
            WorldInteractionOpened?.Invoke(opened);
        }

        private void HandleWorldInteractionResult(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeWorldInteractionResult(
                    packet,
                    out var result,
                    out var error))
            {
                session.FailProtocol("invalid_world_interaction_result", error);
                return;
            }

            sender.RecordIntentAnswered(result.OperationId);
            WorldInteractionCompleted?.Invoke(result);
        }

        private void HandleWorldInteractionClosed(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeWorldInteractionClosed(
                    packet,
                    out var closed,
                    out var error))
            {
                session.FailProtocol("invalid_world_interaction_closed", error);
                return;
            }

            WorldInteractionClosed?.Invoke(closed);
        }

        private void HandleGatheringState(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeGatheringState(
                    packet,
                    out var state,
                    out var error))
            {
                session.FailProtocol("invalid_gathering_state", error);
                return;
            }

            GatheringStateReceived?.Invoke(state);
        }
    }
}
