using System;
using System.Linq;
using Mmorpg.Client.Items;

namespace Mmorpg.Client.Ui
{
    public enum InventoryDropPlanKind
    {
        None,
        Relocate,
        Split,
        Unequip,
        Merge,
        Swap,
        Equip,
        ClaimRecovery,
        CorpseLoot,
        CorpseLootToEquipment,
        CorpseBagSwap,
        CorpseMove,
        CorpseDeposit
    }

    // What a drop sends, decided against the current state, or why it sends nothing. Corpse
    // kinds carry the corpse item or corpse slot they act on.
    public sealed class InventoryDropPlan
    {
        private InventoryDropPlan(
            InventoryDropPlanKind kind,
            string reason,
            InventoryItem item = null,
            InventoryItem target = null,
            InventoryContainer container = null,
            InventorySlot slot = null,
            string equipmentSlotId = null,
            InventoryEquipmentSlot equipmentSlot = null,
            RecoveryDelivery delivery = null,
            CorpseLootItem corpseItem = null,
            CorpseLootSection corpseSection = null,
            CorpseLootSlot corpseSlot = null,
            int quantity = 0)
        {
            Kind = kind;
            Reason = reason ?? string.Empty;
            Item = item;
            Target = target;
            Container = container;
            Slot = slot;
            EquipmentSlotId = equipmentSlotId ?? string.Empty;
            EquipmentSlot = equipmentSlot;
            Delivery = delivery;
            CorpseItem = corpseItem;
            CorpseSection = corpseSection;
            CorpseSlot = corpseSlot;
            Quantity = quantity;
        }

        public InventoryDropPlanKind Kind { get; }

        public bool IsValid
        {
            get { return Kind != InventoryDropPlanKind.None; }
        }

        // Why nothing is sent; empty for a valid plan.
        public string Reason { get; }

        public InventoryItem Item { get; }

        // The item a merge or swap meets.
        public InventoryItem Target { get; }

        public InventoryContainer Container { get; }

        public InventorySlot Slot { get; }

        public string EquipmentSlotId { get; }

        public InventoryEquipmentSlot EquipmentSlot { get; }

        public RecoveryDelivery Delivery { get; }

        public CorpseLootItem CorpseItem { get; }

        public CorpseLootSection CorpseSection { get; }

        public CorpseLootSlot CorpseSlot { get; }

        // The moved quantity of a split or of a corpse loot, deposit, or move.
        public int Quantity { get; }

        // A corpse operation goes through the corpse controller and waits for its result.
        public bool IsCorpseOperation
        {
            get { return Kind >= InventoryDropPlanKind.CorpseLoot; }
        }

        public static InventoryDropPlan Rejected(string reason)
        {
            return new InventoryDropPlan(
                InventoryDropPlanKind.None,
                string.IsNullOrWhiteSpace(reason) ? "This inventory destination is not valid." : reason);
        }

        internal static InventoryDropPlan Relocate(InventoryItem item, InventoryContainer container, InventorySlot slot)
        {
            return new InventoryDropPlan(InventoryDropPlanKind.Relocate, string.Empty, item, container: container, slot: slot);
        }

        internal static InventoryDropPlan Split(InventoryItem item, int quantity, InventoryContainer container, InventorySlot slot)
        {
            return new InventoryDropPlan(InventoryDropPlanKind.Split, string.Empty, item, container: container, slot: slot, quantity: quantity);
        }

        internal static InventoryDropPlan Unequip(InventoryItem item, InventoryContainer container, InventorySlot slot)
        {
            return new InventoryDropPlan(InventoryDropPlanKind.Unequip, string.Empty, item, container: container, slot: slot);
        }

        internal static InventoryDropPlan Merge(InventoryItem item, InventoryItem target)
        {
            return new InventoryDropPlan(InventoryDropPlanKind.Merge, string.Empty, item, target);
        }

        internal static InventoryDropPlan Swap(InventoryItem item, InventoryItem target)
        {
            return new InventoryDropPlan(InventoryDropPlanKind.Swap, string.Empty, item, target);
        }

        internal static InventoryDropPlan Equip(InventoryItem item, InventoryEquipmentSlot equipmentSlot)
        {
            return new InventoryDropPlan(
                InventoryDropPlanKind.Equip,
                string.Empty,
                item,
                equipmentSlot.Item,
                equipmentSlotId: equipmentSlot.SlotId,
                equipmentSlot: equipmentSlot);
        }

        internal static InventoryDropPlan ClaimRecovery(RecoveryDelivery delivery, InventoryContainer container)
        {
            return new InventoryDropPlan(InventoryDropPlanKind.ClaimRecovery, string.Empty, container: container, delivery: delivery);
        }

        internal static InventoryDropPlan CorpseLoot(
            CorpseLootItem corpseItem,
            InventoryContainer container,
            InventorySlot slot,
            int quantity)
        {
            return new InventoryDropPlan(
                InventoryDropPlanKind.CorpseLoot,
                string.Empty,
                container: container,
                slot: slot,
                corpseItem: corpseItem,
                quantity: quantity);
        }

        internal static InventoryDropPlan CorpseLootToEquipment(CorpseLootItem corpseItem, InventoryEquipmentSlot equipmentSlot)
        {
            return new InventoryDropPlan(
                InventoryDropPlanKind.CorpseLootToEquipment,
                string.Empty,
                equipmentSlotId: equipmentSlot.SlotId,
                equipmentSlot: equipmentSlot,
                corpseItem: corpseItem);
        }

        internal static InventoryDropPlan CorpseBagSwap(CorpseLootItem corpseBag)
        {
            return new InventoryDropPlan(InventoryDropPlanKind.CorpseBagSwap, string.Empty, corpseItem: corpseBag);
        }

        internal static InventoryDropPlan CorpseMove(
            CorpseLootItem corpseItem,
            CorpseLootSection section,
            CorpseLootSlot slot,
            int quantity)
        {
            return new InventoryDropPlan(
                InventoryDropPlanKind.CorpseMove,
                string.Empty,
                corpseItem: corpseItem,
                corpseSection: section,
                corpseSlot: slot,
                quantity: quantity);
        }

        internal static InventoryDropPlan CorpseDeposit(
            InventoryItem item,
            CorpseLootSection section,
            CorpseLootSlot slot,
            int quantity)
        {
            return new InventoryDropPlan(
                InventoryDropPlanKind.CorpseDeposit,
                string.Empty,
                item,
                corpseSection: section,
                corpseSlot: slot,
                quantity: quantity);
        }
    }

    // Decides what dropping a dragged item onto a container, equipment, or corpse slot sends.
    // A quantity asks for part of the dragged stack: a split of a carried stack or a partial
    // corpse loot, deposit, or move; null moves the whole stack. Every plan judges the current
    // slot, not the drawn one, since a confirmation may arrive while the player drags. The
    // advisors reuse the shared item rules, and the server still decides every operation.
    public static class InventoryDropPlanner
    {
        // A shown item moves at once, also while earlier moves wait for the server, unless it
        // is a split stack that the server has not confirmed yet.
        public static bool CanMoveItem(InventoryClientState state, InventoryItem item)
        {
            return state != null
                && (state.CanPredict || state.CanMutate)
                && (item == null || !state.IsPredictedItem(item.ItemInstanceId));
        }

        public static InventoryDropPlan PlanContainerDrop(
            InventoryClientState state,
            CorpseLootView corpseView,
            bool canOperateOnCorpse,
            InventoryContainer drawnContainer,
            InventorySlot drawnSlot,
            InventoryDragPayload payload,
            int? quantity)
        {
            if (!TryResolveCurrentSlot(state, drawnContainer, drawnSlot, out var container, out var slot))
            {
                return InventoryDropPlan.Rejected("This slot is no longer available.");
            }

            // Item moves show at once and may follow pending ones; corpse loot and Recovery
            // claims wait for a confirmed state.
            if (!CanMoveItem(state, null))
            {
                return InventoryDropPlan.Rejected("Inventory mutation is unavailable while state is loading.");
            }

            string reason;
            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                if (!TryFindCorpseItem(corpseView, canOperateOnCorpse, payload, out var corpseItem, out var sourceSection, out var sourceSlot))
                {
                    return InventoryDropPlan.Rejected("The corpse item is no longer available for mutation.");
                }

                return CorpseTargetAdvisor.CanLoot(
                        state,
                        corpseView,
                        corpseItem,
                        sourceSection,
                        sourceSlot,
                        container,
                        slot,
                        quantity,
                        out reason)
                    ? InventoryDropPlan.CorpseLoot(corpseItem, container, slot, quantity ?? corpseItem.Quantity)
                    : InventoryDropPlan.Rejected(reason);
            }

            if (payload.Kind == InventoryDragPayloadKind.RecoveryDelivery)
            {
                var delivery = state.RecoveryStorage?.Deliveries.FirstOrDefault(candidate =>
                    candidate.DeliveryId == payload.RecoveryDeliveryId);
                if (delivery == null)
                {
                    return InventoryDropPlan.Rejected("The Recovery delivery is no longer available.");
                }

                return InventoryTargetAdvisor.CanClaimRecovery(state, delivery, container, out reason)
                    ? InventoryDropPlan.ClaimRecovery(delivery, container)
                    : InventoryDropPlan.Rejected(reason);
            }

            if (payload.Kind != InventoryDragPayloadKind.Item
                || !state.TryFindItem(payload.ItemInstanceId, out var item, out var source))
            {
                return InventoryDropPlan.Rejected("The dragged item is no longer available.");
            }

            if (slot.Item != null)
            {
                if (slot.Item.ItemInstanceId == item.ItemInstanceId)
                {
                    return InventoryDropPlan.Rejected("The item is already in this slot.");
                }

                if (quantity != null)
                {
                    return InventoryDropPlan.Rejected("Split stacks must be dropped into an empty slot.");
                }

                var targetLocation = new InventoryItemLocation(
                    InventoryItemLocationKind.Container,
                    container.ContainerId,
                    container.ContainerType,
                    slot.SlotIndex,
                    string.Empty,
                    Guid.Empty);
                if (InventoryTargetAdvisor.CanMerge(state, item, source, slot.Item, targetLocation, out reason))
                {
                    return InventoryDropPlan.Merge(item, slot.Item);
                }

                return InventoryTargetAdvisor.CanSwap(state, item, source, slot.Item, targetLocation, out reason)
                    ? InventoryDropPlan.Swap(item, slot.Item)
                    : InventoryDropPlan.Rejected(reason);
            }

            // A split from an equipped stack unequips part of it, such as some arrows.
            if (quantity == null && source.Kind == InventoryItemLocationKind.Equipment)
            {
                return InventoryTargetAdvisor.CanUnequip(state, item, container, slot, out reason)
                    ? InventoryDropPlan.Unequip(item, container, slot)
                    : InventoryDropPlan.Rejected(reason);
            }

            if (quantity != null
                && !InventoryTargetAdvisor.CanSplit(state.Catalog, item, quantity.Value, out reason))
            {
                return InventoryDropPlan.Rejected(reason);
            }

            if (!InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    item,
                    source,
                    container,
                    slot,
                    quantity ?? item.Quantity,
                    out reason))
            {
                return InventoryDropPlan.Rejected(reason);
            }

            return quantity != null
                ? InventoryDropPlan.Split(item, quantity.Value, container, slot)
                : InventoryDropPlan.Relocate(item, container, slot);
        }

        public static InventoryDropPlan PlanEquipmentDrop(
            InventoryClientState state,
            CorpseLootView corpseView,
            bool canOperateOnCorpse,
            InventoryEquipmentSlot drawnSlot,
            InventoryDragPayload payload,
            int? quantity)
        {
            var equipmentSlot = state?.FullSnapshot?.Equipment.FirstOrDefault(candidate =>
                string.Equals(candidate.SlotId, drawnSlot.SlotId, StringComparison.Ordinal));
            if (equipmentSlot == null)
            {
                return InventoryDropPlan.Rejected("This equipment slot is no longer available.");
            }

            if (!CanMoveItem(state, null))
            {
                return InventoryDropPlan.Rejected("Inventory mutation is unavailable while state is loading.");
            }

            string reason;
            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                if (!TryFindCorpseItem(corpseView, canOperateOnCorpse, payload, out var corpseItem, out var sourceSection, out var sourceSlot))
                {
                    return InventoryDropPlan.Rejected("The corpse item is no longer available.");
                }

                // A corpse Bag swaps with the equipped Bag, or takes an empty Bag slot with its
                // contents; any other corpse item equips whole.
                if (corpseItem.HasBagContents && equipmentSlot.Item != null)
                {
                    return CorpseTargetAdvisor.CanSwapCorpseBagIntoEquipment(
                            state,
                            corpseItem,
                            sourceSection,
                            equipmentSlot,
                            out reason)
                        ? InventoryDropPlan.CorpseBagSwap(corpseItem)
                        : InventoryDropPlan.Rejected(reason);
                }

                if (quantity != null)
                {
                    return InventoryDropPlan.Rejected("Part of a stack cannot be equipped.");
                }

                return CorpseTargetAdvisor.CanLootIntoEquipment(
                        state,
                        corpseView,
                        corpseItem,
                        sourceSection,
                        sourceSlot,
                        equipmentSlot,
                        out reason)
                    ? InventoryDropPlan.CorpseLootToEquipment(corpseItem, equipmentSlot)
                    : InventoryDropPlan.Rejected(reason);
            }

            if (payload.Kind != InventoryDragPayloadKind.Item
                || !state.TryFindItem(payload.ItemInstanceId, out var item, out var source))
            {
                return InventoryDropPlan.Rejected("Only a current inventory item can be equipped.");
            }

            if (quantity != null)
            {
                return InventoryDropPlan.Rejected("Part of a stack cannot be equipped.");
            }

            // A stack of the equipped definition tops up the equipped stack, such as arrows in
            // the ammunition slot.
            if (InventoryTargetAdvisor.IsEquippedStackTopUp(state.Catalog, item, equipmentSlot.Item))
            {
                return InventoryTargetAdvisor.CanMerge(
                        state,
                        item,
                        source,
                        equipmentSlot.Item,
                        InventoryItemLocation.ForEquipment(equipmentSlot.SlotId),
                        out reason)
                    ? InventoryDropPlan.Merge(item, equipmentSlot.Item)
                    : InventoryDropPlan.Rejected(reason);
            }

            if (source.Kind == InventoryItemLocationKind.Equipment)
            {
                return InventoryDropPlan.Rejected(
                    equipmentSlot.Item != null && equipmentSlot.Item.ItemInstanceId == item.ItemInstanceId
                        ? "The item is already equipped in this slot."
                        : "Move the equipped item to an inventory slot before changing equipment slots.");
            }

            // An occupied slot swaps: the equipped item moves into the slot the new item came from.
            return InventoryTargetAdvisor.CanEquip(state, item, source, equipmentSlot, out reason)
                ? InventoryDropPlan.Equip(item, equipmentSlot)
                : InventoryDropPlan.Rejected(reason);
        }

        public static InventoryDropPlan PlanCorpseDrop(
            InventoryClientState state,
            CorpseLootView corpseView,
            bool canOperateOnCorpse,
            CorpseLootSection drawnSection,
            CorpseLootSlot drawnSlot,
            InventoryDragPayload payload,
            int? quantity)
        {
            // A view update may have replaced the drawn section, such as the Bag section after a
            // committed Bag swap, so the plan uses the newest one.
            if (!CanMoveItem(state, null)
                || corpseView == null
                || !canOperateOnCorpse
                || !corpseView.TryGetSection(drawnSection.SectionKind, out var section)
                || !section.TryGetSlot(drawnSlot.SlotIndex, out var slot))
            {
                return InventoryDropPlan.Rejected(
                    "Inventory or corpse mutation is unavailable while state is loading or busy.");
            }

            string reason;
            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                if (payload.CorpseId != corpseView.CorpseId
                    || !corpseView.TryFindItem(
                        payload.ItemInstanceId,
                        out var sourceItem,
                        out var sourceSection,
                        out var sourceSlot))
                {
                    return InventoryDropPlan.Rejected("The corpse source item is no longer available.");
                }

                return CorpseTargetAdvisor.CanMoveWithinCorpse(
                        state,
                        corpseView,
                        sourceItem,
                        sourceSection,
                        sourceSlot,
                        section,
                        slot,
                        quantity,
                        out reason)
                    ? InventoryDropPlan.CorpseMove(sourceItem, section, slot, quantity ?? sourceItem.Quantity)
                    : InventoryDropPlan.Rejected(reason);
            }

            if (payload.Kind != InventoryDragPayloadKind.Item
                || !state.TryFindItem(payload.ItemInstanceId, out var item, out var source))
            {
                return InventoryDropPlan.Rejected("Only a current carried inventory item can enter corpse custody.");
            }

            if (!CorpseTargetAdvisor.CanDeposit(state, item, source, section, slot, quantity, out reason))
            {
                return InventoryDropPlan.Rejected(reason);
            }

            return CorpseTargetAdvisor.IsBagSwap(state, item, source, section, slot)
                ? InventoryDropPlan.CorpseBagSwap(slot.Item)
                : InventoryDropPlan.CorpseDeposit(item, section, slot, quantity ?? item.Quantity);
        }

        // The drawn slot again in the current state, since a confirmation may have arrived
        // during a drag.
        private static bool TryResolveCurrentSlot(
            InventoryClientState state,
            InventoryContainer drawnContainer,
            InventorySlot drawnSlot,
            out InventoryContainer container,
            out InventorySlot slot)
        {
            slot = null;
            var snapshot = state?.FullSnapshot;
            container = snapshot == null
                ? null
                : new[]
                    {
                        snapshot.PermanentInventory,
                        snapshot.EquippedBag?.Contents,
                        state.Bank
                    }
                    .FirstOrDefault(candidate =>
                        candidate != null && candidate.ContainerId == drawnContainer.ContainerId);
            return container != null && container.TryGetSlot(drawnSlot.SlotIndex, out slot);
        }

        private static bool TryFindCorpseItem(
            CorpseLootView corpseView,
            bool canOperateOnCorpse,
            InventoryDragPayload payload,
            out CorpseLootItem corpseItem,
            out CorpseLootSection section,
            out CorpseLootSlot slot)
        {
            corpseItem = null;
            section = null;
            slot = null;
            return corpseView != null
                && canOperateOnCorpse
                && corpseView.CorpseId == payload.CorpseId
                && corpseView.TryFindItem(payload.ItemInstanceId, out corpseItem, out section, out slot);
        }
    }
}
