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
    }
}
