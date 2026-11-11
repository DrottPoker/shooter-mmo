using System;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Ui
{
    // Whether an interaction sent an operation, and why nothing was sent for the player to read.
    // A sent operation says nothing; its prediction shows at once.
    public readonly struct InventoryInteractionResult
    {
        private InventoryInteractionResult(bool sent, ClientText message)
        {
            Sent = sent;
            Message = message;
        }

        public bool Sent { get; }

        public ClientText Message { get; }

        public static InventoryInteractionResult Submitted()
        {
            return new InventoryInteractionResult(true, ClientText.None);
        }

        public static InventoryInteractionResult Rejected(ClientText reason)
        {
            return new InventoryInteractionResult(
                false,
                reason.IsEmpty ? ClientText.Of("ui/items/destination-invalid") : reason);
        }

        // A controller error, which is a finished sentence.
        public static InventoryInteractionResult Rejected(string error)
        {
            return Rejected(string.IsNullOrWhiteSpace(error) ? ClientText.None : ClientText.Literal(error));
        }

        public static InventoryInteractionResult From(bool sent, string error)
        {
            return sent ? Submitted() : Rejected(error);
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
                return InventoryInteractionResult.Rejected(plan != null ? plan.Reason : ClientText.None);
            }

            string error;
            switch (plan.Kind)
            {
                case InventoryDropPlanKind.Relocate:
                    return InventoryInteractionResult.From(
                        inventory.TryRelocate(plan.Item, plan.Container.ContainerId, plan.Slot.SlotIndex, out error),
                        error);
                case InventoryDropPlanKind.Split:
                    return InventoryInteractionResult.From(
                        inventory.TrySplit(plan.Item, plan.Quantity, plan.Container.ContainerId, plan.Slot.SlotIndex, out error),
                        error);
                case InventoryDropPlanKind.Unequip:
                    return InventoryInteractionResult.From(
                        inventory.TryUnequip(plan.Item, plan.Container.ContainerId, plan.Slot.SlotIndex, out error),
                        error);
                case InventoryDropPlanKind.Merge:
                    return InventoryInteractionResult.From(
                        inventory.TryMerge(plan.Item, plan.Target, plan.Quantity, out error),
                        error);
                case InventoryDropPlanKind.Swap:
                    return InventoryInteractionResult.From(
                        inventory.TrySwap(plan.Item, plan.Target, out error),
                        error);
                case InventoryDropPlanKind.Equip:
                    return InventoryInteractionResult.From(
                        inventory.TryEquip(plan.Item, plan.EquipmentSlotId, plan.Quantity, out error),
                        error);
                case InventoryDropPlanKind.ClaimRecovery:
                    return ClaimRecovery(plan.Delivery, plan.Container.ContainerId);
                case InventoryDropPlanKind.CorpseLoot:
                    return InventoryInteractionResult.From(
                        corpses.TryLoot(plan.CorpseItem, plan.Container, plan.Slot, plan.Quantity, out error),
                        error);
                case InventoryDropPlanKind.CorpseLootToEquipment:
                    return InventoryInteractionResult.From(
                        corpses.TryLootToEquipment(plan.CorpseItem, plan.EquipmentSlot, plan.Quantity, out error),
                        error);
                case InventoryDropPlanKind.CorpseBagSwap:
                    return InventoryInteractionResult.From(
                        corpses.TrySwapBag(plan.CorpseItem, out error),
                        error);
                case InventoryDropPlanKind.CorpseMove:
                    return InventoryInteractionResult.From(
                        corpses.TryMoveWithinCorpse(plan.CorpseItem, plan.CorpseSection, plan.CorpseSlot, plan.Quantity, out error),
                        error);
                case InventoryDropPlanKind.CorpseDeposit:
                    return InventoryInteractionResult.From(
                        corpses.TryDeposit(plan.Item, plan.CorpseSection, plan.CorpseSlot, plan.Quantity, out error),
                        error);
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
                        inventory.TryEquip(move.Item, move.EquipmentSlotId, move.Quantity, out error),
                        error);
                case InventoryQuickMoveKind.Unequip:
                    return InventoryInteractionResult.From(
                        inventory.TryUnequip(move.Item, move.Container.ContainerId, move.Slot.SlotIndex, out error),
                        error);
                case InventoryQuickMoveKind.Relocate:
                    return InventoryInteractionResult.From(
                        inventory.TryRelocate(move.Item, move.Container.ContainerId, move.Slot.SlotIndex, out error),
                        error);
                case InventoryQuickMoveKind.Merge:
                    return InventoryInteractionResult.From(
                        inventory.TryMerge(move.Item, move.Target, move.Quantity, out error),
                        error);
                case InventoryQuickMoveKind.Split:
                    return InventoryInteractionResult.From(
                        inventory.TrySplit(move.Item, move.Quantity, move.Container.ContainerId, move.Slot.SlotIndex, out error),
                        error);
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
                        error);
                case InventoryQuickMoveKind.LootToEquipment:
                {
                    var equipmentSlot = State?.FullSnapshot?.Equipment.FirstOrDefault(slot =>
                        string.Equals(slot.SlotId, move.EquipmentSlotId, StringComparison.Ordinal));
                    error = string.Empty;
                    if (equipmentSlot != null
                        && corpses.TryLootToEquipment(move.CorpseItem, equipmentSlot, move.Quantity, out error))
                    {
                        return InventoryInteractionResult.Submitted();
                    }

                    return string.IsNullOrEmpty(error)
                        ? InventoryInteractionResult.Rejected(ClientText.Of("ui/items/equipment-slot-unavailable"))
                        : InventoryInteractionResult.Rejected(error);
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
                        ? ClientText.Of("ui/items/wait-for-stack")
                        : ClientText.Of("ui/items/inventory-loading"));
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
                        ? ClientText.Of("ui/items/wait-for-stack")
                        : ClientText.Of("ui/items/inventory-loading"));
            }

            return state.TryFindItem(item.ItemInstanceId, out var current, out _)
                ? RunQuickMove(InventoryQuickMoves.ForItemPart(state, current, InventoryQuickMoves.HalfOf(current.Quantity), bankShown))
                : InventoryInteractionResult.Rejected(ClientText.Of("ui/items/item-unavailable"));
        }

        public InventoryInteractionResult QuickMoveCorpseItem(CorpseLootItem item)
        {
            if (!CanOperateOnCorpse || !InventoryDropPlanner.CanMoveItem(State, null))
            {
                return InventoryInteractionResult.Rejected(
                    ClientText.Of("ui/corpse/mutation-unavailable"));
            }

            return RunQuickMove(InventoryQuickMoves.ForCorpseItem(State, CorpseView, item));
        }

        // Shift+right-clicking a corpse stack loots half of it, rounded down.
        public InventoryInteractionResult QuickMoveCorpseItemPart(CorpseLootItem item)
        {
            if (!CanOperateOnCorpse || !InventoryDropPlanner.CanMoveItem(State, null))
            {
                return InventoryInteractionResult.Rejected(
                    ClientText.Of("ui/corpse/mutation-unavailable"));
            }

            var view = CorpseView;
            return view != null && item != null && view.TryFindItem(item.ItemInstanceId, out var current, out _, out _)
                ? RunQuickMove(InventoryQuickMoves.ForCorpseItemPart(State, view, current, InventoryQuickMoves.HalfOf(current.Quantity)))
                : InventoryInteractionResult.Rejected(ClientText.Of("ui/corpse/item-unavailable"));
        }

        public InventoryInteractionResult QuickMoveRecoveryDelivery(RecoveryDelivery delivery)
        {
            return RunQuickMove(InventoryQuickMoves.ForRecoveryDelivery(State, delivery));
        }

        public InventoryInteractionResult LootAll()
        {
            return InventoryInteractionResult.From(
                corpses.TryLootAll(out var error),
                error);
        }

        public bool CanDestroy(InventoryItem item, out ClientText reason)
        {
            reason = ClientText.None;
            var state = State;
            if (item == null || state?.Catalog == null)
            {
                reason = ClientText.Of("ui/items/item-unavailable");
                return false;
            }

            if (!InventoryTargetAdvisor.CanDestroy(state.Catalog, item, out reason))
            {
                return false;
            }

            if (!InventoryDropPlanner.CanMoveItem(state, item))
            {
                reason = ClientText.Of("ui/items/wait-for-item");
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
                error);
        }

        public InventoryInteractionResult CloseCorpse()
        {
            if (worldInteraction == null)
            {
                return InventoryInteractionResult.Rejected(ClientText.Of("ui/corpse/state-unavailable"));
            }

            return InventoryInteractionResult.From(worldInteraction.TryCloseActive(out var error), error);
        }

        public InventoryInteractionResult RefreshCorpse()
        {
            if (corpses == null)
            {
                return InventoryInteractionResult.Rejected(ClientText.Of("ui/corpse/state-unavailable"));
            }

            return InventoryInteractionResult.From(corpses.TryRefresh(out var error), error);
        }

        // Whether a dragged item may be offered to the vendor, whole or the dragged part.
        public bool CanOfferToVendor(
            VendorProfileDefinition vendor,
            InventoryDragPayload payload,
            out ClientText reason)
        {
            reason = ClientText.None;
            var state = State;
            if (!InventoryDropPlanner.CanMoveItem(state, null))
            {
                reason = ClientText.Of("ui/items/inventory-loading");
                return false;
            }

            if (payload.Kind != InventoryDragPayloadKind.Item
                || !state.TryFindItem(payload.ItemInstanceId, out var item, out var location))
            {
                reason = ClientText.Of("ui/vendor/sell-needs-carried-item");
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
                error);
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
                return InventoryInteractionResult.Rejected(ClientText.Of("ui/items/item-unavailable"));
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
                error);
        }

        private InventoryInteractionResult ClaimRecovery(RecoveryDelivery delivery, Guid destinationContainerId)
        {
            return InventoryInteractionResult.From(
                inventory.TryClaimRecovery(delivery, destinationContainerId, out var error),
                error);
        }
    }
}
