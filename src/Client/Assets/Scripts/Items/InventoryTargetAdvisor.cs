using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
    public static class InventoryTargetAdvisor
    {
        public static bool CanPlaceInContainer(
            IInventoryView state,
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
            IInventoryView state,
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

            if (definition.Bag != null
                && !CanPlaceBag(state, item, destination, destinationSlot, out reason))
            {
                return false;
            }

            if (IsWeightless(source)
                && ItemContainerTypeIds.IsCarried(destination.ContainerType)
                && !FitsHardCap(state.FullSnapshot, definition, movedQuantity))
            {
                reason = "The move would exceed the 140 percent carry cap.";
                return false;
            }

            return true;
        }

        // Equips the item into the slot. On an occupied slot the equipped item swaps into the slot
        // the new item comes from, so that slot must accept it and the carry cap must hold.
        public static bool CanEquip(
            IInventoryView state,
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

            if (destination.Item != null
                && !CanSwapIntoEquipment(state, item, source, destination, out reason))
            {
                return false;
            }

            return CanEquipIntoSlot(state, item, destination, out reason);
        }

        // The equip rules of the slot itself: compatibility, the quantity limit, and blocked slots.
        // An item in the slot is the one an equip replaces, so it no longer blocks anything.
        public static bool CanEquipIntoSlot(
            IInventoryView state,
            InventoryItem item,
            InventoryEquipmentSlot destination,
            out string reason)
        {
            reason = string.Empty;
            if (state?.Catalog == null || state.FullSnapshot == null || item == null || destination == null)
            {
                reason = "Inventory state is unavailable.";
                return false;
            }

            if (!state.Catalog.TryGetDefinition(item.DefinitionId, out var definition)
                || !ItemEquipmentRules.IsCompatible(definition, destination.SlotId)
                || !state.Catalog.TryGetEquipmentSlot(destination.SlotId, out var slot))
            {
                reason = "The item is not compatible with this equipment slot.";
                return false;
            }

            if (!ItemEquipmentRules.CanHoldQuantity(definition, slot, item.Quantity))
            {
                reason = "This equipment slot holds at most "
                    + ItemEquipmentRules.MaximumQuantity(definition, slot)
                    + ". Split the stack first.";
                return false;
            }

            var blockedSlots = BlockedSlotsByOccupiedSlot(state);
            blockedSlots.Remove(destination.SlotId);
            var blockingSlotId = ItemEquipmentRules.FindBlockingSlot(
                definition,
                destination.SlotId,
                blockedSlots);
            if (blockingSlotId != null)
            {
                reason = "The item equipped in "
                    + EquipmentSlotName(state.Catalog, blockingSlotId)
                    + " blocks this equipment slot.";
                return false;
            }

            return true;
        }

        // A stack of the equipped stackable definition tops up the equipped stack, such as arrows in
        // the ammunition slot, instead of swapping with it.
        public static bool IsEquippedStackTopUp(
            ClientItemCatalog catalog,
            InventoryItem item,
            InventoryItem equipped)
        {
            return catalog != null
                && item != null
                && equipped != null
                && item.ItemInstanceId != equipped.ItemInstanceId
                && string.Equals(item.DefinitionId, equipped.DefinitionId, StringComparison.Ordinal)
                && catalog.TryGetDefinition(item.DefinitionId, out var definition)
                && definition.MaximumStackSize > 1;
        }

        private static bool CanSwapIntoEquipment(
            IInventoryView state,
            InventoryItem item,
            InventoryItemLocation source,
            InventoryEquipmentSlot destination,
            out string reason)
        {
            var occupant = destination.Item;
            if (occupant.ItemInstanceId == item.ItemInstanceId)
            {
                reason = "The item is already equipped in this slot.";
                return false;
            }

            if (source.Kind != InventoryItemLocationKind.Container
                || !TryResolveContainerSlot(state, source, out var sourceContainer, out var sourceSlot))
            {
                reason = "Only an item in a container can swap with an equipped item.";
                return false;
            }

            if (!state.Catalog.TryGetDefinition(item.DefinitionId, out var definition)
                || !state.Catalog.TryGetDefinition(occupant.DefinitionId, out var occupantDefinition))
            {
                reason = "The item definition is unavailable.";
                return false;
            }

            if (occupantDefinition.Bag != null && IsEquippedBagWithContents(state, occupant.ItemInstanceId))
            {
                reason = "Empty the equipped Bag before you swap it.";
                return false;
            }

            if (!CanOccupySlot(state, occupant, sourceContainer, sourceSlot, out reason))
            {
                reason = "The equipped item cannot move into the slot the new item comes from: " + reason;
                return false;
            }

            try
            {
                var finalWeight = state.FullSnapshot.CarriedWeight;
                if (ItemContainerTypeIds.IsCarried(sourceContainer.ContainerType))
                {
                    finalWeight = checked(
                        finalWeight
                        - ItemWeightRules.CalculateStackWeight(definition.UnitWeight, item.Quantity)
                        + ItemWeightRules.CalculateStackWeight(occupantDefinition.UnitWeight, occupant.Quantity));
                }

                var finalCapacity = checked(
                    state.FullSnapshot.CarryCapacity
                    - (occupantDefinition.Bag?.CarryCapacityBonus ?? 0)
                    + (definition.Bag?.CarryCapacityBonus ?? 0));
                if (finalCapacity <= 0
                    || !EncumbranceRules.IsAllowedVoluntaryTransition(
                        state.FullSnapshot.CarriedWeight,
                        state.FullSnapshot.CarryCapacity,
                        finalWeight,
                        finalCapacity))
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

            reason = string.Empty;
            return true;
        }

        public static bool CanUnequip(
            IInventoryView state,
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
                InventoryItemLocation.ForEquipment(string.Empty),
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
            IInventoryView state,
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
                if (!EncumbranceRules.IsAllowedVoluntaryTransition(
                    state.FullSnapshot.CarriedWeight,
                    state.FullSnapshot.CarryCapacity,
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

        public static bool CanSwapWithExternalContainer(
            IInventoryView state,
            InventoryItem carriedItem,
            InventoryItemLocation carriedLocation,
            InventoryItem externalItem,
            InventoryContainer externalContainer,
            InventorySlot externalSlot,
            out string reason)
        {
            reason = string.Empty;
            if (state?.Catalog == null
                || state.FullSnapshot == null
                || carriedItem == null
                || carriedLocation == null
                || externalItem == null
                || externalContainer == null
                || externalSlot == null
                || carriedLocation.Kind != InventoryItemLocationKind.Container
                || !TryResolveContainerSlot(
                    state,
                    carriedLocation,
                    out var carriedContainer,
                    out var carriedSlot))
            {
                reason = "A current carried item and external container item are required for a swap.";
                return false;
            }

            if (!CanOccupySlot(
                    state,
                    carriedItem,
                    externalContainer,
                    externalSlot,
                    out reason)
                || !CanOccupySlot(
                    state,
                    externalItem,
                    carriedContainer,
                    carriedSlot,
                    out reason))
            {
                return false;
            }

            try
            {
                var finalWeight = ApplyContainerWeightDelta(
                    state,
                    state.FullSnapshot.CarriedWeight,
                    carriedItem,
                    carriedContainer.ContainerType,
                    externalContainer.ContainerType);
                finalWeight = ApplyContainerWeightDelta(
                    state,
                    finalWeight,
                    externalItem,
                    externalContainer.ContainerType,
                    carriedContainer.ContainerType);
                if (!EncumbranceRules.IsAllowedVoluntaryTransition(
                    state.FullSnapshot.CarriedWeight,
                    state.FullSnapshot.CarryCapacity,
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
            IInventoryView state,
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

            if (!state.Catalog.TryGetDefinition(source.DefinitionId, out var definition))
            {
                reason = "The item definition is unavailable.";
                return false;
            }

            // An equipped stack, such as the arrows in the ammunition slot, keeps its slot limit.
            if (targetLocation.Kind == InventoryItemLocationKind.Equipment
                && (!state.Catalog.TryGetEquipmentSlot(targetLocation.EquipmentSlotId, out var slot)
                    || !ItemEquipmentRules.CanHoldQuantity(
                        definition,
                        slot,
                        (long)source.Quantity + target.Quantity)))
            {
                reason = "The equipped stack would exceed its equipment slot limit.";
                return false;
            }

            if (IsWeightless(sourceLocation)
                && !IsWeightless(targetLocation)
                && !FitsHardCap(state.FullSnapshot, definition, source.Quantity))
            {
                reason = "Merging this stack would exceed the 140 percent carry cap.";
                return false;
            }

            return true;
        }

        public static bool CanClaimRecovery(
            IInventoryView state,
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

            if (string.Equals(destination.ContainerType, ItemContainerTypeIds.Bank, StringComparison.Ordinal))
            {
                return true;
            }

            // GameService delivers a claim into Permanent Inventory or the Bank only.
            if (!string.Equals(destination.ContainerType, ItemContainerTypeIds.PermanentInventory, StringComparison.Ordinal))
            {
                reason = "Claim a Recovery delivery into Permanent Inventory or the Bank.";
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

                if (!EncumbranceRules.IsAllowedVoluntaryTransition(
                    state.FullSnapshot.CarriedWeight,
                    state.FullSnapshot.CarryCapacity,
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
                || !ItemPolicyRules.Evaluate(
                        definition,
                        item.Policies.Select(policy => new ItemPolicyState(
                            policy.Kind,
                            policy.Status,
                            policy.ProtectionSource,
                            string.Empty)))
                    .CanPlayerDestroy)
            {
                reason = "This item cannot be destroyed by the player.";
                return false;
            }

            return true;
        }

        // Whether an empty slot accepts an item of the definition without contents, as the slot
        // rules of GameService decide when it picks a slot itself.
        public static bool SlotAccepts(ItemDefinition definition, InventoryContainer container, InventorySlot slot)
        {
            if (definition == null || container == null || slot == null)
            {
                return false;
            }

            if (definition.Bag != null)
            {
                return BagDestinationRules.TryResolve(container.ContainerType, slot.SlotKind, out var bagDestination)
                    && BagLocationRules.CanPlace(false, bagDestination, false);
            }

            var slotDefinition = new BagSlotDefinition
            {
                Index = slot.SlotIndex,
                Kind = slot.SlotKind,
                AcceptedTags = slot.AcceptedTags.ToArray()
            };
            return ItemSlotRules.Accepts(definition, slotDefinition);
        }

        private static bool CanPlaceBag(
            IInventoryView state,
            InventoryItem item,
            InventoryContainer destination,
            InventorySlot destinationSlot,
            out string reason)
        {
            var hasContents = IsEquippedBagWithContents(state, item.ItemInstanceId);
            if (!BagDestinationRules.TryResolve(
                    destination.ContainerType,
                    destinationSlot.SlotKind,
                    out var destinationKind))
            {
                reason = "The Bag cannot enter this container.";
                return false;
            }

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

        private static bool IsEquippedBagWithContents(
            IInventoryView state,
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
                return EncumbranceRules.IsAllowedVoluntaryTransition(
                    snapshot.CarriedWeight,
                    snapshot.CarryCapacity,
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
                if (ItemContainerTypeIds.IsCarried(destinationContainerType))
                {
                    finalWeight = checked(
                        finalWeight + ItemWeightRules.CalculateStackWeight(
                            definition.UnitWeight,
                            quantity));
                }

                return finalCapacity > 0
                    && finalWeight >= 0
                    && EncumbranceRules.IsAllowedVoluntaryTransition(
                        snapshot.CarriedWeight,
                        snapshot.CarryCapacity,
                        finalWeight,
                        finalCapacity);
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static bool CanOccupySlot(
            IInventoryView state,
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

            if (definition.Bag != null
                && !CanPlaceBag(state, item, destination, destinationSlot, out reason))
            {
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static long ApplyContainerWeightDelta(
            IInventoryView state,
            long currentWeight,
            InventoryItem item,
            string sourceContainerType,
            string destinationContainerType)
        {
            var sourceCarried = ItemContainerTypeIds.IsCarried(sourceContainerType);
            var destinationCarried = ItemContainerTypeIds.IsCarried(destinationContainerType);
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
            IInventoryView state,
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
                state.Bank
            };
            container = containers.FirstOrDefault(candidate =>
                candidate != null && candidate.ContainerId == location.ContainerId);
            return container != null
                && container.TryGetSlot(location.SlotIndex, out slot);
        }

        // Equipped items and external storage do not count toward carried weight.
        private static bool IsWeightless(InventoryItemLocation location)
        {
            return location.IsExternal || location.Kind == InventoryItemLocationKind.Equipment;
        }

        private static Dictionary<string, string[]> BlockedSlotsByOccupiedSlot(
            IInventoryView state)
        {
            var blockedSlots = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var equipmentSlot in state.FullSnapshot.Equipment)
            {
                if (equipmentSlot.Item != null)
                {
                    blockedSlots[equipmentSlot.SlotId] = state.Catalog.TryGetDefinition(
                        equipmentSlot.Item.DefinitionId,
                        out var equippedDefinition)
                        ? equippedDefinition.BlockedEquipmentSlots ?? Array.Empty<string>()
                        : Array.Empty<string>();
                }
            }

            return blockedSlots;
        }

        private static string EquipmentSlotName(ClientItemCatalog catalog, string equipmentSlotId)
        {
            return catalog.TryGetEquipmentSlot(equipmentSlotId, out var slot)
                && !string.IsNullOrWhiteSpace(slot.DisplayName)
                    ? slot.DisplayName
                    : equipmentSlotId;
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
