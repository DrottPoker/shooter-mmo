using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
    public enum InventoryQuickMoveKind
    {
        None = 0,
        Equip = 1,
        Unequip = 2,
        Relocate = 3,
        Merge = 4,
        ClaimRecovery = 5,
        Loot = 6,
        LootToEquipment = 7
    }

    // What a right-click does: one operation, which an equip may precede with unequips of the
    // items that block its slot, or nothing and why.
    public sealed class InventoryQuickMove
    {
        private InventoryQuickMove(
            InventoryQuickMoveKind kind,
            string reason,
            InventoryItem item = null,
            InventoryItem target = null,
            InventoryContainer container = null,
            InventorySlot slot = null,
            string equipmentSlotId = null,
            RecoveryDelivery delivery = null,
            CorpseLootItem corpseItem = null,
            IReadOnlyList<InventoryQuickMove> before = null)
        {
            Kind = kind;
            Reason = reason ?? string.Empty;
            Item = item;
            Target = target;
            Container = container;
            Slot = slot;
            EquipmentSlotId = equipmentSlotId ?? string.Empty;
            Delivery = delivery;
            CorpseItem = corpseItem;
            Before = before ?? Array.Empty<InventoryQuickMove>();
        }

        public InventoryQuickMoveKind Kind { get; }

        public string Reason { get; }

        public InventoryItem Item { get; }

        // The stack a merge tops up.
        public InventoryItem Target { get; }

        public InventoryContainer Container { get; }

        public InventorySlot Slot { get; }

        public string EquipmentSlotId { get; }

        public RecoveryDelivery Delivery { get; }

        public CorpseLootItem CorpseItem { get; }

        // The unequips that free an equip's slot first.
        public IReadOnlyList<InventoryQuickMove> Before { get; }

        public static InventoryQuickMove Nothing(string reason)
        {
            return new InventoryQuickMove(InventoryQuickMoveKind.None, reason);
        }

        internal static InventoryQuickMove Equip(InventoryItem item, string equipmentSlotId, IReadOnlyList<InventoryQuickMove> before)
        {
            return new InventoryQuickMove(InventoryQuickMoveKind.Equip, string.Empty, item, equipmentSlotId: equipmentSlotId, before: before);
        }

        internal static InventoryQuickMove Unequip(InventoryItem item, InventoryContainer container, InventorySlot slot)
        {
            return new InventoryQuickMove(InventoryQuickMoveKind.Unequip, string.Empty, item, container: container, slot: slot);
        }

        internal static InventoryQuickMove Relocate(InventoryItem item, InventoryContainer container, InventorySlot slot)
        {
            return new InventoryQuickMove(InventoryQuickMoveKind.Relocate, string.Empty, item, container: container, slot: slot);
        }

        internal static InventoryQuickMove Merge(InventoryItem item, InventoryItem target)
        {
            return new InventoryQuickMove(InventoryQuickMoveKind.Merge, string.Empty, item, target);
        }

        internal static InventoryQuickMove Claim(RecoveryDelivery delivery, InventoryContainer container)
        {
            return new InventoryQuickMove(InventoryQuickMoveKind.ClaimRecovery, string.Empty, container: container, delivery: delivery);
        }

        internal static InventoryQuickMove Loot(CorpseLootItem item, InventoryContainer container, InventorySlot slot)
        {
            return new InventoryQuickMove(InventoryQuickMoveKind.Loot, string.Empty, container: container, slot: slot, corpseItem: item);
        }

        internal static InventoryQuickMove LootToEquipment(CorpseLootItem item, string equipmentSlotId)
        {
            return new InventoryQuickMove(InventoryQuickMoveKind.LootToEquipment, string.Empty, equipmentSlotId: equipmentSlotId, corpseItem: item);
        }
    }

    // Right-click moves. An equippable carried item takes a free compatible equipment slot, or
    // replaces the item in one, after the items that block that slot moved to carried storage;
    // arrows top up the equipped stack. An equipped item goes to carried storage. With the Bank
    // open every own item goes to the Bank, and a Bank item comes back. A Recovery delivery is
    // claimed into Permanent Inventory. A corpse item takes a free compatible equipment slot,
    // never replacing anything, or else carried storage. Carried storage fills as a purchase
    // does: stacks first, then matching specialized Bag slots, Permanent Inventory, and general
    // Bag slots. Every move is checked against the prediction its operation would make, so the
    // client never sends a move it knows the server refuses.
    public static class InventoryQuickMoves
    {
        public static InventoryQuickMove ForItem(InventoryClientState state, InventoryItem item, bool bankOpen)
        {
            if (state?.Catalog == null
                || state.FullSnapshot == null
                || item == null
                || !state.TryFindItem(item.ItemInstanceId, out var current, out var location)
                || !state.Catalog.TryGetDefinition(current.DefinitionId, out var definition))
            {
                return InventoryQuickMove.Nothing("The item is no longer available.");
            }

            if (string.Equals(location.ContainerType, ItemContainerTypeIds.Bank, StringComparison.Ordinal))
            {
                return ToCarriedStorage(state, current, location, "Your inventory has no room for the item.");
            }

            if (bankOpen && state.Bank != null)
            {
                return ToContainers(state, current, location, new[] { state.Bank }, new[] { state.Bank }, "The Bank has no room for the item.");
            }

            if (location.Kind == InventoryItemLocationKind.Equipment)
            {
                return ToCarriedStorage(state, current, location, "Your inventory has no room for the item.");
            }

            if (definition.EquipmentSlots == null || definition.EquipmentSlots.Length == 0)
            {
                return InventoryQuickMove.Nothing("Right-click equips equipment; drag this item to move it.");
            }

            return Equip(state, current, location, definition);
        }

        public static InventoryQuickMove ForRecoveryDelivery(InventoryClientState state, RecoveryDelivery delivery)
        {
            var inventory = state?.FullSnapshot?.PermanentInventory;
            if (delivery == null || inventory == null)
            {
                return InventoryQuickMove.Nothing("The Recovery delivery is no longer available.");
            }

            var claim = InventoryPredictedOperation.ClaimRecovery(delivery, inventory.ContainerId);
            return InventoryOperationPredictor.TryApply(state, claim, out _, out var reason)
                ? InventoryQuickMove.Claim(delivery, inventory)
                : InventoryQuickMove.Nothing(reason);
        }

        public static InventoryQuickMove ForCorpseItem(InventoryClientState state, CorpseLootView view, CorpseLootItem corpseItem)
        {
            if (state?.Catalog == null
                || state.FullSnapshot == null
                || view == null
                || corpseItem == null
                || !view.TryFindItem(corpseItem.ItemInstanceId, out var item, out var section, out var slot)
                || !state.Catalog.TryGetDefinition(item.DefinitionId, out var definition))
            {
                return InventoryQuickMove.Nothing("The corpse item is no longer available.");
            }

            var reason = "Your equipment and inventory have no room for the item.";
            if (definition.EquipmentSlots != null && definition.RequiredCharacterLevel <= 1)
            {
                foreach (var equipmentSlot in state.FullSnapshot.Equipment.Where(candidate =>
                             ItemEquipmentRules.IsCompatible(definition, candidate.SlotId)))
                {
                    var topsUp = equipmentSlot.Item != null
                        && InventoryTargetAdvisor.IsEquippedStackTopUp(state.Catalog, item.ToInventoryItem(), equipmentSlot.Item);
                    if ((equipmentSlot.Item == null || topsUp)
                        && CorpseTargetAdvisor.CanLootIntoEquipment(state, view, item, section, slot, equipmentSlot, out _))
                    {
                        return InventoryQuickMove.LootToEquipment(item, equipmentSlot.SlotId);
                    }
                }
            }

            if (item.HasBagContents && view.BagHasContents(item))
            {
                return InventoryQuickMove.Nothing("Your Bag slot is taken. Drag the corpse Bag onto your Bag to swap them.");
            }

            // A stack that takes the whole item, else the first free slot; a loot never swaps.
            foreach (var (container, carriedSlot) in CarriedStacks(state))
            {
                if (InventoryTargetAdvisor.CanMerge(state.Catalog, item.ToInventoryItem(), carriedSlot.Item, out _)
                    && CorpseTargetAdvisor.CanLoot(state, view, item, section, slot, container, carriedSlot, null, out _))
                {
                    return InventoryQuickMove.Loot(item, container, carriedSlot);
                }
            }

            foreach (var (container, carriedSlot) in CarriedFreeSlots(state))
            {
                if (CorpseTargetAdvisor.CanLoot(state, view, item, section, slot, container, carriedSlot, null, out var lootReason))
                {
                    return InventoryQuickMove.Loot(item, container, carriedSlot);
                }

                if (!lootReason.Contains("accepted tags"))
                {
                    reason = lootReason;
                }
            }

            return InventoryQuickMove.Nothing(reason);
        }

        private static InventoryQuickMove Equip(
            InventoryClientState state,
            InventoryItem item,
            InventoryItemLocation location,
            ItemDefinition definition)
        {
            var compatible = state.FullSnapshot.Equipment
                .Where(slot => ItemEquipmentRules.IsCompatible(definition, slot.SlotId))
                .ToArray();
            var reason = "The item cannot be equipped.";
            var topUp = compatible.FirstOrDefault(slot => InventoryTargetAdvisor.IsEquippedStackTopUp(state.Catalog, item, slot.Item));
            if (topUp != null)
            {
                return Predicts(state, InventoryPredictedOperation.Merge(item.ItemInstanceId, topUp.Item.ItemInstanceId), out reason)
                    ? InventoryQuickMove.Merge(item, topUp.Item)
                    : InventoryQuickMove.Nothing(reason);
            }

            // A free slot first, then one whose item swaps into the slot the new item leaves.
            foreach (var slot in compatible.Where(slot => slot.Item == null).Concat(compatible.Where(slot => slot.Item != null)))
            {
                if (TryPlanEquip(state, item, slot, out var move, out var equipReason))
                {
                    return move;
                }

                reason = equipReason;
            }

            return InventoryQuickMove.Nothing(reason);
        }

        // The items that block the slot move to carried storage first, each into the next room
        // that the moves before it left.
        private static bool TryPlanEquip(
            InventoryClientState state,
            InventoryItem item,
            InventoryEquipmentSlot slot,
            out InventoryQuickMove move,
            out string reason)
        {
            move = null;
            IInventoryView view = state;
            var before = new List<InventoryQuickMove>();
            foreach (var blocker in BlockingItems(state, item, slot))
            {
                var blockerSlot = view.FullSnapshot.Equipment.FirstOrDefault(candidate =>
                    candidate.Item != null && candidate.Item.ItemInstanceId == blocker.ItemInstanceId);
                if (blockerSlot == null)
                {
                    reason = "The item that blocks the slot is no longer equipped.";
                    return false;
                }

                var unequip = ToCarriedStorage(
                    view,
                    blockerSlot.Item,
                    InventoryItemLocation.ForEquipment(blockerSlot.SlotId),
                    "Your inventory has no room for the item that blocks the slot.");
                if (unequip.Kind == InventoryQuickMoveKind.None)
                {
                    reason = unequip.Reason;
                    return false;
                }

                if (!InventoryOperationPredictor.TryApply(view, ToOperation(unequip), out var predicted, out reason))
                {
                    return false;
                }

                before.Add(unequip);
                view = new InventorySnapshotView(view.Catalog, predicted);
            }

            if (!InventoryOperationPredictor.TryApply(
                    view,
                    InventoryPredictedOperation.Equip(item.ItemInstanceId, slot.SlotId),
                    out _,
                    out reason))
            {
                return false;
            }

            move = InventoryQuickMove.Equip(item, slot.SlotId, before);
            return true;
        }

        // The equipped items, other than the one in the slot, that block the slot or that the
        // item would block, such as a shield for a two-handed bow.
        private static IEnumerable<InventoryItem> BlockingItems(
            InventoryClientState state,
            InventoryItem item,
            InventoryEquipmentSlot slot)
        {
            state.Catalog.TryGetDefinition(item.DefinitionId, out var definition);
            foreach (var other in state.FullSnapshot.Equipment)
            {
                if (other.Item == null
                    || string.Equals(other.SlotId, slot.SlotId, StringComparison.Ordinal)
                    || !state.Catalog.TryGetDefinition(other.Item.DefinitionId, out var otherDefinition))
                {
                    continue;
                }

                var otherBlocksSlot = otherDefinition.BlockedEquipmentSlots != null
                    && otherDefinition.BlockedEquipmentSlots.Contains(slot.SlotId, StringComparer.Ordinal);
                var itemBlocksOther = definition?.BlockedEquipmentSlots != null
                    && definition.BlockedEquipmentSlots.Contains(other.SlotId, StringComparer.Ordinal);
                if (otherBlocksSlot || itemBlocksOther)
                {
                    yield return other.Item;
                }
            }
        }

        private static InventoryQuickMove ToCarriedStorage(
            IInventoryView view,
            InventoryItem item,
            InventoryItemLocation location,
            string failure)
        {
            var snapshot = view.FullSnapshot;
            var bag = snapshot.EquippedBag?.Contents;
            var stacks = new[] { snapshot.PermanentInventory, bag }.Where(container => container != null).ToArray();
            var free = new List<(InventoryContainer, InventorySlot)>();
            if (bag != null)
            {
                free.AddRange(bag.Slots.Where(IsSpecialized).Select(slot => (bag, slot)));
            }

            free.AddRange(snapshot.PermanentInventory.Slots.Select(slot => (snapshot.PermanentInventory, slot)));
            if (bag != null)
            {
                free.AddRange(bag.Slots.Where(slot => !IsSpecialized(slot)).Select(slot => (bag, slot)));
            }

            return ToSlots(view, item, location, stacks, free, failure);
        }

        private static InventoryQuickMove ToContainers(
            IInventoryView view,
            InventoryItem item,
            InventoryItemLocation location,
            IReadOnlyList<InventoryContainer> stackContainers,
            IReadOnlyList<InventoryContainer> slotContainers,
            string failure)
        {
            return ToSlots(
                view,
                item,
                location,
                stackContainers,
                slotContainers.SelectMany(container => container.Slots.Select(slot => (container, slot))).ToArray(),
                failure);
        }

        // Tops up a stack that takes the whole item, or else fills the first free slot that
        // takes it.
        private static InventoryQuickMove ToSlots(
            IInventoryView view,
            InventoryItem item,
            InventoryItemLocation location,
            IReadOnlyList<InventoryContainer> stackContainers,
            IReadOnlyList<(InventoryContainer Container, InventorySlot Slot)> freeSlots,
            string failure)
        {
            var reason = failure;
            foreach (var target in stackContainers.SelectMany(container => container.Slots).Select(slot => slot.Item))
            {
                if (target != null
                    && target.ItemInstanceId != item.ItemInstanceId
                    && InventoryTargetAdvisor.CanMerge(view.Catalog, item, target, out _)
                    && InventoryOperationPredictor.TryApply(
                        view,
                        InventoryPredictedOperation.Merge(item.ItemInstanceId, target.ItemInstanceId),
                        out _,
                        out _))
                {
                    return InventoryQuickMove.Merge(item, target);
                }
            }

            foreach (var (container, slot) in freeSlots.Where(candidate => candidate.Slot.Item == null))
            {
                var move = location.Kind == InventoryItemLocationKind.Equipment
                    ? InventoryQuickMove.Unequip(item, container, slot)
                    : InventoryQuickMove.Relocate(item, container, slot);
                if (InventoryOperationPredictor.TryApply(view, ToOperation(move), out _, out var moveReason))
                {
                    return move;
                }

                // A slot that only refuses the item's kind says nothing about the rest.
                if (!moveReason.Contains("accepted tags"))
                {
                    reason = moveReason;
                }
            }

            return InventoryQuickMove.Nothing(reason);
        }

        private static IEnumerable<(InventoryContainer, InventorySlot)> CarriedStacks(InventoryClientState state)
        {
            var snapshot = state.FullSnapshot;
            foreach (var container in new[] { snapshot.PermanentInventory, snapshot.EquippedBag?.Contents })
            {
                if (container == null)
                {
                    continue;
                }

                foreach (var slot in container.Slots.Where(slot => slot.Item != null))
                {
                    yield return (container, slot);
                }
            }
        }

        private static IEnumerable<(InventoryContainer, InventorySlot)> CarriedFreeSlots(InventoryClientState state)
        {
            var snapshot = state.FullSnapshot;
            var bag = snapshot.EquippedBag?.Contents;
            var slots = new List<(InventoryContainer, InventorySlot)>();
            if (bag != null)
            {
                slots.AddRange(bag.Slots.Where(slot => slot.Item == null && IsSpecialized(slot)).Select(slot => (bag, slot)));
            }

            slots.AddRange(snapshot.PermanentInventory.Slots.Where(slot => slot.Item == null).Select(slot => (snapshot.PermanentInventory, slot)));
            if (bag != null)
            {
                slots.AddRange(bag.Slots.Where(slot => slot.Item == null && !IsSpecialized(slot)).Select(slot => (bag, slot)));
            }

            return slots;
        }

        private static InventoryPredictedOperation ToOperation(InventoryQuickMove move)
        {
            switch (move.Kind)
            {
                case InventoryQuickMoveKind.Unequip:
                    return InventoryPredictedOperation.Unequip(move.Item.ItemInstanceId, move.Container.ContainerId, move.Slot.SlotIndex);
                case InventoryQuickMoveKind.Relocate:
                    return InventoryPredictedOperation.Relocate(move.Item.ItemInstanceId, move.Container.ContainerId, move.Slot.SlotIndex);
                case InventoryQuickMoveKind.Merge:
                    return InventoryPredictedOperation.Merge(move.Item.ItemInstanceId, move.Target.ItemInstanceId);
                default:
                    throw new InvalidOperationException("The quick move has no single item operation.");
            }
        }

        private static bool Predicts(IInventoryView view, InventoryPredictedOperation operation, out string reason)
        {
            return InventoryOperationPredictor.TryApply(view, operation, out _, out reason);
        }

        private static bool IsSpecialized(InventorySlot slot)
        {
            return !string.Equals(slot.SlotKind, BagSlotKindIds.General, StringComparison.Ordinal);
        }
    }
}
