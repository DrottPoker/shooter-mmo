using System;

namespace Mmorpg.GameProtocol
{
    // Cross-field item rules. The schema generates the models and codecs, and the
    // generated validation calls these after every field rule passed.
    public static partial class RealtimeProtocol
    {
        // A merge or swap needs two different items.
        private static bool SatisfiesRules(RealtimeItemOperationIntent intent)
        {
            return (intent.OperationKind != RealtimeItemOperationKind.MergeStacks
                    && intent.OperationKind != RealtimeItemOperationKind.SwapContainerItems)
                || intent.ItemInstanceId != intent.TargetItemInstanceId;
        }

        // A failed operation commits nothing.
        private static bool SatisfiesRules(RealtimeItemOperationResult result)
        {
            return result.Succeeded
                || (result.ItemRevisions.Length == 0
                    && result.ContainerRevisions.Length == 0
                    && result.RecoveryDeliveryIds.Length == 0);
        }

        // The change advances the item-state revision, and a used-up item has no revision.
        private static bool SatisfiesRules(RealtimeOwnerItemQuantityChanged change)
        {
            return change.CarryState.ItemStateRevision > change.PreviousItemStateRevision
                && (change.Quantity > 0 || change.ItemRevision == 0);
        }

        // One transaction advances the item-state revision by exactly one.
        private static bool SatisfiesRules(RealtimeOwnerItemStateChanged change)
        {
            return change.PreviousItemStateRevision < long.MaxValue
                && change.CarryState.ItemStateRevision == change.PreviousItemStateRevision + 1;
        }

        // A removed item carries only its id, a contained item its container and slot, and an
        // equipped item its equipment slot.
        private static bool SatisfiesRules(RealtimeItemStateChange change)
        {
            switch (change.Location)
            {
                case RealtimeItemStateLocationKind.Removed:
                    return change.DefinitionId.Length == 0
                        && change.Quantity == 0
                        && change.Revision == 0
                        && change.ContainerId == Guid.Empty
                        && change.SlotIndex == -1
                        && change.EquipmentSlotId.Length == 0
                        && change.Policies.Length == 0;
                case RealtimeItemStateLocationKind.Container:
                    return change.DefinitionId.Length > 0
                        && change.Quantity > 0
                        && change.ContainerId != Guid.Empty
                        && change.SlotIndex >= 0
                        && change.EquipmentSlotId.Length == 0;
                default:
                    return change.DefinitionId.Length > 0
                        && change.Quantity > 0
                        && change.ContainerId == Guid.Empty
                        && change.SlotIndex == -1
                        && change.EquipmentSlotId.Length > 0;
            }
        }

        // Whether the change is valid and fits one packet. The worker sends no change that
        // fails, and the client then refreshes instead.
        public static bool CanEncodeOwnerItemStateChanged(RealtimeOwnerItemStateChanged change)
        {
            return IsValidOwnerItemStateChanged(change)
                && HeaderSize + SizeOfOwnerItemStateChanged(change) <= MaximumPacketSize;
        }
    }
}
