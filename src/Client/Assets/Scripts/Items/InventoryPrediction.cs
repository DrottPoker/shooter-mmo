using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
    // One item operation the player made that the client shows before SimulationWorker answers.
    // It names items and destinations only; the revisions the intent expects come from the
    // confirmed state when the operation is sent.
    public sealed class InventoryPredictedOperation
    {
        private InventoryPredictedOperation(
            RealtimeItemOperationKind kind,
            Guid itemInstanceId,
            Guid targetItemInstanceId,
            Guid destinationContainerId,
            int destinationSlotIndex,
            int quantity,
            string equipmentSlotId,
            RecoveryDelivery recoveryDelivery)
        {
            Kind = kind;
            ItemInstanceId = itemInstanceId;
            TargetItemInstanceId = targetItemInstanceId;
            DestinationContainerId = destinationContainerId;
            DestinationSlotIndex = destinationSlotIndex;
            Quantity = quantity;
            EquipmentSlotId = equipmentSlotId ?? string.Empty;
            RecoveryDelivery = recoveryDelivery;
            // A split shows its new stack under an id the server replaces when it commits.
            PredictedItemInstanceId = kind == RealtimeItemOperationKind.SplitStack ? Guid.NewGuid() : Guid.Empty;
        }

        public RealtimeItemOperationKind Kind { get; }

        public Guid ItemInstanceId { get; }

        public Guid TargetItemInstanceId { get; }

        public Guid DestinationContainerId { get; }

        public int DestinationSlotIndex { get; }

        public int Quantity { get; }

        public string EquipmentSlotId { get; }

        public RecoveryDelivery RecoveryDelivery { get; }

        public Guid PredictedItemInstanceId { get; }

        // False for an operation the client cannot show ahead, such as a Recovery claim or a
        // Bag that moves with its contents. It waits alone and blocks later operations.
        public bool IsPredicted { get; internal set; } = true;

        public Guid OperationId { get; internal set; }

        public bool IsSent
        {
            get { return OperationId != Guid.Empty; }
        }

        // The item-state revision the operation committed, or -1 before its result.
        public long CommittedRevision { get; internal set; } = -1;

        public InventoryRefreshScope RejectionRefreshScope { get; internal set; } = InventoryRefreshScope.Full;

        public static InventoryPredictedOperation Relocate(Guid itemInstanceId, Guid destinationContainerId, int destinationSlotIndex)
        {
            return new InventoryPredictedOperation(
                RealtimeItemOperationKind.Relocate,
                itemInstanceId,
                Guid.Empty,
                destinationContainerId,
                destinationSlotIndex,
                0,
                string.Empty,
                null);
        }

        public static InventoryPredictedOperation Unequip(Guid itemInstanceId, Guid destinationContainerId, int destinationSlotIndex)
        {
            return new InventoryPredictedOperation(
                RealtimeItemOperationKind.Unequip,
                itemInstanceId,
                Guid.Empty,
                destinationContainerId,
                destinationSlotIndex,
                0,
                string.Empty,
                null);
        }

        public static InventoryPredictedOperation Equip(Guid itemInstanceId, string equipmentSlotId)
        {
            return new InventoryPredictedOperation(
                RealtimeItemOperationKind.Equip,
                itemInstanceId,
                Guid.Empty,
                Guid.Empty,
                -1,
                0,
                equipmentSlotId,
                null);
        }

        public static InventoryPredictedOperation Split(
            Guid itemInstanceId,
            int quantity,
            Guid destinationContainerId,
            int destinationSlotIndex)
        {
            return new InventoryPredictedOperation(
                RealtimeItemOperationKind.SplitStack,
                itemInstanceId,
                Guid.Empty,
                destinationContainerId,
                destinationSlotIndex,
                quantity,
                string.Empty,
                null);
        }

        public static InventoryPredictedOperation Merge(Guid sourceItemInstanceId, Guid targetItemInstanceId)
        {
            return new InventoryPredictedOperation(
                RealtimeItemOperationKind.MergeStacks,
                sourceItemInstanceId,
                targetItemInstanceId,
                Guid.Empty,
                -1,
                0,
                string.Empty,
                null);
        }

        public static InventoryPredictedOperation Swap(Guid firstItemInstanceId, Guid secondItemInstanceId)
        {
            return new InventoryPredictedOperation(
                RealtimeItemOperationKind.SwapContainerItems,
                firstItemInstanceId,
                secondItemInstanceId,
                Guid.Empty,
                -1,
                0,
                string.Empty,
                null);
        }

        public static InventoryPredictedOperation Destroy(Guid itemInstanceId)
        {
            return new InventoryPredictedOperation(
                RealtimeItemOperationKind.Destroy,
                itemInstanceId,
                Guid.Empty,
                Guid.Empty,
                -1,
                0,
                string.Empty,
                null);
        }

        public static InventoryPredictedOperation ClaimRecovery(RecoveryDelivery delivery, Guid destinationContainerId)
        {
            return new InventoryPredictedOperation(
                RealtimeItemOperationKind.ClaimRecoveryDelivery,
                Guid.Empty,
                Guid.Empty,
                destinationContainerId,
                -1,
                0,
                string.Empty,
                delivery)
            {
                IsPredicted = false
            };
        }
    }

    // Applies one operation to an inventory the way GameService commits it, after the same
    // target rules the drop targets use. It is the client's prediction, never the authority:
    // the committed change or a reload replaces whatever it shows.
    public static class InventoryOperationPredictor
    {
        public static bool CanPredict(IInventoryView view, InventoryPredictedOperation operation)
        {
            if (operation == null || !operation.IsPredicted)
            {
                return false;
            }

            // An equipped Bag carries contents and capacity that only the server's state shows.
            return operation.Kind is not (RealtimeItemOperationKind.Equip or RealtimeItemOperationKind.Unequip)
                || view?.Catalog == null
                || !TryFind(view, operation.ItemInstanceId, out var item, out _)
                || !view.Catalog.TryGetDefinition(item.DefinitionId, out var definition)
                || definition.Bag == null;
        }

        public static bool TryApply(
            IInventoryView view,
            InventoryPredictedOperation operation,
            out CharacterInventorySnapshot predicted,
            out string reason)
        {
            predicted = null;
            reason = "The item is no longer where it was.";
            if (view?.Catalog == null || view.FullSnapshot == null || operation == null)
            {
                reason = "Inventory state is unavailable.";
                return false;
            }

            if (!TryFind(view, operation.ItemInstanceId, out var item, out var source)
                || !view.Catalog.TryGetDefinition(item.DefinitionId, out var definition))
            {
                return false;
            }

            var editor = new InventorySnapshotEditor(view.FullSnapshot, view.Bank, null);
            var weight = view.FullSnapshot.CarriedWeight;
            try
            {
                switch (operation.Kind)
                {
                    case RealtimeItemOperationKind.Relocate:
                    case RealtimeItemOperationKind.Unequip:
                    {
                        if ((operation.Kind == RealtimeItemOperationKind.Unequip)
                                != (source.Kind == InventoryItemLocationKind.Equipment)
                            || !TryResolveDestination(view, operation, item, source, item.Quantity, out var container, out var slot, out reason)
                            || (operation.Kind == RealtimeItemOperationKind.Unequip
                                ? !InventoryTargetAdvisor.CanUnequip(view, item, container, slot, out reason)
                                : !InventoryTargetAdvisor.CanPlaceInContainer(view, item, source, container, slot, out reason)))
                        {
                            return false;
                        }

                        editor.Remove(item.ItemInstanceId);
                        editor.TryPlace(container.ContainerId, slot.SlotIndex, item);
                        weight = MoveWeight(weight, definition, item.Quantity, source, container.ContainerType);
                        break;
                    }
                    case RealtimeItemOperationKind.Equip:
                    {
                        var equipmentSlot = view.FullSnapshot.Equipment.FirstOrDefault(candidate =>
                            string.Equals(candidate.SlotId, operation.EquipmentSlotId, StringComparison.Ordinal));
                        if (source.Kind != InventoryItemLocationKind.Container
                            || equipmentSlot == null
                            || !InventoryTargetAdvisor.CanEquip(view, item, source, equipmentSlot, out reason))
                        {
                            if (equipmentSlot == null)
                            {
                                reason = "The equipment slot is unavailable.";
                            }

                            return false;
                        }

                        var occupant = equipmentSlot.Item;
                        editor.Remove(item.ItemInstanceId);
                        weight = MoveWeight(weight, definition, item.Quantity, source, null);
                        if (occupant != null)
                        {
                            // The equipped item swaps into the slot the new item came from.
                            editor.Remove(occupant.ItemInstanceId);
                            editor.TryPlace(source.ContainerId, source.SlotIndex, occupant);
                            weight = MoveWeight(
                                weight,
                                view.Catalog.TryGetDefinition(occupant.DefinitionId, out var occupantDefinition)
                                    ? occupantDefinition
                                    : throw new OverflowException(),
                                occupant.Quantity,
                                InventoryItemLocation.ForEquipment(equipmentSlot.SlotId),
                                source.ContainerType);
                        }

                        editor.TryEquip(equipmentSlot.SlotId, item);
                        break;
                    }
                    case RealtimeItemOperationKind.SwapContainerItems:
                    {
                        if (!TryFind(view, operation.TargetItemInstanceId, out var second, out var secondLocation)
                            || !InventoryTargetAdvisor.CanSwap(view, item, source, second, secondLocation, out reason)
                            || !view.Catalog.TryGetDefinition(second.DefinitionId, out var secondDefinition))
                        {
                            return false;
                        }

                        editor.Remove(item.ItemInstanceId);
                        editor.Remove(second.ItemInstanceId);
                        editor.TryPlace(secondLocation.ContainerId, secondLocation.SlotIndex, item);
                        editor.TryPlace(source.ContainerId, source.SlotIndex, second);
                        weight = MoveWeight(weight, definition, item.Quantity, source, secondLocation.ContainerType);
                        weight = MoveWeight(weight, secondDefinition, second.Quantity, secondLocation, source.ContainerType);
                        break;
                    }
                    case RealtimeItemOperationKind.MergeStacks:
                    {
                        if (!TryFind(view, operation.TargetItemInstanceId, out var target, out var targetLocation)
                            || !InventoryTargetAdvisor.CanMerge(view, item, source, target, targetLocation, out reason))
                        {
                            return false;
                        }

                        editor.Remove(item.ItemInstanceId);
                        editor.TryReplace(
                            target.ItemInstanceId,
                            WithQuantity(target, target.Quantity + item.Quantity));
                        weight = MoveWeight(
                            weight,
                            definition,
                            item.Quantity,
                            source,
                            targetLocation.Kind == InventoryItemLocationKind.Equipment ? null : targetLocation.ContainerType);
                        break;
                    }
                    case RealtimeItemOperationKind.SplitStack:
                    {
                        if (!InventoryTargetAdvisor.CanSplit(view.Catalog, item, operation.Quantity, out reason)
                            || !TryResolveDestination(view, operation, item, source, operation.Quantity, out var container, out var slot, out reason)
                            || !InventoryTargetAdvisor.CanPlaceInContainer(view, item, source, container, slot, operation.Quantity, out reason))
                        {
                            return false;
                        }

                        editor.TryReplace(item.ItemInstanceId, WithQuantity(item, item.Quantity - operation.Quantity));
                        editor.TryPlace(
                            container.ContainerId,
                            slot.SlotIndex,
                            new InventoryItem(
                                operation.PredictedItemInstanceId,
                                item.DefinitionId,
                                operation.Quantity,
                                0,
                                item.Policies.ToArray()));
                        weight = MoveWeight(weight, definition, operation.Quantity, source, container.ContainerType);
                        break;
                    }
                    case RealtimeItemOperationKind.Destroy:
                    {
                        if (!InventoryTargetAdvisor.CanDestroy(view.Catalog, item, out reason))
                        {
                            return false;
                        }

                        editor.Remove(item.ItemInstanceId);
                        weight = MoveWeight(weight, definition, item.Quantity, source, null);
                        break;
                    }
                    default:
                        reason = "This operation cannot be shown before the server answers.";
                        return false;
                }

                predicted = editor.Build(
                    view.FullSnapshot.ItemStateRevision,
                    Math.Max(0, weight),
                    view.FullSnapshot.CarryCapacity,
                    view.FullSnapshot.Currency);
                reason = string.Empty;
                return true;
            }
            catch (OverflowException)
            {
                reason = "The item weight is invalid.";
                return false;
            }
        }

        private static bool TryFind(
            IInventoryView view,
            Guid itemInstanceId,
            out InventoryItem item,
            out InventoryItemLocation location)
        {
            item = null;
            location = null;
            return view.FullSnapshot != null
                && itemInstanceId != Guid.Empty
                && new InventorySnapshotEditor(view.FullSnapshot, view.Bank, null).TryFind(
                    itemInstanceId,
                    out item,
                    out location);
        }

        // An explicit slot, or the first free slot that accepts the item, as GameService picks.
        private static bool TryResolveDestination(
            IInventoryView view,
            InventoryPredictedOperation operation,
            InventoryItem item,
            InventoryItemLocation source,
            int quantity,
            out InventoryContainer container,
            out InventorySlot slot,
            out string reason)
        {
            reason = "The destination container is unavailable.";
            slot = null;
            container = new[]
                {
                    view.FullSnapshot.PermanentInventory,
                    view.FullSnapshot.EquippedBag?.Contents,
                    view.FullSnapshot.SecureContainer?.Contents,
                    view.Bank
                }
                .FirstOrDefault(candidate => candidate != null && candidate.ContainerId == operation.DestinationContainerId);
            if (container == null)
            {
                return false;
            }

            if (operation.DestinationSlotIndex >= 0)
            {
                if (container.TryGetSlot(operation.DestinationSlotIndex, out slot))
                {
                    return true;
                }

                reason = "The destination slot is unavailable.";
                return false;
            }

            foreach (var candidate in container.Slots)
            {
                if (candidate.Item == null
                    && InventoryTargetAdvisor.CanPlaceInContainer(view, item, source, container, candidate, quantity, out _))
                {
                    slot = candidate;
                    return true;
                }
            }

            reason = "The destination container has no free slot for the item.";
            return false;
        }

        // Carried weight counts carried containers only; equipment and storage weigh nothing.
        private static long MoveWeight(
            long weight,
            ItemDefinition definition,
            int quantity,
            InventoryItemLocation source,
            string destinationContainerType)
        {
            var sourceCarried = source.Kind == InventoryItemLocationKind.Container
                && ItemContainerTypeIds.IsCarried(source.ContainerType);
            var destinationCarried = destinationContainerType != null
                && ItemContainerTypeIds.IsCarried(destinationContainerType);
            if (sourceCarried == destinationCarried)
            {
                return weight;
            }

            var stackWeight = ItemWeightRules.CalculateStackWeight(definition.UnitWeight, quantity);
            return destinationCarried
                ? checked(weight + stackWeight)
                : checked(weight - stackWeight);
        }

        private static InventoryItem WithQuantity(InventoryItem item, int quantity)
        {
            return new InventoryItem(
                item.ItemInstanceId,
                item.DefinitionId,
                quantity,
                item.Revision,
                item.Policies.ToArray());
        }
    }

    // Predicted operations that were undone because the server rejected them or they no longer
    // applied: why, and the items they touched, which the panel marks.
    public sealed class InventoryPredictionRollback
    {
        public InventoryPredictionRollback(InventoryClientError error, IReadOnlyCollection<Guid> itemInstanceIds)
        {
            Error = error;
            ItemInstanceIds = itemInstanceIds ?? Array.Empty<Guid>();
        }

        public InventoryClientError Error { get; }

        public IReadOnlyCollection<Guid> ItemInstanceIds { get; }

        public static IReadOnlyCollection<Guid> ItemsOf(IEnumerable<InventoryPredictedOperation> operations)
        {
            var ids = new HashSet<Guid>();
            foreach (var operation in operations)
            {
                foreach (var id in new[]
                         {
                             operation.ItemInstanceId,
                             operation.TargetItemInstanceId,
                             operation.PredictedItemInstanceId
                         })
                {
                    if (id != Guid.Empty)
                    {
                        ids.Add(id);
                    }
                }
            }

            return ids;
        }
    }
}
