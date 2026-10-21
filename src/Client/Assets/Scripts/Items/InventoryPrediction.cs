using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
    public enum InventoryOperationSendResult
    {
        Sent,
        // The owning channel is busy, such as a corpse refresh in flight; try again later.
        Wait,
        Failed
    }

    // Applies an operation that another controller owns, such as a corpse loot or a vendor
    // trade, to one step of the shown inventory.
    internal delegate bool InventoryOperationPrediction(InventoryPredictionEdit edit, out string reason);

    // Sends such an operation with the revisions the confirmed state expects.
    public delegate InventoryOperationSendResult InventoryOperationSend(
        InventoryClientState state,
        out Guid operationId,
        out string error);

    // One item operation the player made that the client shows before SimulationWorker answers.
    // It names items and destinations only; the revisions the intent expects come from the
    // confirmed state when the operation is sent. Item operations carry their kind; corpse
    // and vendor operations carry the prediction and the send of the controller that owns them.
    public sealed class InventoryPredictedOperation
    {
        private readonly InventoryOperationPrediction externalPrediction;
        private readonly InventoryOperationSend externalSend;
        private readonly Guid[] externalItemIds;
        private readonly Guid[] predictedItemIds;

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
            predictedItemIds = PredictedItemInstanceId == Guid.Empty
                ? Array.Empty<Guid>()
                : new[] { PredictedItemInstanceId };
            externalItemIds = Array.Empty<Guid>();
        }

        private InventoryPredictedOperation(
            InventoryOperationPrediction prediction,
            InventoryOperationSend send,
            IEnumerable<Guid> itemInstanceIds,
            IEnumerable<Guid> createdItemInstanceIds)
            : this(default, Guid.Empty, Guid.Empty, Guid.Empty, -1, 0, string.Empty, null)
        {
            externalPrediction = prediction ?? throw new ArgumentNullException(nameof(prediction));
            externalSend = send ?? throw new ArgumentNullException(nameof(send));
            externalItemIds = itemInstanceIds?.Where(id => id != Guid.Empty).ToArray() ?? Array.Empty<Guid>();
            predictedItemIds = createdItemInstanceIds?.Where(id => id != Guid.Empty).ToArray() ?? Array.Empty<Guid>();
        }

        // The item operation kind; unused by a corpse or vendor operation.
        public RealtimeItemOperationKind Kind { get; }

        public bool IsExternal
        {
            get { return externalPrediction != null; }
        }

        public Guid ItemInstanceId { get; }

        public Guid TargetItemInstanceId { get; }

        public Guid DestinationContainerId { get; }

        public int DestinationSlotIndex { get; }

        public int Quantity { get; }

        public string EquipmentSlotId { get; }

        public RecoveryDelivery RecoveryDelivery { get; }

        public Guid PredictedItemInstanceId { get; }

        // The ids the prediction invents for items the server creates, which nothing may act
        // on until the server's ids replace them.
        public IReadOnlyCollection<Guid> PredictedItemInstanceIds
        {
            get { return predictedItemIds; }
        }

        // The contents container a predicted Bag equip shows until the server names its own.
        public Guid PredictedContainerId { get; } = Guid.NewGuid();

        // False for an operation the client no longer shows ahead, such as a sent operation
        // that a newer confirmed state overtook. It waits for its result.
        public bool IsPredicted { get; internal set; } = true;

        public Guid OperationId { get; internal set; }

        public bool IsSent
        {
            get { return OperationId != Guid.Empty; }
        }

        // The item-state revision the operation committed, or -1 before its result.
        public long CommittedRevision { get; internal set; } = -1;

        public InventoryRefreshScope RejectionRefreshScope { get; internal set; } = InventoryRefreshScope.Full;

        // Every item the operation touches, which an undo marks.
        public IEnumerable<Guid> TouchedItemInstanceIds
        {
            get
            {
                return new[] { ItemInstanceId, TargetItemInstanceId }
                    .Concat(externalItemIds)
                    .Concat(predictedItemIds)
                    .Concat(RecoveryDelivery?.Items.Select(item => item.Item.ItemInstanceId) ?? Enumerable.Empty<Guid>())
                    .Where(id => id != Guid.Empty)
                    .Distinct();
            }
        }

        internal bool TryPredictExternal(InventoryPredictionEdit edit, out string reason)
        {
            return externalPrediction(edit, out reason);
        }

        internal InventoryOperationSendResult SendExternal(
            InventoryClientState state,
            out Guid operationId,
            out string error)
        {
            return externalSend(state, out operationId, out error);
        }

        internal static InventoryPredictedOperation External(
            InventoryOperationPrediction prediction,
            InventoryOperationSend send,
            IEnumerable<Guid> itemInstanceIds,
            IEnumerable<Guid> createdItemInstanceIds = null)
        {
            return new InventoryPredictedOperation(prediction, send, itemInstanceIds, createdItemInstanceIds);
        }

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
                delivery ?? throw new ArgumentNullException(nameof(delivery)));
        }
    }

    // One step of a prediction: a working copy of the shown inventory with the carried weight,
    // carry capacity, and balance that the step changes.
    internal sealed class InventoryPredictionEdit
    {
        private readonly Func<Guid, bool> isPredictedContainer;

        public InventoryPredictionEdit(IInventoryView view, Func<Guid, bool> isPredictedContainer = null)
        {
            View = view;
            this.isPredictedContainer = isPredictedContainer;
            Editor = new InventorySnapshotEditor(view.FullSnapshot, view.Bank, view.FullSnapshot.RecoveryStorage);
            Weight = view.FullSnapshot.CarriedWeight;
            Capacity = view.FullSnapshot.CarryCapacity;
            Currency = view.FullSnapshot.Currency;
        }

        public IInventoryView View { get; }

        public ClientItemCatalog Catalog
        {
            get { return View.Catalog; }
        }

        public InventorySnapshotEditor Editor { get; }

        public long Weight { get; set; }

        public long Capacity { get; set; }

        public long Currency { get; set; }

        // Carried weight counts carried containers only; equipment and storage weigh nothing.
        public void MoveWeight(ItemDefinition definition, int quantity, string sourceContainerType, string destinationContainerType)
        {
            var sourceCarried = sourceContainerType != null && ItemContainerTypeIds.IsCarried(sourceContainerType);
            var destinationCarried = destinationContainerType != null && ItemContainerTypeIds.IsCarried(destinationContainerType);
            if (sourceCarried == destinationCarried)
            {
                return;
            }

            var stackWeight = ItemWeightRules.CalculateStackWeight(definition.UnitWeight, quantity);
            Weight = destinationCarried ? checked(Weight + stackWeight) : checked(Weight - stackWeight);
        }

        public void MoveWeightFrom(ItemDefinition definition, int quantity, InventoryItemLocation source, string destinationContainerType)
        {
            MoveWeight(
                definition,
                quantity,
                source.Kind == InventoryItemLocationKind.Container ? source.ContainerType : null,
                destinationContainerType);
        }

        // The voluntary transition rule GameService checks when the transaction commits.
        public bool FitsHardCap()
        {
            return Capacity > 0
                && Weight >= 0
                && EncumbranceRules.IsAllowedVoluntaryTransition(
                    View.FullSnapshot.CarriedWeight,
                    View.FullSnapshot.CarryCapacity,
                    Weight,
                    Capacity);
        }

        public bool TryGetDefinition(string definitionId, out ItemDefinition definition)
        {
            return Catalog.TryGetDefinition(definitionId, out definition);
        }

        // A Bag contents container that only a pending equip shows has no server id yet, so
        // nothing may target it until the equip commits.
        public bool IsPredictedContainer(Guid containerId)
        {
            return isPredictedContainer != null && isPredictedContainer(containerId);
        }

        public InventoryContainer FindContainer(Guid containerId)
        {
            if (IsPredictedContainer(containerId))
            {
                return null;
            }

            return new[]
                {
                    View.FullSnapshot.PermanentInventory,
                    View.FullSnapshot.EquippedBag?.Contents,
                    View.FullSnapshot.SecureContainer?.Contents,
                    View.Bank
                }
                .FirstOrDefault(candidate => candidate != null && candidate.ContainerId == containerId);
        }

        // The first empty slot, in slot order, that accepts the definition and that this step
        // has not filled yet, as GameService picks a slot when the request names none.
        public InventorySlot FindFreeSlot(
            InventoryContainer container,
            ItemDefinition definition,
            ISet<(Guid, int)> filled,
            Func<InventorySlot, bool> include = null)
        {
            return container?.Slots.FirstOrDefault(slot =>
                slot.Item == null
                && !filled.Contains((container.ContainerId, slot.SlotIndex))
                && (include == null || include(slot))
                && InventoryTargetAdvisor.SlotAccepts(definition, container, slot));
        }

        public CharacterInventorySnapshot Build()
        {
            return Editor.Build(View.FullSnapshot.ItemStateRevision, Math.Max(0, Weight), Capacity, Currency);
        }

        public static InventoryItem WithQuantity(InventoryItem item, int quantity)
        {
            return new InventoryItem(
                item.ItemInstanceId,
                item.DefinitionId,
                quantity,
                item.Revision,
                item.Policies.ToArray());
        }
    }

    // Applies one operation to an inventory the way GameService commits it, after the same
    // target rules the drop targets use. It is the client's prediction, never the authority:
    // the committed change or a reload replaces whatever it shows.
    public static class InventoryOperationPredictor
    {
        public static bool TryApply(
            IInventoryView view,
            InventoryPredictedOperation operation,
            out CharacterInventorySnapshot predicted,
            out string reason)
        {
            return TryApply(view, operation, null, out predicted, out reason);
        }

        internal static bool TryApply(
            IInventoryView view,
            InventoryPredictedOperation operation,
            Func<Guid, bool> isPredictedContainer,
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

            var edit = new InventoryPredictionEdit(view, isPredictedContainer);
            try
            {
                var applied = operation.IsExternal
                    ? operation.TryPredictExternal(edit, out reason)
                    : operation.Kind == RealtimeItemOperationKind.ClaimRecoveryDelivery
                        ? TryClaim(edit, operation, out reason)
                        : TryApplyItemOperation(edit, operation, out reason);
                if (!applied)
                {
                    return false;
                }

                // GameService checks the hard cap on every voluntary transaction, so no step
                // may show a load it would refuse, such as a swap to a smaller Bag.
                if (!edit.FitsHardCap())
                {
                    reason = "The move would exceed the 140 percent carry cap.";
                    return false;
                }

                predicted = edit.Build();
                reason = string.Empty;
                return true;
            }
            catch (OverflowException)
            {
                reason = "The item weight is invalid.";
                return false;
            }
        }

        private static bool TryApplyItemOperation(
            InventoryPredictionEdit edit,
            InventoryPredictedOperation operation,
            out string reason)
        {
            reason = "The item is no longer where it was.";
            var view = edit.View;
            var editor = edit.Editor;
            if (!editor.TryFind(operation.ItemInstanceId, out var item, out var source)
                || !edit.TryGetDefinition(item.DefinitionId, out var definition))
            {
                return false;
            }

            switch (operation.Kind)
            {
                case RealtimeItemOperationKind.Relocate:
                case RealtimeItemOperationKind.Unequip:
                {
                    if ((operation.Kind == RealtimeItemOperationKind.Unequip)
                            != (source.Kind == InventoryItemLocationKind.Equipment)
                        || !TryResolveDestination(edit, operation, item, source, item.Quantity, out var container, out var slot, out reason)
                        || (operation.Kind == RealtimeItemOperationKind.Unequip
                            ? !InventoryTargetAdvisor.CanUnequip(view, item, container, slot, out reason)
                            : !InventoryTargetAdvisor.CanPlaceInContainer(view, item, source, container, slot, out reason)))
                    {
                        return false;
                    }

                    editor.Remove(item.ItemInstanceId);
                    editor.TryPlace(container.ContainerId, slot.SlotIndex, item);
                    edit.MoveWeightFrom(definition, item.Quantity, source, container.ContainerType);
                    if (definition.Bag != null && source.Kind == InventoryItemLocationKind.Equipment)
                    {
                        edit.Capacity -= definition.Bag.CarryCapacityBonus;
                    }

                    return true;
                }
                case RealtimeItemOperationKind.Equip:
                {
                    var equipmentSlot = view.FullSnapshot.Equipment.FirstOrDefault(candidate =>
                        string.Equals(candidate.SlotId, operation.EquipmentSlotId, StringComparison.Ordinal));
                    if (equipmentSlot == null)
                    {
                        reason = "The equipment slot is unavailable.";
                        return false;
                    }

                    if (source.Kind != InventoryItemLocationKind.Container
                        || !InventoryTargetAdvisor.CanEquip(view, item, source, equipmentSlot, out reason))
                    {
                        return false;
                    }

                    var occupant = equipmentSlot.Item;
                    editor.Remove(item.ItemInstanceId);
                    edit.MoveWeightFrom(definition, item.Quantity, source, null);
                    if (occupant != null)
                    {
                        // The equipped item swaps into the slot the new item came from. An
                        // equipped Bag must be empty, so its contents leave view with it.
                        if (!edit.TryGetDefinition(occupant.DefinitionId, out var occupantDefinition))
                        {
                            return false;
                        }

                        editor.Remove(occupant.ItemInstanceId);
                        editor.TryPlace(source.ContainerId, source.SlotIndex, occupant);
                        edit.MoveWeight(occupantDefinition, occupant.Quantity, null, source.ContainerType);
                        if (occupantDefinition.Bag != null)
                        {
                            edit.Capacity -= occupantDefinition.Bag.CarryCapacityBonus;
                        }
                    }

                    if (definition.Bag != null
                        && string.Equals(equipmentSlot.SlotId, ItemEquipmentSlotIds.Bag, StringComparison.Ordinal))
                    {
                        // A Bag outside the Bag slot is always empty, so it equips with empty contents.
                        edit.Capacity += definition.Bag.CarryCapacityBonus;
                        return editor.TryEquipBag(
                            item,
                            InventorySnapshotEditor.CreateBagContents(definition, operation.PredictedContainerId, 0));
                    }

                    return editor.TryEquip(equipmentSlot.SlotId, item);
                }
                case RealtimeItemOperationKind.SwapContainerItems:
                {
                    if (!editor.TryFind(operation.TargetItemInstanceId, out var second, out var secondLocation)
                        || !InventoryTargetAdvisor.CanSwap(view, item, source, second, secondLocation, out reason)
                        || !edit.TryGetDefinition(second.DefinitionId, out var secondDefinition))
                    {
                        return false;
                    }

                    editor.Remove(item.ItemInstanceId);
                    editor.Remove(second.ItemInstanceId);
                    editor.TryPlace(secondLocation.ContainerId, secondLocation.SlotIndex, item);
                    editor.TryPlace(source.ContainerId, source.SlotIndex, second);
                    edit.MoveWeightFrom(definition, item.Quantity, source, secondLocation.ContainerType);
                    edit.MoveWeightFrom(secondDefinition, second.Quantity, secondLocation, source.ContainerType);
                    return true;
                }
                case RealtimeItemOperationKind.MergeStacks:
                {
                    if (!editor.TryFind(operation.TargetItemInstanceId, out var target, out var targetLocation)
                        || !InventoryTargetAdvisor.CanMerge(view, item, source, target, targetLocation, out reason))
                    {
                        return false;
                    }

                    editor.Remove(item.ItemInstanceId);
                    editor.TryReplace(
                        target.ItemInstanceId,
                        InventoryPredictionEdit.WithQuantity(target, target.Quantity + item.Quantity));
                    edit.MoveWeightFrom(
                        definition,
                        item.Quantity,
                        source,
                        targetLocation.Kind == InventoryItemLocationKind.Equipment ? null : targetLocation.ContainerType);
                    return true;
                }
                case RealtimeItemOperationKind.SplitStack:
                {
                    if (!InventoryTargetAdvisor.CanSplit(view.Catalog, item, operation.Quantity, out reason)
                        || !TryResolveDestination(edit, operation, item, source, operation.Quantity, out var container, out var slot, out reason)
                        || !InventoryTargetAdvisor.CanPlaceInContainer(view, item, source, container, slot, operation.Quantity, out reason))
                    {
                        return false;
                    }

                    editor.TryReplace(
                        item.ItemInstanceId,
                        InventoryPredictionEdit.WithQuantity(item, item.Quantity - operation.Quantity));
                    editor.TryPlace(
                        container.ContainerId,
                        slot.SlotIndex,
                        new InventoryItem(
                            operation.PredictedItemInstanceId,
                            item.DefinitionId,
                            operation.Quantity,
                            0,
                            item.Policies.ToArray()));
                    edit.MoveWeightFrom(definition, operation.Quantity, source, container.ContainerType);
                    return true;
                }
                case RealtimeItemOperationKind.Destroy:
                {
                    if (!InventoryTargetAdvisor.CanDestroy(view.Catalog, item, out reason))
                    {
                        return false;
                    }

                    editor.Remove(item.ItemInstanceId);
                    edit.MoveWeightFrom(definition, item.Quantity, source, null);
                    if (definition.Bag != null && source.Kind == InventoryItemLocationKind.Equipment)
                    {
                        edit.Capacity -= definition.Bag.CarryCapacityBonus;
                    }

                    return true;
                }
                default:
                    reason = "This operation cannot be shown before the server answers.";
                    return false;
            }
        }

        // A claim moves the whole delivery, in item order, into the first free slots of the
        // destination that accept each item, never merging, and the delivery leaves Recovery.
        private static bool TryClaim(
            InventoryPredictionEdit edit,
            InventoryPredictedOperation operation,
            out string reason)
        {
            var recovery = edit.View.FullSnapshot.RecoveryStorage;
            var delivery = recovery?.Deliveries.FirstOrDefault(candidate =>
                candidate.DeliveryId == operation.RecoveryDelivery.DeliveryId);
            var destination = edit.FindContainer(operation.DestinationContainerId);
            if (delivery == null || destination == null)
            {
                reason = "The Recovery delivery or its destination is no longer available.";
                return false;
            }

            if (!InventoryTargetAdvisor.CanClaimRecovery(edit.View, delivery, destination, out reason))
            {
                return false;
            }

            edit.Editor.RemoveDelivery(delivery.DeliveryId);
            var filled = new HashSet<(Guid, int)>();
            foreach (var deliveryItem in delivery.Items.OrderBy(entry => entry.ItemOrder))
            {
                if (!edit.TryGetDefinition(deliveryItem.Item.DefinitionId, out var definition))
                {
                    reason = "A Recovery item definition is unavailable.";
                    return false;
                }

                var slot = edit.FindFreeSlot(destination, definition, filled);
                if (slot == null)
                {
                    reason = "The destination has no free slot for every delivered item.";
                    return false;
                }

                filled.Add((destination.ContainerId, slot.SlotIndex));
                edit.Editor.TryPlace(destination.ContainerId, slot.SlotIndex, deliveryItem.Item);
                edit.MoveWeight(definition, deliveryItem.Item.Quantity, ItemContainerTypeIds.RecoveryStorage, destination.ContainerType);
            }

            reason = string.Empty;
            return true;
        }

        // An explicit slot, or the first free slot that accepts the item, as GameService picks.
        private static bool TryResolveDestination(
            InventoryPredictionEdit edit,
            InventoryPredictedOperation operation,
            InventoryItem item,
            InventoryItemLocation source,
            int quantity,
            out InventoryContainer container,
            out InventorySlot slot,
            out string reason)
        {
            reason = edit.IsPredictedContainer(operation.DestinationContainerId)
                ? "Wait a moment until your equipped Bag is confirmed."
                : "The destination container is unavailable.";
            slot = null;
            container = edit.FindContainer(operation.DestinationContainerId);
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
                    && InventoryTargetAdvisor.CanPlaceInContainer(edit.View, item, source, container, candidate, quantity, out _))
                {
                    slot = candidate;
                    return true;
                }
            }

            reason = "The destination container has no free slot for the item.";
            return false;
        }
    }

    // Predicted operations that were undone because the server rejected them or they no longer
    // applied: why, the operations, and the items they touched, which the panel marks.
    public sealed class InventoryPredictionRollback
    {
        public InventoryPredictionRollback(
            InventoryClientError error,
            IReadOnlyList<InventoryPredictedOperation> operations)
        {
            Error = error;
            Operations = operations ?? Array.Empty<InventoryPredictedOperation>();
            ItemInstanceIds = Operations.SelectMany(operation => operation.TouchedItemInstanceIds).Distinct().ToArray();
        }

        public InventoryClientError Error { get; }

        public IReadOnlyList<InventoryPredictedOperation> Operations { get; }

        public IReadOnlyCollection<Guid> ItemInstanceIds { get; }
    }
}
