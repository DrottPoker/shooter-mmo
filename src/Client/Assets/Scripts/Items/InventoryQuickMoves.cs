using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
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
        LootToEquipment = 7,
        Split = 8
    }

    // What a right-click does: one operation, which an equip may precede with unequips of the
    // items that block its slot, or nothing and why.
    public sealed class InventoryQuickMove
    {
        private InventoryQuickMove(
            InventoryQuickMoveKind kind,
            ClientText reason,
            InventoryItem item = null,
            InventoryItem target = null,
            InventoryContainer container = null,
            InventorySlot slot = null,
            string equipmentSlotId = null,
            RecoveryDelivery delivery = null,
            CorpseLootItem corpseItem = null,
            IReadOnlyList<InventoryQuickMove> before = null,
            int quantity = 0)
        {
            Kind = kind;
            Reason = reason;
            Item = item;
            Target = target;
            Container = container;
            Slot = slot;
            EquipmentSlotId = equipmentSlotId ?? string.Empty;
            Delivery = delivery;
            CorpseItem = corpseItem;
            Before = before ?? Array.Empty<InventoryQuickMove>();
            Quantity = quantity;
        }

        public InventoryQuickMoveKind Kind { get; }

        public ClientText Reason { get; }

        // The part of the stack a split, a merge, an equip, or a loot moves; zero moves the
        // whole stack.
        public int Quantity { get; }

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

        public static InventoryQuickMove Nothing(ClientText reason)
        {
            return new InventoryQuickMove(InventoryQuickMoveKind.None, reason);
        }

        internal static InventoryQuickMove Equip(
            InventoryItem item,
            string equipmentSlotId,
            IReadOnlyList<InventoryQuickMove> before,
            int quantity = 0)
        {
            return new InventoryQuickMove(
                InventoryQuickMoveKind.Equip,
                ClientText.None,
                item,
                equipmentSlotId: equipmentSlotId,
                before: before,
                quantity: quantity);
        }

        internal static InventoryQuickMove Unequip(InventoryItem item, InventoryContainer container, InventorySlot slot)
        {
            return new InventoryQuickMove(InventoryQuickMoveKind.Unequip, ClientText.None, item, container: container, slot: slot);
        }

        internal static InventoryQuickMove Relocate(InventoryItem item, InventoryContainer container, InventorySlot slot)
        {
            return new InventoryQuickMove(InventoryQuickMoveKind.Relocate, ClientText.None, item, container: container, slot: slot);
        }

        internal static InventoryQuickMove Merge(InventoryItem item, InventoryItem target, int quantity = 0)
        {
            return new InventoryQuickMove(InventoryQuickMoveKind.Merge, ClientText.None, item, target, quantity: quantity);
        }

        internal static InventoryQuickMove Claim(RecoveryDelivery delivery, InventoryContainer container)
        {
            return new InventoryQuickMove(InventoryQuickMoveKind.ClaimRecovery, ClientText.None, container: container, delivery: delivery);
        }

        internal static InventoryQuickMove Split(InventoryItem item, int quantity, InventoryContainer container, InventorySlot slot)
        {
            return new InventoryQuickMove(InventoryQuickMoveKind.Split, ClientText.None, item, container: container, slot: slot, quantity: quantity);
        }

        internal static InventoryQuickMove Loot(CorpseLootItem item, InventoryContainer container, InventorySlot slot, int quantity = 0)
        {
            return new InventoryQuickMove(InventoryQuickMoveKind.Loot, ClientText.None, container: container, slot: slot, corpseItem: item, quantity: quantity);
        }

        internal static InventoryQuickMove LootToEquipment(CorpseLootItem item, string equipmentSlotId, int quantity = 0)
        {
            return new InventoryQuickMove(
                InventoryQuickMoveKind.LootToEquipment,
                ClientText.None,
                equipmentSlotId: equipmentSlotId,
                corpseItem: item,
                quantity: quantity);
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
                return InventoryQuickMove.Nothing(ClientText.Of("ui/items/item-unavailable"));
            }

            if (string.Equals(location.ContainerType, ItemContainerTypeIds.Bank, StringComparison.Ordinal))
            {
                return ToCarriedStorage(state, current, location, ClientText.Of("ui/items/inventory-full"));
            }

            if (bankOpen && state.Bank != null)
            {
                return ToContainers(state, current, location, new[] { state.Bank }, new[] { state.Bank }, ClientText.Of("ui/items/bank-full"));
            }

            if (location.Kind == InventoryItemLocationKind.Equipment)
            {
                return ToCarriedStorage(state, current, location, ClientText.Of("ui/items/inventory-full"));
            }

            if (definition.EquipmentSlots == null || definition.EquipmentSlots.Length == 0)
            {
                return InventoryQuickMove.Nothing(ClientText.Of("ui/items/right-click-equips"));
            }

            return Equip(state, current, location, definition);
        }

        public static InventoryQuickMove ForRecoveryDelivery(InventoryClientState state, RecoveryDelivery delivery)
        {
            var inventory = state?.FullSnapshot?.PermanentInventory;
            if (delivery == null || inventory == null)
            {
                return InventoryQuickMove.Nothing(ClientText.Of("ui/items/recovery-delivery-unavailable"));
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
                return InventoryQuickMove.Nothing(ClientText.Of("ui/corpse/item-unavailable"));
            }

            var reason = ClientText.Of("ui/items/equipment-and-inventory-full");
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
                return InventoryQuickMove.Nothing(ClientText.Of("ui/corpse/bag-slot-taken"));
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

                if (!IsSlotKindRefusal(lootReason))
                {
                    reason = lootReason;
                }
            }

            return InventoryQuickMove.Nothing(reason);
        }

        // Half of a stack, rounded down: what Shift drags and Shift+right-click sends.
        public static int HalfOf(int quantity)
        {
            return Math.Max(0, quantity) / 2;
        }

        // Shift+right-click: part of a stack goes where right-click sends the stack, and the
        // rest stays. Like the whole stack, it tops up a stack first and else takes a free
        // slot; arrows top up the equipped arrows or fill an empty ammunition slot. A part never
        // replaces an equipped item.
        public static InventoryQuickMove ForItemPart(InventoryClientState state, InventoryItem item, int quantity, bool bankOpen)
        {
            if (state?.Catalog == null
                || state.FullSnapshot == null
                || item == null
                || !state.TryFindItem(item.ItemInstanceId, out var current, out var location)
                || !state.Catalog.TryGetDefinition(current.DefinitionId, out var definition))
            {
                return InventoryQuickMove.Nothing(ClientText.Of("ui/items/item-unavailable"));
            }

            if (!InventoryTargetAdvisor.CanSplit(state.Catalog, current, quantity, out var reason))
            {
                return InventoryQuickMove.Nothing(reason);
            }

            if (string.Equals(location.ContainerType, ItemContainerTypeIds.Bank, StringComparison.Ordinal))
            {
                return PartToCarriedStorage(state, current, quantity);
            }

            if (bankOpen && state.Bank != null)
            {
                var bank = state.Bank;
                return PartToSlots(
                    state,
                    current,
                    quantity,
                    bank.Slots.Where(slot => slot.Item != null).Select(slot => (bank, slot)),
                    bank.Slots.Where(slot => slot.Item == null).Select(slot => (bank, slot)),
                    ClientText.Of("ui/items/bank-full"));
            }

            if (location.Kind == InventoryItemLocationKind.Equipment)
            {
                return PartToCarriedStorage(state, current, quantity);
            }

            if (definition.EquipmentSlots == null || definition.EquipmentSlots.Length == 0)
            {
                return InventoryQuickMove.Nothing(ClientText.Of("ui/items/right-click-equips"));
            }

            return EquipPart(state, current, definition, quantity);
        }

        // Shift+right-click on a corpse stack: part of it goes where right-click sends the stack,
        // and the rest stays in the corpse. It tops up the equipped stack of its kind or fills an
        // empty compatible equipment slot, else tops up a carried stack or takes the first free
        // carried slot.
        public static InventoryQuickMove ForCorpseItemPart(
            InventoryClientState state,
            CorpseLootView view,
            CorpseLootItem corpseItem,
            int quantity)
        {
            if (state?.Catalog == null
                || state.FullSnapshot == null
                || view == null
                || corpseItem == null
                || !view.TryFindItem(corpseItem.ItemInstanceId, out var item, out var section, out var slot))
            {
                return InventoryQuickMove.Nothing(ClientText.Of("ui/corpse/item-unavailable"));
            }

            if (quantity < 1 || quantity >= item.Quantity)
            {
                return InventoryQuickMove.Nothing(ClientText.Of("ui/items/single-item-split"));
            }

            var part = item.WithQuantity(quantity);
            if (state.Catalog.TryGetDefinition(item.DefinitionId, out var definition)
                && definition.EquipmentSlots != null
                && definition.RequiredCharacterLevel <= 1)
            {
                foreach (var equipmentSlot in state.FullSnapshot.Equipment.Where(candidate =>
                             ItemEquipmentRules.IsCompatible(definition, candidate.SlotId)))
                {
                    var takesPart = equipmentSlot.Item == null
                        || InventoryTargetAdvisor.IsEquippedStackTopUp(state.Catalog, part.ToInventoryItem(), equipmentSlot.Item);
                    if (takesPart && CorpseTargetAdvisor.CanLootIntoEquipment(state, view, part, section, slot, equipmentSlot, out _))
                    {
                        return InventoryQuickMove.LootToEquipment(item, equipmentSlot.SlotId, quantity);
                    }
                }
            }

            var reason = ClientText.Of("ui/items/inventory-full");
            foreach (var (container, carriedSlot) in CarriedStacks(state).Concat(CarriedFreeSlots(state)))
            {
                if (CorpseTargetAdvisor.CanLoot(state, view, item, section, slot, container, carriedSlot, quantity, out var lootReason))
                {
                    return InventoryQuickMove.Loot(item, container, carriedSlot, quantity);
                }

                if (carriedSlot.Item == null && !IsSlotKindRefusal(lootReason))
                {
                    reason = lootReason;
                }
            }

            return InventoryQuickMove.Nothing(reason);
        }

        private static InventoryQuickMove PartToCarriedStorage(InventoryClientState state, InventoryItem item, int quantity)
        {
            return PartToSlots(
                state,
                item,
                quantity,
                CarriedStacks(state),
                CarriedFreeSlots(state),
                ClientText.Of("ui/items/inventory-full"));
        }

        // Tops up the first stack that takes the part, or else splits it into the first free
        // slot that takes it.
        private static InventoryQuickMove PartToSlots(
            IInventoryView view,
            InventoryItem item,
            int quantity,
            IEnumerable<(InventoryContainer Container, InventorySlot Slot)> stacks,
            IEnumerable<(InventoryContainer Container, InventorySlot Slot)> freeSlots,
            ClientText failure)
        {
            var part = item.WithQuantity(quantity);
            foreach (var (_, slot) in stacks)
            {
                if (slot.Item != null
                    && slot.Item.ItemInstanceId != item.ItemInstanceId
                    && InventoryTargetAdvisor.CanMerge(view.Catalog, part, slot.Item, out _)
                    && Predicts(view, InventoryPredictedOperation.Merge(item.ItemInstanceId, slot.Item.ItemInstanceId, quantity), out _))
                {
                    return InventoryQuickMove.Merge(item, slot.Item, quantity);
                }
            }

            var reason = failure;
            foreach (var (container, slot) in freeSlots)
            {
                var split = InventoryPredictedOperation.Split(item.ItemInstanceId, quantity, container.ContainerId, slot.SlotIndex);
                if (InventoryOperationPredictor.TryApply(view, split, out _, out var splitReason))
                {
                    return InventoryQuickMove.Split(item, quantity, container, slot);
                }

                if (!IsSlotKindRefusal(splitReason))
                {
                    reason = splitReason;
                }
            }

            return InventoryQuickMove.Nothing(reason);
        }

        // Part of a stack tops up the equipped stack of its kind, or else fills an empty
        // compatible slot as a new stack.
        private static InventoryQuickMove EquipPart(
            InventoryClientState state,
            InventoryItem item,
            ItemDefinition definition,
            int quantity)
        {
            var compatible = state.FullSnapshot.Equipment
                .Where(slot => ItemEquipmentRules.IsCompatible(definition, slot.SlotId))
                .ToArray();
            var reason = ClientText.Of("ui/items/cannot-equip");
            var topUp = compatible.FirstOrDefault(slot => InventoryTargetAdvisor.IsEquippedStackTopUp(state.Catalog, item, slot.Item));
            if (topUp != null)
            {
                return Predicts(state, InventoryPredictedOperation.Merge(item.ItemInstanceId, topUp.Item.ItemInstanceId, quantity), out reason)
                    ? InventoryQuickMove.Merge(item, topUp.Item, quantity)
                    : InventoryQuickMove.Nothing(reason);
            }

            foreach (var slot in compatible.Where(slot => slot.Item == null))
            {
                if (Predicts(state, InventoryPredictedOperation.Equip(item.ItemInstanceId, slot.SlotId, quantity), out reason))
                {
                    return InventoryQuickMove.Equip(item, slot.SlotId, Array.Empty<InventoryQuickMove>(), quantity);
                }
            }

            return InventoryQuickMove.Nothing(compatible.Any(slot => slot.Item == null)
                ? reason
                : ClientText.Of("ui/items/equip-part-needs-empty-slot"));
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
            var reason = ClientText.Of("ui/items/cannot-equip");
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
            out ClientText reason)
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
                    reason = ClientText.Of("ui/items/blocker-gone");
                    return false;
                }

                var unequip = ToCarriedStorage(
                    view,
                    blockerSlot.Item,
                    InventoryItemLocation.ForEquipment(blockerSlot.SlotId),
                    ClientText.Of("ui/items/blocker-no-room"));
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
            ClientText failure)
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
            ClientText failure)
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
            ClientText failure)
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
                if (!IsSlotKindRefusal(moveReason))
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

        private static bool Predicts(IInventoryView view, InventoryPredictedOperation operation, out ClientText reason)
        {
            return InventoryOperationPredictor.TryApply(view, operation, out _, out reason);
        }

        private static bool IsSpecialized(InventorySlot slot)
        {
            return !string.Equals(slot.SlotKind, BagSlotKindIds.General, StringComparison.Ordinal);
        }

        // A slot that only refuses the item's kind by its accepted tags.
        private static bool IsSlotKindRefusal(ClientText reason)
        {
            return reason.Key == "ui/items/slot-tags-mismatch"
                || reason.Key == "ui/items/opposite-slot-tags-mismatch";
        }
    }
}
