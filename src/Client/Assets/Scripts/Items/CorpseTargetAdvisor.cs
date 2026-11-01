using System;
using System.Linq;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
    // Client-side corpse target rules. They mirror the GameService corpse commands so the UI
    // offers only drops that can commit; GameService stays authoritative. A null partial
    // quantity moves the whole stack.
    public static class CorpseTargetAdvisor
    {
        // A corpse item dropped on a carried container slot loots it. An empty corpse Bag
        // loots like any item into an empty slot; the view tells whether a Bag is empty, and
        // without one a Bag in the corpse equipment counts as full.
        public static bool CanLoot(
            InventoryClientState state,
            CorpseLootView view,
            CorpseLootItem corpseItem,
            CorpseLootSection sourceSection,
            CorpseLootSlot sourceSlot,
            InventoryContainer destination,
            InventorySlot destinationSlot,
            int? partialQuantity,
            out string reason)
        {
            reason = string.Empty;
            if (state?.FullSnapshot == null
                || corpseItem == null
                || sourceSection == null
                || sourceSlot == null
                || destination == null
                || destinationSlot == null)
            {
                reason = "The corpse item is no longer available for mutation.";
                return false;
            }

            if (!IsCarriedLootDestination(state.FullSnapshot, destination))
            {
                reason = "Corpse loot can enter only Permanent Inventory or the equipped Bag.";
                return false;
            }

            if (corpseItem.HasBagContents)
            {
                if (BagMayHoldItems(view, corpseItem, sourceSection))
                {
                    reason = "A corpse Bag with contents goes onto the Bag equipment slot.";
                    return false;
                }

                if (destinationSlot.Item != null)
                {
                    reason = "Drop a corpse Bag on an empty slot.";
                    return false;
                }
            }

            if (!TryResolveQuantity(corpseItem.Quantity, partialQuantity, out var quantity))
            {
                reason = "Partial loot quantity must be greater than zero and smaller than the corpse stack.";
                return false;
            }

            var item = new InventoryItem(
                corpseItem.ItemInstanceId,
                corpseItem.DefinitionId,
                quantity,
                corpseItem.Revision,
                Array.Empty<InventoryPolicy>());
            var source = ToLocation(sourceSection, sourceSlot);
            if (destinationSlot.Item == null)
            {
                return InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    item,
                    source,
                    destination,
                    destinationSlot,
                    quantity,
                    out reason);
            }

            var target = new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                destination.ContainerId,
                destination.ContainerType,
                destinationSlot.SlotIndex,
                string.Empty,
                Guid.Empty);
            if (InventoryTargetAdvisor.CanMerge(
                state,
                item,
                source,
                destinationSlot.Item,
                target,
                out reason))
            {
                return true;
            }

            if (partialQuantity != null)
            {
                reason = "A partial stack can only enter an empty slot or merge with a compatible stack.";
                return false;
            }

            return CanOccupyEquipmentSlot(state.Catalog, destinationSlot.Item, sourceSlot, out reason)
                && InventoryTargetAdvisor.CanSwapWithExternalContainer(
                    state,
                    destinationSlot.Item,
                    target,
                    item,
                    ToInventoryContainer(sourceSection),
                    ToInventorySlot(sourceSlot),
                    out reason);
        }

        // A carried item dropped on a corpse slot deposits it. The equipped Bag dropped on
        // the occupied corpse Bag slot is an atomic Bag swap instead.
        public static bool CanDeposit(
            InventoryClientState state,
            InventoryItem item,
            InventoryItemLocation source,
            CorpseLootSection section,
            CorpseLootSlot slot,
            int? partialQuantity,
            out string reason)
        {
            reason = string.Empty;
            if (state?.FullSnapshot == null
                || item == null
                || source == null
                || section == null
                || slot == null)
            {
                reason = "Only a current carried inventory item can enter corpse custody.";
                return false;
            }

            if (source.Kind == InventoryItemLocationKind.Equipment)
            {
                if (IsBagSwap(state, item, source, section, slot))
                {
                    return true;
                }

                reason = "Move ordinary equipped items into carried storage before depositing them.";
                return false;
            }

            if (source.Kind != InventoryItemLocationKind.Container
                || !ItemContainerTypeIds.IsCarried(source.ContainerType))
            {
                reason = "Bank and Recovery Storage items cannot enter a corpse.";
                return false;
            }

            if (slot.Item != null && slot.Item.HasBagContents)
            {
                reason = "An occupied corpse Bag aggregate must use the Bag equipment swap.";
                return false;
            }

            if (!TryResolveQuantity(item.Quantity, partialQuantity, out var quantity))
            {
                reason = "Partial deposit quantity must be greater than zero and smaller than the carried stack.";
                return false;
            }

            var movedItem = WithQuantity(item, quantity);
            if (!CanOccupyEquipmentSlot(state.Catalog, movedItem, slot, out reason))
            {
                return false;
            }

            var corpseContainer = ToInventoryContainer(section);
            var corpseSlot = ToInventorySlot(slot);
            if (slot.Item == null)
            {
                return InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    item,
                    source,
                    corpseContainer,
                    corpseSlot,
                    quantity,
                    out reason);
            }

            var corpseItem = slot.Item.ToInventoryItem();
            if (InventoryTargetAdvisor.CanMerge(state.Catalog, movedItem, corpseItem, out reason))
            {
                return true;
            }

            if (partialQuantity != null)
            {
                reason = "A partial stack can only enter an empty slot or merge with a compatible stack.";
                return false;
            }

            return InventoryTargetAdvisor.CanSwapWithExternalContainer(
                state,
                item,
                source,
                corpseItem,
                corpseContainer,
                corpseSlot,
                out reason);
        }

        // A corpse item dropped on another slot of the same corpse rearranges the corpse.
        public static bool CanMoveWithinCorpse(
            InventoryClientState state,
            CorpseLootView view,
            CorpseLootItem sourceItem,
            CorpseLootSection sourceSection,
            CorpseLootSlot sourceSlot,
            CorpseLootSection destinationSection,
            CorpseLootSlot destinationSlot,
            int? partialQuantity,
            out string reason)
        {
            reason = string.Empty;
            if (state?.Catalog == null
                || view == null
                || sourceItem == null
                || sourceSection == null
                || sourceSlot == null
                || destinationSection == null
                || destinationSlot == null)
            {
                reason = "The corpse source item is no longer available.";
                return false;
            }

            if (sourceSection.ContainerId == destinationSection.ContainerId
                && sourceSlot.SlotIndex == destinationSlot.SlotIndex)
            {
                reason = "The corpse item is already in that slot.";
                return false;
            }

            if (view.BagHasContents(sourceItem) || view.BagHasContents(destinationSlot.Item))
            {
                reason = "A non-empty corpse Bag cannot use an ordinary internal slot move.";
                return false;
            }

            if (!TryResolveQuantity(sourceItem.Quantity, partialQuantity, out var quantity))
            {
                reason = "Partial move quantity must be greater than zero and smaller than the corpse stack.";
                return false;
            }

            var sourceInventoryItem = sourceItem.ToInventoryItem();
            var movedItem = WithQuantity(sourceInventoryItem, quantity);
            if (!CanOccupyEquipmentSlot(state.Catalog, movedItem, destinationSlot, out reason))
            {
                return false;
            }

            var destinationContainer = ToInventoryContainer(destinationSection);
            var sourceLocation = ToLocation(sourceSection, sourceSlot);
            if (destinationSlot.Item == null)
            {
                return InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    movedItem,
                    sourceLocation,
                    destinationContainer,
                    ToInventorySlot(destinationSlot),
                    quantity,
                    out reason);
            }

            var targetItem = destinationSlot.Item.ToInventoryItem();
            if (InventoryTargetAdvisor.CanMerge(state.Catalog, movedItem, targetItem, out reason))
            {
                return true;
            }

            if (partialQuantity != null)
            {
                reason = "A partial stack can only enter an empty slot or merge with a compatible stack.";
                return false;
            }

            return CanOccupyEquipmentSlot(state.Catalog, targetItem, sourceSlot, out reason)
                && InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    sourceInventoryItem,
                    sourceLocation,
                    destinationContainer,
                    ToEmptyInventorySlot(destinationSlot),
                    out reason)
                && InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    targetItem,
                    ToLocation(destinationSection, destinationSlot),
                    ToInventoryContainer(sourceSection),
                    ToEmptyInventorySlot(sourceSlot),
                    out reason);
        }

        // A whole corpse item dropped on an equipment slot equips it. A stack of the equipped
        // definition tops up the equipped stack, and any other equipped item swaps into the
        // corpse slot the loot leaves, as a deposit into that slot would. A live Mob corpse
        // cannot take an item back, so GameService rejects a swap there; a top-up still works.
        public static bool CanLootIntoEquipment(
            InventoryClientState state,
            CorpseLootView view,
            CorpseLootItem corpseItem,
            CorpseLootSection sourceSection,
            CorpseLootSlot sourceSlot,
            InventoryEquipmentSlot equipmentSlot,
            out string reason)
        {
            reason = string.Empty;
            if (state?.FullSnapshot == null
                || corpseItem == null
                || sourceSection == null
                || sourceSlot == null
                || equipmentSlot == null)
            {
                reason = "The corpse item is no longer available for mutation.";
                return false;
            }

            var item = corpseItem.ToInventoryItem();
            var occupant = equipmentSlot.Item;
            // A corpse Bag takes an empty Bag slot with what it holds, and swaps with an equipped
            // Bag only through the Bag aggregate swap.
            if (corpseItem.HasBagContents)
            {
                if (occupant != null)
                {
                    reason = "A corpse Bag swaps only with the equipped Bag on the Bag slot.";
                    return false;
                }

                return InventoryTargetAdvisor.CanEquipIntoSlot(state, item, equipmentSlot, out reason)
                    && FitsWithBagContents(state, view, corpseItem, out reason);
            }
            if (occupant != null && InventoryTargetAdvisor.IsEquippedStackTopUp(state.Catalog, item, occupant))
            {
                return InventoryTargetAdvisor.CanMerge(
                    state,
                    item,
                    ToLocation(sourceSection, sourceSlot),
                    occupant,
                    InventoryItemLocation.ForEquipment(equipmentSlot.SlotId),
                    out reason);
            }

            if (occupant != null)
            {
                if (state.FullSnapshot.EquippedBag != null
                    && state.FullSnapshot.EquippedBag.Item.ItemInstanceId == occupant.ItemInstanceId)
                {
                    reason = "The equipped Bag swaps only with a corpse Bag.";
                    return false;
                }

                if (!CanOccupyEquipmentSlot(state.Catalog, occupant, sourceSlot, out reason)
                    || !InventoryTargetAdvisor.CanPlaceInContainer(
                        state,
                        occupant,
                        InventoryItemLocation.ForEquipment(equipmentSlot.SlotId),
                        ToInventoryContainer(sourceSection),
                        ToEmptyInventorySlot(sourceSlot),
                        out reason))
                {
                    reason = "The equipped item cannot move into the corpse slot: " + reason;
                    return false;
                }
            }

            return InventoryTargetAdvisor.CanEquipIntoSlot(state, item, equipmentSlot, out reason);
        }

        // A corpse Bag with contents dropped on the occupied Bag equipment slot swaps both
        // complete Bags atomically.
        public static bool CanSwapCorpseBagIntoEquipment(
            InventoryClientState state,
            CorpseLootItem corpseBag,
            CorpseLootSection sourceSection,
            InventoryEquipmentSlot equipmentSlot,
            out string reason)
        {
            reason = string.Empty;
            if (corpseBag == null
                || sourceSection == null
                || equipmentSlot == null
                || !string.Equals(equipmentSlot.SlotId, ItemEquipmentSlotIds.Bag, StringComparison.Ordinal)
                || equipmentSlot.Item == null
                || state?.FullSnapshot?.EquippedBag == null
                || equipmentSlot.Item.ItemInstanceId != state.FullSnapshot.EquippedBag.Item.ItemInstanceId
                || !corpseBag.HasBagContents
                || !string.Equals(
                    sourceSection.SectionKind,
                    CorpseSectionKindIds.Equipment,
                    StringComparison.Ordinal))
            {
                reason = "Atomic corpse Bag swap requires both occupied Bag equipment aggregates.";
                return false;
            }

            return true;
        }

        // The equipped Bag dropped on the occupied corpse Bag slot is the reverse direction of
        // the atomic Bag swap.
        public static bool IsBagSwap(
            InventoryClientState state,
            InventoryItem item,
            InventoryItemLocation source,
            CorpseLootSection section,
            CorpseLootSlot slot)
        {
            return item != null
                && source != null
                && section != null
                && slot != null
                && source.Kind == InventoryItemLocationKind.Equipment
                && string.Equals(source.EquipmentSlotId, ItemEquipmentSlotIds.Bag, StringComparison.Ordinal)
                && state?.FullSnapshot?.EquippedBag?.Item.ItemInstanceId == item.ItemInstanceId
                && string.Equals(section.SectionKind, CorpseSectionKindIds.Equipment, StringComparison.Ordinal)
                && slot.Item != null
                && slot.Item.HasBagContents;
        }

        // A corpse equipment slot accepts compatible items up to its equipment slot limit,
        // counting a stack in the slot that the item merges with.
        private static bool CanOccupyEquipmentSlot(
            ClientItemCatalog catalog,
            InventoryItem item,
            CorpseLootSlot slot,
            out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(slot.EquipmentSlotId))
            {
                return true;
            }

            if (!catalog.TryGetEquipmentSlot(slot.EquipmentSlotId, out var equipmentSlot)
                || !catalog.TryGetDefinition(item.DefinitionId, out var definition)
                || !ItemEquipmentRules.IsCompatible(definition, slot.EquipmentSlotId))
            {
                reason = "The item is not compatible with corpse equipment slot '"
                    + slot.EquipmentSlotId + "'.";
                return false;
            }

            long quantity = item.Quantity;
            if (slot.Item != null
                && slot.Item.ItemInstanceId != item.ItemInstanceId
                && InventoryTargetAdvisor.CanMerge(catalog, item, slot.Item.ToInventoryItem(), out _))
            {
                quantity += slot.Item.Quantity;
            }

            if (!ItemEquipmentRules.CanHoldQuantity(definition, equipmentSlot, quantity))
            {
                reason = "Corpse equipment slot '" + slot.EquipmentSlotId + "' holds at most "
                    + ItemEquipmentRules.MaximumQuantity(definition, equipmentSlot) + ".";
                return false;
            }

            return true;
        }

        // Only the corpse's equipped Bag can hold items. Without a view a Bag in the corpse
        // equipment may.
        private static bool BagMayHoldItems(CorpseLootView view, CorpseLootItem bag, CorpseLootSection sourceSection)
        {
            return view == null
                ? string.Equals(sourceSection.SectionKind, CorpseSectionKindIds.Equipment, StringComparison.Ordinal)
                : view.BagHasContents(bag);
        }

        // An equipped Bag carries its contents, so their weight counts against the larger
        // capacity it brings.
        private static bool FitsWithBagContents(
            InventoryClientState state,
            CorpseLootView view,
            CorpseLootItem bag,
            out string reason)
        {
            reason = "The Bag definition is unavailable.";
            if (!state.Catalog.TryGetDefinition(bag.DefinitionId, out var definition) || definition.Bag == null)
            {
                return false;
            }

            try
            {
                var weight = state.FullSnapshot.CarriedWeight;
                foreach (var content in CorpseInventoryPrediction.BagContents(view, bag, definition).Slots
                             .Where(slot => slot.Item != null))
                {
                    if (!state.Catalog.TryGetDefinition(content.Item.DefinitionId, out var contentDefinition))
                    {
                        return false;
                    }

                    weight = checked(weight + ItemWeightRules.CalculateStackWeight(
                        contentDefinition.UnitWeight,
                        content.Item.Quantity));
                }

                reason = "The Bag and its contents would exceed the 140 percent carry cap.";
                return EncumbranceRules.IsAllowedVoluntaryTransition(
                    state.FullSnapshot.CarriedWeight,
                    state.FullSnapshot.CarryCapacity,
                    weight,
                    checked(state.FullSnapshot.CarryCapacity + definition.Bag.CarryCapacityBonus));
            }
            catch (OverflowException)
            {
                reason = "The Bag weight is invalid.";
                return false;
            }
        }

        private static bool IsCarriedLootDestination(
            CharacterInventorySnapshot snapshot,
            InventoryContainer container)
        {
            return snapshot.PermanentInventory.ContainerId == container.ContainerId
                || snapshot.EquippedBag?.Contents.ContainerId == container.ContainerId;
        }

        private static bool TryResolveQuantity(int stackQuantity, int? partialQuantity, out int quantity)
        {
            quantity = partialQuantity ?? stackQuantity;
            return partialQuantity == null
                || (partialQuantity.Value > 0 && partialQuantity.Value < stackQuantity);
        }

        private static InventoryItem WithQuantity(InventoryItem item, int quantity)
        {
            return quantity == item.Quantity
                ? item
                : new InventoryItem(
                    item.ItemInstanceId,
                    item.DefinitionId,
                    quantity,
                    item.Revision,
                    item.Policies.ToArray());
        }

        private static InventoryItemLocation ToLocation(
            CorpseLootSection section,
            CorpseLootSlot slot)
        {
            return new InventoryItemLocation(
                InventoryItemLocationKind.Corpse,
                section.ContainerId,
                ContainerType(section),
                slot.SlotIndex,
                string.Empty,
                Guid.Empty);
        }

        private static InventoryContainer ToInventoryContainer(CorpseLootSection section)
        {
            return new InventoryContainer(
                section.ContainerId,
                ContainerType(section),
                section.ContainerRevision,
                section.SlotCapacity,
                section.Slots.Select(ToInventorySlot).ToArray());
        }

        private static InventorySlot ToInventorySlot(CorpseLootSlot slot)
        {
            return new InventorySlot(
                slot.SlotIndex,
                slot.SlotKind,
                slot.AcceptedTags.ToArray(),
                slot.Item?.ToInventoryItem());
        }

        private static InventorySlot ToEmptyInventorySlot(CorpseLootSlot slot)
        {
            return new InventorySlot(
                slot.SlotIndex,
                slot.SlotKind,
                slot.AcceptedTags.ToArray(),
                null);
        }

        // Corpse views only carry the three canonical sections, so every section maps.
        private static string ContainerType(CorpseLootSection section)
        {
            if (!CorpseSectionKindIds.TryGetContainerType(section.SectionKind, out var containerType))
            {
                throw new InvalidOperationException(
                    "Corpse section kind is not canonical: " + section.SectionKind);
            }

            return containerType;
        }
    }
}
