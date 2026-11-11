using System;
using System.Collections.Generic;
using System.Globalization;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Mmorpg.Client.Ui
{
    // What every kind of slot shows and does, and what a drop onto it sends. A stack splits
    // three ways: Shift drags half of it, Shift+right-click sends half where right-click sends
    // the stack, and Ctrl+click asks how many and carries them on the pointer. The rest stays.
    public sealed partial class InventoryWindows
    {
        // A slot of Permanent Inventory, the Bag, or the Bank.
        internal ItemSlotSpec ContainerSlot(InventoryContainer container, InventorySlot slot, string keyPrefix)
        {
            var state = State;
            var item = slot.Item;
            var specialized = !string.Equals(slot.SlotKind, BagSlotKindIds.General, StringComparison.Ordinal);
            var tagLabel = specialized
                ? InventorySlotPresentation.AcceptedTagsLabel(state.Catalog, slot.AcceptedTags)
                : string.Empty;
            var description = SlotDescription(
                InventorySlotPresentation.ContainerName(texts, state, container.ContainerType),
                slot.SlotIndex,
                specialized,
                tagLabel);
            var spec = ItemSpec(item, keyPrefix + ":" + slot.SlotIndex.ToString(CultureInfo.InvariantCulture));
            spec.Silhouette = specialized ? theme.TagSilhouette(slot.AcceptedTags) : null;
            spec.OnRightClick = item == null ? null : () => OnItemRightClicked(item, false);
            spec.DragPayload = item == null ? null : InventoryDragPayload.ForItem(item.ItemInstanceId);
            spec.CanDrag = InventoryDropPlanner.CanMoveItem(state, item);
            OfferStackParts(spec, item?.Quantity ?? 0, () => OnItemRightClicked(item, true));
            spec.DropValidator = (InventoryDragPayload payload, out ClientText reason) =>
                CanDropOnContainer(container, slot, payload, out reason);
            spec.OnDrop = payload => DropOnContainer(container, slot, payload);
            spec.Tooltip = item != null
                ? InventorySlotPresentation.BuildItemTooltip(state.Catalog, item, description)
                : specialized ? EmptySlotTooltip(description) : string.Empty;
            return spec;
        }

        // "Permanent Inventory slot 3", with the accepted item types of a specialized slot.
        private string SlotDescription(string place, int slotIndex, bool accepts, string acceptedTags)
        {
            var placeArgument = MessageArgument.Text("place", place);
            var number = MessageArgument.Text("number", (slotIndex + 1).ToString(CultureInfo.InvariantCulture));
            return accepts
                ? Text("ui/inventory/slot-number-accepts", placeArgument, number, MessageArgument.Text("tags", acceptedTags))
                : Text("ui/inventory/slot-number", placeArgument, number);
        }

        private string EmptySlotTooltip(string description)
        {
            return "<b>" + Text("ui/inventory/empty") + "</b>\n" + description;
        }

        // An equipment slot. The equipped arrows show the worker's live count, which already
        // subtracts shots that are not committed yet.
        internal ItemSlotSpec EquipmentSlot(
            InventoryEquipmentSlot equipmentSlot,
            IReadOnlyDictionary<string, InventoryEquipmentSlot> blocked)
        {
            var state = State;
            var item = equipmentSlot.Item;
            var slotName = InventorySlotPresentation.EquipmentSlotName(state.Catalog, equipmentSlot.SlotId);
            var blocker = item == null && blocked.TryGetValue(equipmentSlot.SlotId, out var found) ? found : null;
            var spec = ItemSpec(item, "equipment:" + equipmentSlot.SlotId);
            spec.Silhouette = theme.EquipmentSilhouette(equipmentSlot.SlotId);
            spec.Blocked = blocker != null;
            spec.OnRightClick = item == null ? null : () => OnItemRightClicked(item, false);
            spec.DragPayload = item == null ? null : InventoryDragPayload.ForItem(item.ItemInstanceId);
            spec.CanDrag = InventoryDropPlanner.CanMoveItem(state, item);
            OfferStackParts(spec, item?.Quantity ?? 0, () => OnItemRightClicked(item, true));
            spec.DropValidator = (InventoryDragPayload payload, out ClientText reason) =>
                CanDropOnEquipment(equipmentSlot, payload, out reason);
            spec.OnDrop = payload => Report(interactions.Execute(
                interactions.PlanEquipmentDrop(equipmentSlot, payload, payload.Quantity)));
            var description = Text("ui/inventory/equipped-in", MessageArgument.Text("slot", slotName));
            if (item != null && IsAmmunitionSlot(equipmentSlot))
            {
                var live = LiveAmmunitionQuantity(item);
                spec.QuantityText = InventorySlotPresentation.LiveQuantityText(item.Quantity, live);
                description = InventorySlotPresentation.LiveQuantityDescription(texts, description, item.Quantity, live);
            }

            spec.Tooltip = item != null
                ? InventorySlotPresentation.BuildItemTooltip(state.Catalog, item, description)
                : "<b>" + slotName + "</b>\n" + (blocker != null
                    ? InventorySlotPresentation.BlockedSlotText(state.Catalog, blocker)
                    : InventorySlotPresentation.EmptyEquipmentSlotText(texts, state.Catalog, equipmentSlot.SlotId));
            return spec;
        }

        internal static bool IsAmmunitionSlot(InventoryEquipmentSlot equipmentSlot)
        {
            return string.Equals(equipmentSlot.SlotId, ItemEquipmentSlotIds.Ammunition, StringComparison.Ordinal);
        }

        // The worker's live count while it names the equipped stack, else the committed one.
        internal int LiveAmmunitionQuantity(InventoryItem item)
        {
            return CombatLoadoutRules.DisplayedAmmunitionQuantity(
                item.ItemInstanceId,
                item.Quantity,
                combatController != null ? combatController.State.Owner : null);
        }

        internal ItemSlotSpec CorpseSlot(CorpseLootView view, CorpseLootSection section, CorpseLootSlot slot)
        {
            var state = State;
            var corpseItem = slot.Item;
            var item = corpseItem?.ToInventoryItem();
            var label = InventorySlotPresentation.CorpseSlotLabel(state.Catalog, slot);
            var sectionTitle = InventorySlotPresentation.CorpseSectionTitle(texts, section.SectionKind);
            var description = !string.IsNullOrWhiteSpace(slot.EquipmentSlotId)
                ? Text(
                    "ui/corpse/equipment-slot-description",
                    MessageArgument.Text("section", sectionTitle),
                    MessageArgument.Text("slot", label))
                : SlotDescription(sectionTitle, slot.SlotIndex, label.Length > 0, label);
            var spec = ItemSpec(item, "corpse:" + section.SectionKind + ":" + slot.SlotIndex.ToString(CultureInfo.InvariantCulture));
            spec.Silhouette = !string.IsNullOrWhiteSpace(slot.EquipmentSlotId)
                ? theme.EquipmentSilhouette(slot.EquipmentSlotId)
                : theme.TagSilhouette(slot.AcceptedTags);
            spec.OnRightClick = corpseItem == null
                ? null
                : () => Report(interactions.QuickMoveCorpseItem(corpseItem));
            spec.DragPayload = corpseItem == null
                ? null
                : InventoryDragPayload.ForCorpseItem(view.CorpseId, corpseItem.ItemInstanceId);
            spec.CanDrag = CorpseState != null
                && CorpseState.CanShowOperations
                && InventoryDropPlanner.CanMoveItem(state, item);
            // A creature's corpse gives whole items only, so its stacks offer no part.
            OfferStackParts(
                spec,
                WorldActorCorpseRules.IsMobCorpsePresentationKey(view.PresentationKey) ? 0 : corpseItem?.Quantity ?? 0,
                () => Report(interactions.QuickMoveCorpseItemPart(corpseItem)));
            spec.DropValidator = (InventoryDragPayload payload, out ClientText reason) =>
                CanDropOnCorpse(section, slot, payload, out reason);
            spec.OnDrop = payload => DropOnCorpse(section, slot, payload);
            spec.Tooltip = item != null
                ? InventorySlotPresentation.BuildItemTooltip(state.Catalog, item, description)
                : label.Length > 0 ? EmptySlotTooltip(description) : string.Empty;
            return spec;
        }

        // An item of a Recovery delivery, which stands for the whole delivery.
        internal ItemSlotSpec RecoverySlot(RecoveryDelivery delivery, RecoveryDeliveryItem deliveryItem)
        {
            var state = State;
            var item = deliveryItem.Item;
            var spec = ItemSpec(
                item,
                "recovery:" + delivery.DeliveryId + ":" + deliveryItem.ItemOrder.ToString(CultureInfo.InvariantCulture));
            spec.OnRightClick = () => Report(interactions.QuickMoveRecoveryDelivery(delivery));
            spec.DragPayload = InventoryDragPayload.ForRecoveryDelivery(delivery.DeliveryId);
            spec.CanDrag = InventoryDropPlanner.CanMoveItem(state, null);
            spec.DropValidator = (InventoryDragPayload payload, out ClientText reason) =>
            {
                reason = ClientText.Of("ui/items/recovery-storage-drop");
                return false;
            };
            spec.Tooltip = InventorySlotPresentation.BuildItemTooltip(
                state.Catalog,
                item,
                Text("ui/inventory/recovery-delivery-slot"));
            return spec;
        }

        // The look every item slot shares: icon, rarity frame, name, policy badge, search dim,
        // and rollback mark.
        internal ItemSlotSpec ItemSpec(InventoryItem item, string key)
        {
            var catalog = State?.Catalog;
            var spec = new ItemSlotSpec
            {
                Key = key,
                Item = item,
                OnRejected = Say
            };
            if (item == null)
            {
                return spec;
            }

            spec.DisplayName = catalog != null ? catalog.GetDisplayName(item.DefinitionId) : item.DefinitionId;
            spec.Icon = catalog?.LoadIcon(item.DefinitionId);
            spec.FrameColor = ItemRarityPresentation.FrameColor(theme, catalog, item.DefinitionId);
            spec.Badge = InventorySlotPresentation.PolicyBadge(item);
            spec.Dimmed = !InventorySearch.Matches(catalog, item.DefinitionId, searchQuery);
            spec.RolledBack = IsRolledBack(item);
            return spec;
        }

        // A stack of more than one item splits: Shift drags half of it, Shift+right-click sends
        // half, and Ctrl+click asks how many to carry. A slot that cannot be dragged offers none.
        private void OfferStackParts(ItemSlotSpec spec, int stack, Action halfRightClick)
        {
            if (stack < 2 || !spec.CanDrag || spec.DragPayload == null)
            {
                return;
            }

            var payload = spec.DragPayload;
            spec.HalfQuantity = InventoryQuickMoves.HalfOf(stack);
            spec.OnHalfRightClick = halfRightClick;
            spec.OnSplitClick = () => AskSplit(payload);
        }

        private bool CanDropOnContainer(
            InventoryContainer container,
            InventorySlot slot,
            InventoryDragPayload payload,
            out ClientText reason)
        {
            var plan = interactions.PlanContainerDrop(container, slot, payload, payload.Quantity);
            reason = plan.Reason;
            return plan.IsValid;
        }

        private void DropOnContainer(InventoryContainer container, InventorySlot slot, InventoryDragPayload payload)
        {
            Report(interactions.Execute(interactions.PlanContainerDrop(container, slot, payload, payload.Quantity)));
        }

        private bool CanDropOnEquipment(InventoryEquipmentSlot equipmentSlot, InventoryDragPayload payload, out ClientText reason)
        {
            var plan = interactions.PlanEquipmentDrop(equipmentSlot, payload, payload.Quantity);
            reason = plan.Reason;
            return plan.IsValid;
        }

        private bool CanDropOnCorpse(CorpseLootSection section, CorpseLootSlot slot, InventoryDragPayload payload, out ClientText reason)
        {
            var plan = interactions.PlanCorpseDrop(section, slot, payload, payload.Quantity);
            reason = plan.Reason;
            return plan.IsValid;
        }

        private void DropOnCorpse(CorpseLootSection section, CorpseLootSlot slot, InventoryDragPayload payload)
        {
            Report(interactions.Execute(interactions.PlanCorpseDrop(section, slot, payload, payload.Quantity)));
        }

        // Ctrl+click: asks how many items of the stack to take, up to all but one, then carries
        // them on the pointer until a click puts them on a slot.
        private void AskSplit(InventoryDragPayload payload)
        {
            if (!TryFindStack(payload, out var definitionId, out var stack))
            {
                Say(ClientText.Of("ui/items/item-unavailable"));
                return;
            }

            if (stack < 2)
            {
                Say(ClientText.Of("ui/items/single-item-split"));
                return;
            }

            var catalog = State.Catalog;
            dialog.AskQuantity(
                Text("ui/inventory/split-heading"),
                Text(
                    "ui/inventory/split-stack-size",
                    MessageArgument.Text("item", catalog.GetDisplayName(definitionId)),
                    MessageArgument.Text("quantity", stack.ToString(CultureInfo.InvariantCulture))),
                1,
                stack - 1,
                InventoryQuickMoves.HalfOf(stack),
                PointerPosition(),
                quantity => CarrySplit(payload, quantity));
        }

        private void CarrySplit(InventoryDragPayload payload, int quantity)
        {
            if (!TryFindStack(payload, out var definitionId, out var stack) || quantity >= stack)
            {
                Say(Text("ui/inventory/split-stack-changed"));
                return;
            }

            var catalog = State.Catalog;
            dragCoordinator.BeginCarry(
                payload.WithQuantity(quantity),
                catalog.GetDisplayName(definitionId),
                catalog.LoadIcon(definitionId),
                InventorySlotPresentation.QuantityText(quantity),
                PointerPosition());
        }

        // The definition and quantity of a carried, equipped, or corpse stack.
        private bool TryFindStack(InventoryDragPayload payload, out string definitionId, out int stack)
        {
            definitionId = string.Empty;
            stack = 0;
            if (payload == null || State?.Catalog == null)
            {
                return false;
            }

            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                var view = CorpseState?.ShownView;
                if (view == null || !view.TryFindItem(payload.ItemInstanceId, out var corpseItem, out _, out _))
                {
                    return false;
                }

                definitionId = corpseItem.DefinitionId;
                stack = corpseItem.Quantity;
                return true;
            }

            if (payload.Kind != InventoryDragPayloadKind.Item
                || !State.TryFindItem(payload.ItemInstanceId, out var item, out _))
            {
                return false;
            }

            definitionId = item.DefinitionId;
            stack = item.Quantity;
            return true;
        }

        // Dropped outside every window, a carried or equipped item is offered for destruction.
        // Destruction takes the whole stack, so a dragged half is not offered.
        private void OnDroppedOutside(InventoryDragPayload payload)
        {
            if (payload.Kind != InventoryDragPayloadKind.Item
                || State == null
                || !State.TryFindItem(payload.ItemInstanceId, out var item, out _))
            {
                return;
            }

            if (payload.IsPartial)
            {
                Say(Text("ui/inventory/destroy-whole-stack"));
                return;
            }

            if (!interactions.CanDestroy(item, out var reason))
            {
                Say(reason);
                return;
            }

            var name = State.Catalog.GetDisplayName(item.DefinitionId)
                + (item.Quantity > 1 ? " x" + item.Quantity.ToString(CultureInfo.InvariantCulture) : string.Empty);
            dialog.AskConfirmation(
                Text("ui/inventory/destroy-heading"),
                Text("ui/inventory/destroy-question", MessageArgument.Text("item", name)),
                Text("ui/inventory/destroy"),
                () => Report(interactions.Destroy(item)));
        }

        // Right-clicking a carried or equipped item. An open vendor takes a carried item into
        // its Sell slot, and an open Bank takes the item. With Shift, half the stack, rounded
        // down, goes the same way and the rest stays.
        private void OnItemRightClicked(InventoryItem item, bool half)
        {
            var state = State;
            if (!InventoryDropPlanner.CanMoveItem(state, item))
            {
                Say(state.IsPredictedItem(item.ItemInstanceId)
                    ? ClientText.Of("ui/items/wait-for-stack")
                    : ClientText.Of("ui/items/inventory-loading"));
                return;
            }

            if (IsStorageOpen
                && storageContext == InventoryContextKind.Vendor
                && state.TryFindItem(item.ItemInstanceId, out _, out var location)
                && location.Kind == InventoryItemLocationKind.Container
                && ItemContainerTypeIds.IsCarried(location.ContainerType))
            {
                var payload = InventoryDragPayload.ForItem(item.ItemInstanceId);
                storage.OfferForSale(half ? payload.WithQuantity(InventoryQuickMoves.HalfOf(item.Quantity)) : payload);
                return;
            }

            Report(half
                ? interactions.QuickMoveItemPart(item, IsBankShown())
                : interactions.QuickMoveItem(item, IsBankShown()));
        }

        // The Bank takes right-clicked items only while its tab shows.
        private bool IsBankShown()
        {
            return IsStorageOpen && storageContext == InventoryContextKind.Bank && State?.Bank != null;
        }

        private static Vector2 PointerPosition()
        {
            var mouse = Mouse.current;
            return mouse != null
                ? mouse.position.ReadValue()
                : new Vector2(Screen.width / 2f, Screen.height / 2f);
        }
    }
}
