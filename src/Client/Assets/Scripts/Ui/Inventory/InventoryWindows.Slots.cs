using System;
using System.Collections.Generic;
using System.Globalization;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Items;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Mmorpg.Client.Ui
{
    // What every kind of slot shows and does, and what a drop onto it sends. A drag with Shift
    // carries part of a stack: the drop asks how many before it sends anything.
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
            var description = InventorySlotPresentation.ContainerName(state, container.ContainerType)
                + " slot " + (slot.SlotIndex + 1).ToString(CultureInfo.InvariantCulture)
                + (specialized ? ", accepts " + tagLabel : string.Empty);
            var spec = ItemSpec(item, keyPrefix + ":" + slot.SlotIndex.ToString(CultureInfo.InvariantCulture));
            spec.Silhouette = specialized ? theme.TagSilhouette(slot.AcceptedTags) : null;
            spec.OnRightClick = item == null ? null : () => OnItemRightClicked(item);
            spec.DragPayload = item == null ? null : InventoryDragPayload.ForItem(item.ItemInstanceId);
            spec.CanDrag = InventoryDropPlanner.CanMoveItem(state, item);
            spec.AllowPartial = item != null && item.Quantity > 1;
            spec.DropValidator = (InventoryDragPayload payload, out string reason) =>
                CanDropOnContainer(container, slot, payload, out reason);
            spec.OnDrop = payload => DropOnContainer(container, slot, payload);
            spec.Tooltip = item != null
                ? InventorySlotPresentation.BuildItemTooltip(state.Catalog, item, description)
                : specialized ? "<b>Empty</b>\n" + description : string.Empty;
            return spec;
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
            spec.OnRightClick = item == null ? null : () => OnItemRightClicked(item);
            spec.DragPayload = item == null ? null : InventoryDragPayload.ForItem(item.ItemInstanceId);
            spec.CanDrag = InventoryDropPlanner.CanMoveItem(state, item);
            spec.AllowPartial = item != null && item.Quantity > 1;
            spec.DropValidator = (InventoryDragPayload payload, out string reason) =>
                CanDropOnEquipment(equipmentSlot, payload, out reason);
            spec.OnDrop = payload => Report(interactions.Execute(
                interactions.PlanEquipmentDrop(equipmentSlot, payload, null)));
            var description = "Equipped in " + slotName;
            if (item != null && IsAmmunitionSlot(equipmentSlot))
            {
                var live = LiveAmmunitionQuantity(item);
                spec.QuantityText = InventorySlotPresentation.LiveQuantityText(item.Quantity, live);
                description = InventorySlotPresentation.LiveQuantityDescription(description, item.Quantity, live);
            }

            spec.Tooltip = item != null
                ? InventorySlotPresentation.BuildItemTooltip(state.Catalog, item, description)
                : "<b>" + slotName + "</b>\n" + (blocker != null
                    ? InventorySlotPresentation.BlockedSlotText(state.Catalog, blocker)
                    : InventorySlotPresentation.EmptyEquipmentSlotText(state.Catalog, equipmentSlot.SlotId));
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
            var description = InventorySlotPresentation.CorpseSectionTitle(section.SectionKind)
                + (!string.IsNullOrWhiteSpace(slot.EquipmentSlotId)
                    ? ", " + label
                    : " slot " + (slot.SlotIndex + 1).ToString(CultureInfo.InvariantCulture)
                        + (label.Length > 0 ? ", accepts " + label : string.Empty));
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
            spec.AllowPartial = corpseItem != null && corpseItem.Quantity > 1;
            spec.DropValidator = (InventoryDragPayload payload, out string reason) =>
                CanDropOnCorpse(section, slot, payload, out reason);
            spec.OnDrop = payload => DropOnCorpse(section, slot, payload);
            spec.Tooltip = item != null
                ? InventorySlotPresentation.BuildItemTooltip(state.Catalog, item, description)
                : label.Length > 0 ? "<b>Empty</b>\n" + description : string.Empty;
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
            spec.DropValidator = (InventoryDragPayload payload, out string reason) =>
            {
                reason = "Recovery Storage accepts system deliveries only. "
                    + "Drag a Recovery delivery to your inventory or the Bank to claim it.";
                return false;
            };
            spec.Tooltip = InventorySlotPresentation.BuildItemTooltip(
                state.Catalog,
                item,
                "Recovery delivery, claimed as a whole");
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

        private bool CanDropOnContainer(
            InventoryContainer container,
            InventorySlot slot,
            InventoryDragPayload payload,
            out string reason)
        {
            var plan = interactions.PlanContainerDrop(container, slot, payload, ProbeQuantity(payload));
            reason = plan.Reason;
            return plan.IsValid;
        }

        private void DropOnContainer(InventoryContainer container, InventorySlot slot, InventoryDragPayload payload)
        {
            if (IsPartialDrop(payload))
            {
                AskPartialQuantity(payload, false, quantity => Report(interactions.Execute(
                    interactions.PlanContainerDrop(container, slot, payload, quantity))));
                return;
            }

            Report(interactions.Execute(interactions.PlanContainerDrop(container, slot, payload, null)));
        }

        private bool CanDropOnEquipment(InventoryEquipmentSlot equipmentSlot, InventoryDragPayload payload, out string reason)
        {
            var plan = interactions.PlanEquipmentDrop(equipmentSlot, payload, ProbeQuantity(payload));
            reason = plan.Reason;
            return plan.IsValid;
        }

        private bool CanDropOnCorpse(CorpseLootSection section, CorpseLootSlot slot, InventoryDragPayload payload, out string reason)
        {
            var plan = interactions.PlanCorpseDrop(section, slot, payload, ProbeQuantity(payload));
            reason = plan.Reason;
            return plan.IsValid;
        }

        private void DropOnCorpse(CorpseLootSection section, CorpseLootSlot slot, InventoryDragPayload payload)
        {
            if (IsPartialDrop(payload))
            {
                AskPartialQuantity(payload, true, quantity => Report(interactions.Execute(
                    interactions.PlanCorpseDrop(section, slot, payload, quantity))));
                return;
            }

            Report(interactions.Execute(interactions.PlanCorpseDrop(section, slot, payload, null)));
        }

        // A partial drag is judged as if it moved one item, the least a part can be.
        private static int? ProbeQuantity(InventoryDragPayload payload)
        {
            return IsPartialDrop(payload) ? 1 : (int?)null;
        }

        private static bool IsPartialDrop(InventoryDragPayload payload)
        {
            return payload.IsPartial && payload.Kind != InventoryDragPayloadKind.RecoveryDelivery;
        }

        // Asks how many items of the dragged stack to move: up to all but one for a split, or
        // up to the whole stack for a corpse item or a deposit into the corpse.
        private void AskPartialQuantity(InventoryDragPayload payload, bool corpseDestination, Action<int> chosen)
        {
            string definitionId;
            int stack;
            var corpse = payload.Kind == InventoryDragPayloadKind.CorpseItem;
            if (corpse)
            {
                var view = CorpseState?.ShownView;
                if (view == null || !view.TryFindItem(payload.ItemInstanceId, out var corpseItem, out _, out _))
                {
                    Say("The corpse item is no longer available.");
                    return;
                }

                definitionId = corpseItem.DefinitionId;
                stack = corpseItem.Quantity;
            }
            else if (State != null && State.TryFindItem(payload.ItemInstanceId, out var item, out _))
            {
                definitionId = item.DefinitionId;
                stack = item.Quantity;
            }
            else
            {
                Say("The dragged item is no longer available.");
                return;
            }

            var maximum = corpse || corpseDestination ? stack : stack - 1;
            if (maximum < 1)
            {
                Say("A single item cannot be split.");
                return;
            }

            var name = State.Catalog.GetDisplayName(definitionId);
            dialog.AskQuantity(
                corpse || corpseDestination ? "Move Part" : "Split Stack",
                name + " (" + stack.ToString(CultureInfo.InvariantCulture) + ")",
                1,
                maximum,
                Math.Max(1, stack / 2),
                PointerPosition(),
                chosen);
        }

        // Dropped outside every window, a carried or equipped item is offered for destruction.
        private void OnDroppedOutside(InventoryDragPayload payload)
        {
            if (payload.Kind != InventoryDragPayloadKind.Item
                || State == null
                || !State.TryFindItem(payload.ItemInstanceId, out var item, out _))
            {
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
                "Destroy Item",
                "Destroy " + name + "? It cannot be brought back.",
                "Destroy",
                () => Report(interactions.Destroy(item)));
        }

        // Right-clicking a carried or equipped item. An open vendor takes a carried item into
        // its Sell slot, and an open Bank takes the item.
        private void OnItemRightClicked(InventoryItem item)
        {
            var state = State;
            if (!InventoryDropPlanner.CanMoveItem(state, item))
            {
                Say(state.IsPredictedItem(item.ItemInstanceId)
                    ? "Wait a moment until the stack is confirmed."
                    : "Inventory mutation is unavailable while state is loading.");
                return;
            }

            if (IsStorageOpen
                && storageContext == InventoryContextKind.Vendor
                && state.TryFindItem(item.ItemInstanceId, out _, out var location)
                && location.Kind == InventoryItemLocationKind.Container
                && ItemContainerTypeIds.IsCarried(location.ContainerType))
            {
                storage.OfferForSale(InventoryDragPayload.ForItem(item.ItemInstanceId));
                return;
            }

            Report(interactions.QuickMoveItem(item, IsBankShown()));
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
