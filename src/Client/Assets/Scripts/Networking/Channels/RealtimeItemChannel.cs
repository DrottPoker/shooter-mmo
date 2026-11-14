using System;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // The item operations of the controlled character and the item changes the worker sends.
    // Each message that carries a carry state advances the movement session first, so its
    // CarryStateChanged comes before its own event.
    public interface IRealtimeItemChannel
    {
        event Action<RealtimeItemOperationResult> ItemOperationCompleted;

        event Action<RealtimeOwnerItemQuantityChanged> OwnerItemQuantityChanged;

        event Action<RealtimeOwnerItemStateChanged> OwnerItemStateChanged;

        // The item-state revision of the newest committed carry state the worker sent, or -1
        // outside a joined session.
        long CarryItemStateRevision { get; }

        bool TrySendItemOperation(RealtimeItemOperationIntent intent);
    }

    internal sealed class RealtimeItemChannel : IRealtimeItemChannel
    {
        private static readonly Func<RealtimeItemOperationIntent, byte[]> EncodeIntent =
            RealtimeProtocol.EncodeItemOperationIntent;

        private readonly RealtimeSession session;
        private readonly RealtimeMovementChannel movement;
        private readonly RealtimeSender sender;

        public RealtimeItemChannel(
            RealtimeSession session,
            RealtimeMovementChannel movement,
            RealtimeSender sender,
            RealtimeMessageTable messages)
        {
            this.session = session;
            this.movement = movement;
            this.sender = sender;
            messages.Register(
                RealtimeMessageType.ItemOperationResult,
                HandleItemOperationResult,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_item_operation_result",
                "An item operation result arrived without an active movement session.");
            messages.Register(
                RealtimeMessageType.OwnerItemQuantityChanged,
                HandleOwnerItemQuantityChanged,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_owner_item_quantity_changed",
                "An item quantity change arrived without an active movement session.");
            messages.Register(
                RealtimeMessageType.OwnerItemStateChanged,
                HandleOwnerItemStateChanged,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_owner_item_state_changed",
                "An item state change arrived without an active movement session.");
        }

        public event Action<RealtimeItemOperationResult> ItemOperationCompleted;

        public event Action<RealtimeOwnerItemQuantityChanged> OwnerItemQuantityChanged;

        public event Action<RealtimeOwnerItemStateChanged> OwnerItemStateChanged;

        public long CarryItemStateRevision
        {
            get
            {
                var movementSession = session.MovementSession;
                return movementSession != null ? movementSession.CarryState.ItemStateRevision : -1;
            }
        }

        public bool TrySendItemOperation(RealtimeItemOperationIntent intent)
        {
            return sender.TrySendIntent(intent, EncodeIntent, "Item operation", RealtimeIntentKinds.Item, intent?.OperationId ?? Guid.Empty);
        }

        private void HandleItemOperationResult(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeItemOperationResult(
                    packet,
                    out var result,
                    out var error)
                || !movement.TryAdvanceCarryState(result.CarryState, out error))
            {
                session.FailProtocol("invalid_item_operation_result", error);
                return;
            }

            sender.RecordIntentAnswered(result.OperationId);
            ItemOperationCompleted?.Invoke(result);
        }

        private void HandleOwnerItemQuantityChanged(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeOwnerItemQuantityChanged(
                    packet,
                    out var change,
                    out var error)
                || !movement.TryAdvanceCarryState(change.CarryState, out error))
            {
                session.FailProtocol("invalid_owner_item_quantity_changed", error);
                return;
            }

            OwnerItemQuantityChanged?.Invoke(change);
        }

        private void HandleOwnerItemStateChanged(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeOwnerItemStateChanged(
                    packet,
                    out var change,
                    out var error)
                || !movement.TryAdvanceCarryState(change.CarryState, out error))
            {
                session.FailProtocol("invalid_owner_item_state_changed", error);
                return;
            }

            OwnerItemStateChanged?.Invoke(change);
        }
    }
}
