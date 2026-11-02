using System;
using System.Globalization;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Ui
{
    // Whether an interaction sent an operation, and the message for the player: what was sent,
    // or why nothing was.
    public readonly struct InventoryInteractionResult
    {
        private InventoryInteractionResult(bool sent, string message)
        {
            Sent = sent;
            Message = message ?? string.Empty;
        }

        public bool Sent { get; }

        public string Message { get; }

        public static InventoryInteractionResult Submitted(string message)
        {
            return new InventoryInteractionResult(true, message);
        }

        public static InventoryInteractionResult Rejected(string reason)
        {
            return new InventoryInteractionResult(
                false,
                string.IsNullOrWhiteSpace(reason) ? "This inventory destination is not valid." : reason);
        }

        public static InventoryInteractionResult From(bool sent, string error, string success)
        {
            return sent ? Submitted(success) : Rejected(error);
        }
    }

    // Sends the item operations of the inventory windows: drops that InventoryDropPlanner
    // decided, right-click quick moves, Loot all, destruction, corpse closing, and vendor
    // trades. The controllers predict and send each operation; nothing here draws, and the
    // windows only choose what to ask for and show the result.
    public sealed class InventoryInteractions
    {
        private readonly InventoryClientController inventory;
        private readonly CorpseClientController corpses;
        private readonly WorldInteractionClientController worldInteraction;

        public InventoryInteractions(
            InventoryClientController inventory,
            CorpseClientController corpses,
            WorldInteractionClientController worldInteraction)
        {
            this.inventory = inventory;
            this.corpses = corpses;
            this.worldInteraction = worldInteraction;
        }

        public InventoryClientState State
        {
            get { return inventory != null ? inventory.State : null; }
        }

        // The corpse view the windows show, or null.
        public CorpseLootView CorpseView
        {
            get { return corpses != null ? corpses.State.ShownView : null; }
        }

        public bool CanOperateOnCorpse
        {
            get { return corpses != null && corpses.State.CanShowOperations; }
        }

        public InventoryDropPlan PlanContainerDrop(
            InventoryContainer container,
            InventorySlot slot,
            InventoryDragPayload payload,
            int? quantity)
        {
            return InventoryDropPlanner.PlanContainerDrop(
                State,
                CorpseView,
                CanOperateOnCorpse,
                container,
                slot,
                payload,
                quantity);
        }

        public InventoryDropPlan PlanEquipmentDrop(
            InventoryEquipmentSlot equipmentSlot,
            InventoryDragPayload payload,
            int? quantity)
        {
            return InventoryDropPlanner.PlanEquipmentDrop(
                State,
                CorpseView,
                CanOperateOnCorpse,
                equipmentSlot,
                payload,
                quantity);
        }

        public InventoryDropPlan PlanCorpseDrop(
            CorpseLootSection section,
            CorpseLootSlot slot,
            InventoryDragPayload payload,
            int? quantity)
        {
            return InventoryDropPlanner.PlanCorpseDrop(
                State,
                CorpseView,
                CanOperateOnCorpse,
                section,
                slot,
                payload,
                quantity);
        }

        public InventoryInteractionResult Execute(InventoryDropPlan plan)
        {
            if (plan == null || !plan.IsValid)
            {
                return InventoryInteractionResult.Rejected(plan?.Reason);
            }

            string error;
            switch (plan.Kind)
            {
                case InventoryDropPlanKind.Relocate:
                    return InventoryInteractionResult.From(
                        inventory.TryRelocate(plan.Item, plan.Container.ContainerId, plan.Slot.SlotIndex, out error),
                        error,
                        "Move submitted.");
                case InventoryDropPlanKind.Split:
                    return InventoryInteractionResult.From(
                        inventory.TrySplit(plan.Item, plan.Quantity, plan.Container.ContainerId, plan.Slot.SlotIndex, out error),
                        error,
                        "Split submitted.");
                case InventoryDropPlanKind.Unequip:
                    return InventoryInteractionResult.From(
                        inventory.TryUnequip(plan.Item, plan.Container.ContainerId, plan.Slot.SlotIndex, out error),
                        error,
                        "Move submitted.");
                case InventoryDropPlanKind.Merge:
                    return InventoryInteractionResult.From(
                        inventory.TryMerge(plan.Item, plan.Target, out error),
                        error,
                        "Merge submitted.");
                case InventoryDropPlanKind.Swap:
                    return InventoryInteractionResult.From(
                        inventory.TrySwap(plan.Item, plan.Target, out error),
                        error,
                        "Swap submitted.");
                case InventoryDropPlanKind.Equip:
                    return InventoryInteractionResult.From(
                        inventory.TryEquip(plan.Item, plan.EquipmentSlotId, out error),
                        error,
                        plan.Target != null ? "Swap submitted." : "Equip submitted.");
                case InventoryDropPlanKind.ClaimRecovery:
                    return ClaimRecovery(plan.Delivery, plan.Container.ContainerId);
                case InventoryDropPlanKind.CorpseLoot:
                    return InventoryInteractionResult.From(
                        corpses.TryLoot(plan.CorpseItem, plan.Container, plan.Slot, plan.Quantity, out error),
                        error,
                        plan.Quantity < plan.CorpseItem.Quantity
                            ? "Partial corpse loot submitted."
                            : "Corpse loot submitted.");
                case InventoryDropPlanKind.CorpseLootToEquipment:
                    return InventoryInteractionResult.From(
                        corpses.TryLootToEquipment(plan.CorpseItem, plan.EquipmentSlot, out error),
                        error,
                        plan.EquipmentSlot.Item == null ? "Corpse equip submitted." : "Corpse equip swap submitted.");
                case InventoryDropPlanKind.CorpseBagSwap:
                    return InventoryInteractionResult.From(
                        corpses.TrySwapBag(plan.CorpseItem, out error),
                        error,
                        "Atomic Bag swap submitted.");
                case InventoryDropPlanKind.CorpseMove:
                    return InventoryInteractionResult.From(
                        corpses.TryMoveWithinCorpse(plan.CorpseItem, plan.CorpseSection, plan.CorpseSlot, plan.Quantity, out error),
                        error,
                        plan.Quantity < plan.CorpseItem.Quantity
                            ? "Partial corpse rearrangement submitted."
                            : "Corpse rearrangement submitted.");
                case InventoryDropPlanKind.CorpseDeposit:
                    return InventoryInteractionResult.From(
                        corpses.TryDeposit(plan.Item, plan.CorpseSection, plan.CorpseSlot, plan.Quantity, out error),
                        error,
                        plan.Quantity < plan.Item.Quantity
                            ? "Partial corpse deposit submitted."
                            : "Corpse deposit submitted.");
                default:
                    return InventoryInteractionResult.Rejected(plan.Reason);
            }
        }

        // A right-click move that InventoryQuickMoves decided.
        public InventoryInteractionResult RunQuickMove(InventoryQuickMove move)
        {
            string error;
            switch (move.Kind)
            {
                case InventoryQuickMoveKind.Equip:
                    // The items that block the slot leave first; if one cannot, nothing is equipped.
                    foreach (var unequip in move.Before)
                    {
                        if (!inventory.TryUnequip(unequip.Item, unequip.Container.ContainerId, unequip.Slot.SlotIndex, out error))
                        {
                            return InventoryInteractionResult.Rejected(error);
                        }
                    }

                    return InventoryInteractionResult.From(
                        inventory.TryEquip(move.Item, move.EquipmentSlotId, out error),
                        error,
                        "Equip submitted.");
                case InventoryQuickMoveKind.Unequip:
                    return InventoryInteractionResult.From(
                        inventory.TryUnequip(move.Item, move.Container.ContainerId, move.Slot.SlotIndex, out error),
                        error,
                        "Move submitted.");
                case InventoryQuickMoveKind.Relocate:
                    return InventoryInteractionResult.From(
                        inventory.TryRelocate(move.Item, move.Container.ContainerId, move.Slot.SlotIndex, out error),
                        error,
                        "Move submitted.");
                case InventoryQuickMoveKind.Merge:
                    return InventoryInteractionResult.From(
                        inventory.TryMerge(move.Item, move.Target, out error),
                        error,
                        "Merge submitted.");
                case InventoryQuickMoveKind.Split:
                    return InventoryInteractionResult.From(
                        inventory.TrySplit(move.Item, move.Quantity, move.Container.ContainerId, move.Slot.SlotIndex, out error),
                        error,
                        "Split submitted.");
                case InventoryQuickMoveKind.ClaimRecovery:
                    return ClaimRecovery(move.Delivery, move.Container.ContainerId);
                case InventoryQuickMoveKind.Loot:
                    return InventoryInteractionResult.From(
                        corpses.TryLoot(
                            move.CorpseItem,
                            move.Container,
                            move.Slot,
                            move.Quantity > 0 ? move.Quantity : move.CorpseItem.Quantity,
                            out error),
                        error,
                        move.Quantity > 0 ? "Partial corpse loot submitted." : "Corpse loot submitted.");
                case InventoryQuickMoveKind.LootToEquipment:
                {
                    var equipmentSlot = State?.FullSnapshot?.Equipment.FirstOrDefault(slot =>
                        string.Equals(slot.SlotId, move.EquipmentSlotId, StringComparison.Ordinal));
                    error = string.Empty;
                    return InventoryInteractionResult.From(
                        equipmentSlot != null
                            && corpses.TryLootToEquipment(move.CorpseItem, equipmentSlot, out error),
                        string.IsNullOrEmpty(error) ? "The equipment slot is unavailable." : error,
                        "Corpse equip submitted.");
                }

                default:
                    return InventoryInteractionResult.Rejected(move.Reason);
            }
        }

        // Right-clicking a carried or equipped item; an open Bank takes it.
        public InventoryInteractionResult QuickMoveItem(InventoryItem item, bool bankShown)
        {
            var state = State;
            if (!InventoryDropPlanner.CanMoveItem(state, item))
            {
                return InventoryInteractionResult.Rejected(
                    state != null && state.IsPredictedItem(item.ItemInstanceId)
                        ? "Wait a moment until the stack is confirmed."
                        : "Inventory mutation is unavailable while state is loading.");
            }

            return RunQuickMove(InventoryQuickMoves.ForItem(state, item, bankShown));
        }

        // Shift+right-clicking a carried or equipped stack sends half of it, rounded down, where
        // right-click sends the stack.
        public InventoryInteractionResult QuickMoveItemPart(InventoryItem item, bool bankShown)
        {
            var state = State;
            if (!InventoryDropPlanner.CanMoveItem(state, item))
            {
                return InventoryInteractionResult.Rejected(
                    state != null && state.IsPredictedItem(item.ItemInstanceId)
                        ? "Wait a moment until the stack is confirmed."
                        : "Inventory mutation is unavailable while state is loading.");
            }

            return state.TryFindItem(item.ItemInstanceId, out var current, out _)
                ? RunQuickMove(InventoryQuickMoves.ForItemPart(state, current, InventoryQuickMoves.HalfOf(current.Quantity), bankShown))
                : InventoryInteractionResult.Rejected("The item is no longer available.");
        }

        public InventoryInteractionResult QuickMoveCorpseItem(CorpseLootItem item)
        {
            if (!CanOperateOnCorpse || !InventoryDropPlanner.CanMoveItem(State, null))
            {
                return InventoryInteractionResult.Rejected(
                    "Inventory or corpse mutation is unavailable while state is loading or busy.");
            }

            return RunQuickMove(InventoryQuickMoves.ForCorpseItem(State, CorpseView, item));
        }

        // Shift+right-clicking a corpse stack loots half of it, rounded down.
        public InventoryInteractionResult QuickMoveCorpseItemPart(CorpseLootItem item)
        {
            if (!CanOperateOnCorpse || !InventoryDropPlanner.CanMoveItem(State, null))
            {
                return InventoryInteractionResult.Rejected(
                    "Inventory or corpse mutation is unavailable while state is loading or busy.");
            }

            var view = CorpseView;
            return view != null && item != null && view.TryFindItem(item.ItemInstanceId, out var current, out _, out _)
                ? RunQuickMove(InventoryQuickMoves.ForCorpseItemPart(State, view, current, InventoryQuickMoves.HalfOf(current.Quantity)))
                : InventoryInteractionResult.Rejected("The corpse item is no longer available.");
        }

        public InventoryInteractionResult QuickMoveRecoveryDelivery(RecoveryDelivery delivery)
        {
            return RunQuickMove(InventoryQuickMoves.ForRecoveryDelivery(State, delivery));
        }

        public InventoryInteractionResult LootAll()
        {
            return InventoryInteractionResult.From(
                corpses.TryLootAll(out var error),
                error,
                "Loot all submitted.");
        }

        public bool CanDestroy(InventoryItem item, out string reason)
        {
            reason = string.Empty;
            var state = State;
            if (item == null || state?.Catalog == null)
            {
                reason = "The item is no longer available.";
                return false;
            }

            if (!InventoryTargetAdvisor.CanDestroy(state.Catalog, item, out reason))
            {
                return false;
            }

            if (!InventoryDropPlanner.CanMoveItem(state, item))
            {
                reason = "Wait a moment until the item is confirmed.";
                return false;
            }

            return true;
        }

        public InventoryInteractionResult Destroy(InventoryItem item)
        {
            if (!CanDestroy(item, out var reason))
            {
                return InventoryInteractionResult.Rejected(reason);
            }

            return InventoryInteractionResult.From(
                inventory.TryDestroy(item, out var error),
                error,
                "Destroy submitted.");
        }

        public InventoryInteractionResult CloseCorpse()
        {
            var error = "Corpse interaction state is unavailable.";
            return InventoryInteractionResult.From(
                worldInteraction != null && worldInteraction.TryCloseActive(out error),
                error,
                "Corpse close submitted.");
        }

        public InventoryInteractionResult RefreshCorpse()
        {
            var error = "Corpse interaction state is unavailable.";
            return InventoryInteractionResult.From(
                corpses != null && corpses.TryRefresh(out error),
                error,
                "Corpse refresh submitted.");
        }

        // Whether a dragged item may be offered to the vendor, whole or the dragged part.
        public bool CanOfferToVendor(
            VendorProfileDefinition vendor,
            InventoryDragPayload payload,
            out string reason)
        {
            reason = string.Empty;
            var state = State;
            if (!InventoryDropPlanner.CanMoveItem(state, null))
            {
                reason = "Inventory mutation is unavailable while state is loading.";
                return false;
            }

            if (payload.Kind != InventoryDragPayloadKind.Item
                || !state.TryFindItem(payload.ItemInstanceId, out var item, out var location))
            {
                reason = "Only a carried item can be sold.";
                return false;
            }

            return VendorClientRules.CanSell(
                state.Catalog,
                vendor,
                state.FullSnapshot,
                item,
                location,
                payload.Quantity ?? item.Quantity,
                out _,
                out reason);
        }

        public InventoryInteractionResult Buy(
            RealtimeWorldActorCapability capability,
            VendorProfileDefinition vendor,
            string definitionId,
            int quantity)
        {
            var state = State;
            if (!VendorClientRules.CanBuy(
                    state.Catalog,
                    vendor,
                    state.FullSnapshot,
                    definitionId,
                    quantity,
                    out var price,
                    out var reason))
            {
                return InventoryInteractionResult.Rejected(reason);
            }

            return InventoryInteractionResult.From(
                worldInteraction.TryPurchase(capability.Id, definitionId, quantity, price, out var error),
                error,
                "Buying " + quantity.ToString(CultureInfo.InvariantCulture) + " "
                    + state.Catalog.GetDisplayName(definitionId) + "...");
        }

        public InventoryInteractionResult Sell(
            RealtimeWorldActorCapability capability,
            VendorProfileDefinition vendor,
            Guid itemInstanceId,
            int quantity)
        {
            var state = State;
            if (!state.TryFindItem(itemInstanceId, out var item, out var location))
            {
                return InventoryInteractionResult.Rejected("The item is no longer available.");
            }

            if (!VendorClientRules.CanSell(
                    state.Catalog,
                    vendor,
                    state.FullSnapshot,
                    item,
                    location,
                    quantity,
                    out var price,
                    out var reason))
            {
                return InventoryInteractionResult.Rejected(reason);
            }

            return InventoryInteractionResult.From(
                worldInteraction.TrySell(capability.Id, item.ItemInstanceId, quantity, price, out var error),
                error,
                "Selling " + quantity.ToString(CultureInfo.InvariantCulture) + " "
                    + state.Catalog.GetDisplayName(item.DefinitionId) + "...");
        }

        private InventoryInteractionResult ClaimRecovery(RecoveryDelivery delivery, Guid destinationContainerId)
        {
            return InventoryInteractionResult.From(
                inventory.TryClaimRecovery(delivery, destinationContainerId, out var error),
                error,
                "Recovery claim submitted.");
        }
    }
}
