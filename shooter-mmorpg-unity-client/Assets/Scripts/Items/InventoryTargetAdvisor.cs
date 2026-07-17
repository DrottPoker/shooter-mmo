using System;
using System.Linq;
using ShooterMmo.WorldData.Items;

namespace ShooterMmo.Items
{
    public static class InventoryTargetAdvisor
    {
        public static bool CanPlaceInContainer(
            InventoryClientState state,
            InventoryItem item,
            InventoryItemLocation source,
            InventoryContainer destination,
            InventorySlot destinationSlot,
            out string reason)
        {
            return CanPlaceInContainer(
                state,
                item,
                source,
                destination,
                destinationSlot,
                item == null ? 0 : item.Quantity,
                out reason);
        }

        public static bool CanPlaceInContainer(
            InventoryClientState state,
            InventoryItem item,
            InventoryItemLocation source,
            InventoryContainer destination,
            InventorySlot destinationSlot,
            int movedQuantity,
            out string reason)
        {
            reason = string.Empty;
            if (state?.Catalog == null
                || state.FullSnapshot == null
                || item == null
                || source == null
                || destination == null
                || destinationSlot == null
                || movedQuantity <= 0
                || movedQuantity > item.Quantity)
            {
                reason = "Inventory state is unavailable.";
                return false;
            }

            if (destinationSlot.Item != null)
            {
                reason = "The destination slot is occupied.";
                return false;
            }

            if (!state.Catalog.TryGetDefinition(item.DefinitionId, out var definition))
            {
                reason = "The item definition is unavailable.";
                return false;
            }

            var slotDefinition = new BagSlotDefinition
            {
                Index = destinationSlot.SlotIndex,
                Kind = destinationSlot.SlotKind,
                AcceptedTags = destinationSlot.AcceptedTags.ToArray()
            };
            if (!ItemSlotRules.Accepts(definition, slotDefinition))
            {
                reason = "The item does not match this slot's accepted tags.";
                return false;
            }

            if (string.Equals(
                    destination.ContainerType,
                    "secure_container",
                    StringComparison.Ordinal)
                && !SecureContainerRules.IsEligible(definition))
            {
                reason = "This definition is not eligible for the Secure Container.";
                return false;
            }

            if (definition.Bag != null
                && !CanPlaceBag(state, item, destination, destinationSlot, out reason))
            {
                return false;
            }

            if (source.IsExternal
                && IsCarriedContainer(destination.ContainerType)
                && !FitsHardCap(state.FullSnapshot, definition, movedQuantity))
            {
                reason = "The move would exceed the 140 percent carry cap.";
                return false;
            }

            return true;
        }

        public static bool CanEquip(
            InventoryClientState state,
            InventoryItem item,
            InventoryItemLocation source,
            InventoryEquipmentSlot destination,
            out string reason)
        {
            reason = string.Empty;
            if (state?.Catalog == null
                || state.FullSnapshot == null
                || item == null
                || source == null
                || destination == null)
            {
                reason = "Inventory state is unavailable.";
                return false;
            }

            if (destination.Item != null)
            {
                reason = "The equipment slot is occupied.";
                return false;
            }

            if (!state.Catalog.TryGetDefinition(item.DefinitionId, out var definition)
                || !ItemEquipmentRules.IsCompatible(definition, destination.SlotId))
            {
                reason = "The item is not compatible with this equipment slot.";
                return false;
            }

            return true;
        }

        public static bool CanUnequip(
            InventoryClientState state,
            InventoryItem item,
            InventoryContainer destination,
            InventorySlot destinationSlot,
            out string reason)
        {
            reason = string.Empty;
            if (state?.Catalog == null || state.FullSnapshot == null || item == null)
            {
                reason = "Inventory state is unavailable.";
                return false;
            }

            if (!CanPlaceInContainer(
                state,
                item,
                new InventoryItemLocation(
                    InventoryItemLocationKind.Equipment,
                    Guid.Empty,
                    "equipment",
                    -1,
                    string.Empty,
                    Guid.Empty),
                destination,
                destinationSlot,
                out reason))
            {
                return false;
            }

            if (!state.Catalog.TryGetDefinition(item.DefinitionId, out var definition))
            {
                reason = "The item definition is unavailable.";
                return false;
            }

            if (definition.Bag != null)
            {
                var hasContents = IsEquippedBagWithContents(state, item.ItemInstanceId);
                if (hasContents)
                {
                    reason = "A non-empty equipped Bag cannot move to ordinary storage.";
                    return false;
                }
            }

            if (!FitsAfterUnequip(
                state.FullSnapshot,
                definition,
                item.Quantity,
                destination.ContainerType))
            {
                reason = "Unequipping this item would exceed the 140 percent carry cap.";
                return false;
            }

            return true;
        }

        public static bool CanSwap(
            InventoryClientState state,
            InventoryItem firstItem,
            InventoryItemLocation firstLocation,
            InventoryItem secondItem,
            InventoryItemLocation secondLocation,
            out string reason)
        {
            reason = string.Empty;
            if (state?.Catalog == null
                || state.FullSnapshot == null
                || firstItem == null
                || secondItem == null
                || firstLocation == null
                || secondLocation == null
                || firstItem.ItemInstanceId == secondItem.ItemInstanceId
                || firstLocation.Kind != InventoryItemLocationKind.Container
                || secondLocation.Kind != InventoryItemLocationKind.Container
                || !TryResolveContainerSlot(
                    state,
                    firstLocation,
                    out var firstContainer,
                    out var firstSlot)
                || !TryResolveContainerSlot(
                    state,
                    secondLocation,
                    out var secondContainer,
                    out var secondSlot))
            {
                reason = "Two current container items are required for a slot swap.";
                return false;
            }

            if (!CanOccupySlot(
                    state,
                    firstItem,
                    secondContainer,
                    secondSlot,
                    out reason)
                || !CanOccupySlot(
                    state,
                    secondItem,
                    firstContainer,
                    firstSlot,
                    out reason))
            {
                return false;
            }

            try
            {
                var finalWeight = state.FullSnapshot.CarriedWeight;
                finalWeight = ApplyContainerWeightDelta(
                    state,
                    finalWeight,
                    firstItem,
                    firstContainer.ContainerType,
                    secondContainer.ContainerType);
                finalWeight = ApplyContainerWeightDelta(
                    state,
                    finalWeight,
                    secondItem,
                    secondContainer.ContainerType,
                    firstContainer.ContainerType);
                if (!EncumbranceRules.IsWithinHardCap(
                    finalWeight,
                    state.FullSnapshot.CarryCapacity))
                {
                    reason = "The swap would exceed the 140 percent carry cap.";
                    return false;
                }
            }
            catch (OverflowException)
            {
                reason = "The swap weight is invalid.";
                return false;
            }

            return true;
        }

        public static bool CanMerge(
            ClientItemCatalog catalog,
            InventoryItem source,
            InventoryItem target,
            out string reason)
        {
            reason = string.Empty;
            if (catalog == null
                || source == null
                || target == null
                || source.ItemInstanceId == target.ItemInstanceId
                || !catalog.TryGetDefinition(source.DefinitionId, out var definition))
            {
                reason = "The selected stacks cannot be merged.";
                return false;
            }

            var sourceState = ToStackState(source, definition);
            var targetState = ToStackState(target, definition);
            if (!ItemStackRules.CanMerge(definition, sourceState, targetState))
            {
                reason = "The selected stacks are incompatible or would exceed the stack limit.";
                return false;
            }

            return true;
        }

        public static bool CanMerge(
            InventoryClientState state,
            InventoryItem source,
            InventoryItemLocation sourceLocation,
            InventoryItem target,
            InventoryItemLocation targetLocation,
            out string reason)
        {
            reason = string.Empty;
            if (state?.FullSnapshot == null
                || sourceLocation == null
                || targetLocation == null
                || !CanMerge(state.Catalog, source, target, out reason))
            {
                return false;
            }

            if (sourceLocation.IsExternal
                && !targetLocation.IsExternal
                && state.Catalog.TryGetDefinition(source.DefinitionId, out var definition)
                && !FitsHardCap(state.FullSnapshot, definition, source.Quantity))
            {
                reason = "Merging this stack would exceed the 140 percent carry cap.";
                return false;
            }

            return true;
        }

        public static bool CanClaimRecovery(
            InventoryClientState state,
            RecoveryDelivery delivery,
            InventoryContainer destination,
            out string reason)
        {
            reason = string.Empty;
            if (state?.Catalog == null
                || state.FullSnapshot == null
                || delivery == null
                || delivery.Items.Count == 0
                || destination == null)
            {
                reason = "Recovery state is unavailable.";
                return false;
            }

            if (string.Equals(destination.ContainerType, "bank", StringComparison.Ordinal))
            {
                return true;
            }

            if (!IsCarriedContainer(destination.ContainerType))
            {
                reason = "This container is not a supported Recovery destination.";
                return false;
            }

            try
            {
                var finalWeight = state.FullSnapshot.CarriedWeight;
                foreach (var deliveryItem in delivery.Items)
                {
                    if (!state.Catalog.TryGetDefinition(
                        deliveryItem.Item.DefinitionId,
                        out var definition))
                    {
                        reason = "A Recovery item definition is unavailable.";
                        return false;
                    }

                    finalWeight = checked(
                        finalWeight + ItemWeightRules.CalculateStackWeight(
                            definition.UnitWeight,
                            deliveryItem.Item.Quantity));
                }

                if (!EncumbranceRules.IsWithinHardCap(
                    finalWeight,
                    state.FullSnapshot.CarryCapacity))
                {
                    reason = "Claiming this delivery would exceed the 140 percent carry cap.";
                    return false;
                }

                return true;
            }
            catch (OverflowException)
            {
                reason = "The Recovery delivery weight is invalid.";
                return false;
            }
        }

        public static bool CanSplit(
            ClientItemCatalog catalog,
            InventoryItem item,
            int quantity,
            out string reason)
        {
            reason = string.Empty;
            if (catalog == null
                || item == null
                || !catalog.TryGetDefinition(item.DefinitionId, out var definition)
                || quantity <= 0
                || quantity >= item.Quantity
                || quantity > definition.MaximumStackSize)
            {
                reason = "The split quantity must leave a positive source stack.";
                return false;
            }

            return true;
        }

        public static bool CanDestroy(
            ClientItemCatalog catalog,
            InventoryItem item,
            out string reason)
        {
            reason = string.Empty;
            if (catalog == null
                || item == null
                || !catalog.TryGetDefinition(item.DefinitionId, out var definition)
                || !definition.PlayerDestroyable)
            {
                reason = "This item cannot be destroyed by the player.";
                return false;
            }

            return true;
        }

        private static bool CanPlaceBag(
            InventoryClientState state,
            InventoryItem item,
            InventoryContainer destination,
            InventorySlot destinationSlot,
            out string reason)
        {
            var hasContents = IsEquippedBagWithContents(state, item.ItemInstanceId);
            var destinationKind = ToBagDestination(destination, destinationSlot);
            var wouldCycle = state.FullSnapshot.EquippedBag != null
                && state.FullSnapshot.EquippedBag.Item.ItemInstanceId == item.ItemInstanceId
                && state.FullSnapshot.EquippedBag.Contents.ContainerId == destination.ContainerId;
            if (!BagLocationRules.CanPlace(hasContents, destinationKind, wouldCycle))
            {
                reason = hasContents
                    ? "A non-empty Bag can move only through an atomic Bag-slot transfer."
                    : "The Bag cannot enter this slot.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static BagDestinationKind ToBagDestination(
            InventoryContainer destination,
            InventorySlot slot)
        {
            if (string.Equals(destination.ContainerType, "permanent_inventory", StringComparison.Ordinal))
            {
                return BagDestinationKind.PermanentInventoryGeneralSlot;
            }

            if (string.Equals(destination.ContainerType, "bag_contents", StringComparison.Ordinal))
            {
                return string.Equals(slot.SlotKind, BagSlotKindIds.General, StringComparison.Ordinal)
                    ? BagDestinationKind.BagGeneralSlot
                    : BagDestinationKind.BagSpecializedSlot;
            }

            if (string.Equals(destination.ContainerType, "bank", StringComparison.Ordinal))
            {
                return BagDestinationKind.BankGeneralSlot;
            }

            if (string.Equals(destination.ContainerType, "recovery_storage", StringComparison.Ordinal))
            {
                return BagDestinationKind.RecoveryStorageSlot;
            }

            return BagDestinationKind.SecureContainerSlot;
        }

        private static bool IsEquippedBagWithContents(
            InventoryClientState state,
            Guid itemInstanceId)
        {
            return state.FullSnapshot.EquippedBag != null
                && state.FullSnapshot.EquippedBag.Item.ItemInstanceId == itemInstanceId
                && state.FullSnapshot.EquippedBag.Contents.Slots.Any(slot => slot.Item != null);
        }

        private static bool FitsHardCap(
            CharacterInventorySnapshot snapshot,
            ItemDefinition definition,
            int quantity)
        {
            try
            {
                var addedWeight = ItemWeightRules.CalculateStackWeight(
                    definition.UnitWeight,
                    quantity);
                return EncumbranceRules.IsWithinHardCap(
                    checked(snapshot.CarriedWeight + addedWeight),
                    snapshot.CarryCapacity);
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static bool FitsAfterUnequip(
            CharacterInventorySnapshot snapshot,
            ItemDefinition definition,
            int quantity,
            string destinationContainerType)
        {
            try
            {
                var capacityBonus = definition.Bag == null
                    ? 0
                    : definition.Bag.CarryCapacityBonus;
                var finalCapacity = checked(snapshot.CarryCapacity - capacityBonus);
                var finalWeight = snapshot.CarriedWeight;
                if (IsCarriedContainer(destinationContainerType))
                {
                    finalWeight = checked(
                        finalWeight + ItemWeightRules.CalculateStackWeight(
                            definition.UnitWeight,
                            quantity));
                }

                return finalCapacity > 0
                    && finalWeight >= 0
                    && EncumbranceRules.IsWithinHardCap(finalWeight, finalCapacity);
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static bool CanOccupySlot(
            InventoryClientState state,
            InventoryItem item,
            InventoryContainer destination,
            InventorySlot destinationSlot,
            out string reason)
        {
            if (!state.Catalog.TryGetDefinition(item.DefinitionId, out var definition))
            {
                reason = "The item definition is unavailable.";
                return false;
            }

            var slotDefinition = new BagSlotDefinition
            {
                Index = destinationSlot.SlotIndex,
                Kind = destinationSlot.SlotKind,
                AcceptedTags = destinationSlot.AcceptedTags.ToArray()
            };
            if (!ItemSlotRules.Accepts(definition, slotDefinition))
            {
                reason = "An item does not match the opposite slot's accepted tags.";
                return false;
            }

            if (string.Equals(
                    destination.ContainerType,
                    "secure_container",
                    StringComparison.Ordinal)
                && !SecureContainerRules.IsEligible(definition))
            {
                reason = "An item is not eligible for the opposite Secure Container slot.";
                return false;
            }

            if (definition.Bag != null
                && !CanPlaceBag(state, item, destination, destinationSlot, out reason))
            {
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static long ApplyContainerWeightDelta(
            InventoryClientState state,
            long currentWeight,
            InventoryItem item,
            string sourceContainerType,
            string destinationContainerType)
        {
            var sourceCarried = IsCarriedContainer(sourceContainerType);
            var destinationCarried = IsCarriedContainer(destinationContainerType);
            if (sourceCarried == destinationCarried)
            {
                return currentWeight;
            }

            if (!state.Catalog.TryGetDefinition(item.DefinitionId, out var definition))
            {
                throw new OverflowException("The item definition is unavailable.");
            }

            var weight = ItemWeightRules.CalculateStackWeight(
                definition.UnitWeight,
                item.Quantity);
            return destinationCarried
                ? checked(currentWeight + weight)
                : checked(currentWeight - weight);
        }

        private static bool TryResolveContainerSlot(
            InventoryClientState state,
            InventoryItemLocation location,
            out InventoryContainer container,
            out InventorySlot slot)
        {
            container = null;
            slot = null;
            var containers = new[]
            {
                state.FullSnapshot.PermanentInventory,
                state.FullSnapshot.EquippedBag?.Contents,
                state.FullSnapshot.SecureContainer.Contents,
                state.Bank
            };
            container = containers.FirstOrDefault(candidate =>
                candidate != null && candidate.ContainerId == location.ContainerId);
            return container != null
                && container.TryGetSlot(location.SlotIndex, out slot);
        }

        private static bool IsCarriedContainer(string containerType)
        {
            return string.Equals(containerType, "permanent_inventory", StringComparison.Ordinal)
                || string.Equals(containerType, "bag_contents", StringComparison.Ordinal)
                || string.Equals(containerType, "secure_container", StringComparison.Ordinal);
        }

        private static ItemStackState ToStackState(
            InventoryItem item,
            ItemDefinition definition)
        {
            var policyFingerprint = item.Policies.Count == 0
                ? "none"
                : string.Join(
                    "|",
                    item.Policies
                        .OrderBy(policy => policy.Kind, StringComparer.Ordinal)
                        .ThenBy(policy => policy.Status, StringComparer.Ordinal)
                        .Select(policy => policy.Kind + ":" + policy.Status));
            return new ItemStackState(
                item.DefinitionId,
                item.Quantity,
                policyFingerprint,
                definition.StructuralFingerprint);
        }
    }
}
