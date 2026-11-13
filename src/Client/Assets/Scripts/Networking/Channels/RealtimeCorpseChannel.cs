using System;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // The corpse messages of the joined session. EditMode tests drive the corpse controller
    // through a fake.
    public interface IRealtimeCorpseChannel
    {
        event Action<RealtimeCorpsePresenceSnapshotChunk> CorpsePresenceChunkReceived;

        // Raised after the result's carry state advanced the movement session.
        event Action<RealtimeCorpseInteractionResult> CorpseInteractionCompleted;

        event Action<RealtimeCorpseViewStateChunk> CorpseViewStateChunkReceived;

        event Action<RealtimeCorpseViewClosed> CorpseViewClosed;

        bool TrySendCorpseInteraction(RealtimeCorpseInteractionIntent intent);
    }

    internal sealed class RealtimeCorpseChannel : IRealtimeCorpseChannel
    {
        private static readonly Func<RealtimeCorpseInteractionIntent, byte[]> EncodeIntent =
            RealtimeProtocol.EncodeCorpseInteractionIntent;

        private readonly RealtimeSession session;
        private readonly RealtimeMovementChannel movement;
        private readonly RealtimeSender sender;

        public RealtimeCorpseChannel(
            RealtimeSession session,
            RealtimeMovementChannel movement,
            RealtimeSender sender,
            RealtimeMessageTable messages)
        {
            this.session = session;
            this.movement = movement;
            this.sender = sender;
            messages.Register(
                RealtimeMessageType.CorpsePresenceSnapshotChunk,
                HandleCorpsePresenceChunk,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_corpse_presence",
                "Corpse presence arrived without an active simulation session.");
            messages.Register(
                RealtimeMessageType.CorpseInteractionResult,
                HandleCorpseInteractionResult,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_corpse_operation_result",
                "A corpse operation result arrived without an active movement session.");
            messages.Register(
                RealtimeMessageType.CorpseViewStateChunk,
                HandleCorpseViewStateChunk,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_corpse_view_state",
                "Corpse view state arrived without an active simulation session.");
            messages.Register(
                RealtimeMessageType.CorpseViewClosed,
                HandleCorpseViewClosed,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_corpse_view_closed",
                "Corpse view closure arrived without an active simulation session.");
        }

        public event Action<RealtimeCorpsePresenceSnapshotChunk> CorpsePresenceChunkReceived;

        public event Action<RealtimeCorpseInteractionResult> CorpseInteractionCompleted;

        public event Action<RealtimeCorpseViewStateChunk> CorpseViewStateChunkReceived;

        public event Action<RealtimeCorpseViewClosed> CorpseViewClosed;

        public bool TrySendCorpseInteraction(RealtimeCorpseInteractionIntent intent)
        {
            return sender.TrySendIntent(intent, EncodeIntent, "Corpse interaction");
        }

        private void HandleCorpsePresenceChunk(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeCorpsePresenceSnapshotChunk(
                    packet,
                    out var chunk,
                    out var error))
            {
                session.FailProtocol("invalid_corpse_presence", error);
                return;
            }

            CorpsePresenceChunkReceived?.Invoke(chunk);
        }

        private void HandleCorpseInteractionResult(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeCorpseInteractionResult(
                    packet,
                    out var result,
                    out var error)
                || !movement.TryAdvanceCarryState(result.CarryState, out error))
            {
                session.FailProtocol("invalid_corpse_operation_result", error);
                return;
            }

            CorpseInteractionCompleted?.Invoke(result);
        }

        private void HandleCorpseViewStateChunk(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeCorpseViewStateChunk(
                    packet,
                    out var chunk,
                    out var error))
            {
                session.FailProtocol("invalid_corpse_view_state", error);
                return;
            }

            CorpseViewStateChunkReceived?.Invoke(chunk);
        }

        private void HandleCorpseViewClosed(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeCorpseViewClosed(
                    packet,
                    out var closed,
                    out var error))
            {
                session.FailProtocol("invalid_corpse_view_closed", error);
                return;
            }

            CorpseViewClosed?.Invoke(closed);
        }
    }
}
