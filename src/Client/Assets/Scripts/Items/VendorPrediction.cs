using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
    // Vendor trades as predicted inventory operations, placed the way GameService acquires and
    // sells: a purchase first tops up unprotected stacks of the definition in Permanent
    // Inventory, then the equipped Bag, then takes free slots in the order specialized Bag
    // slots, Permanent Inventory, general Bag slots; a sale takes a carried stack or part of it.
    public static class VendorPrediction
    {
        public static InventoryPredictedOperation Purchase(
            string definitionId,
            int quantity,
            long price,
            InventoryOperationSend send)
        {
            var createdId = Guid.NewGuid();
            return InventoryPredictedOperation.External(
                (InventoryPredictionEdit edit, out string reason) =>
                    TryPurchase(edit, definitionId, quantity, price, createdId, out reason),
                send,
                Array.Empty<Guid>(),
                new[] { createdId });
        }

        public static InventoryPredictedOperation Sale(
            Guid itemInstanceId,
            int quantity,
            long price,
            InventoryOperationSend send)
        {
            return InventoryPredictedOperation.External(
                (InventoryPredictionEdit edit, out string reason) =>
                    TrySell(edit, itemInstanceId, quantity, price, out reason),
                send,
                new[] { itemInstanceId });
        }

        private static bool TryPurchase(
            InventoryPredictionEdit edit,
            string definitionId,
            int quantity,
            long price,
            Guid createdId,
            out string reason)
        {
            reason = "The offer is no longer available.";
            if (!edit.TryGetDefinition(definitionId, out var definition) || quantity <= 0)
            {
                return false;
            }

            if (price > edit.Currency)
            {
                reason = "You cannot afford this purchase.";
                return false;
            }

            edit.Currency -= price;
            var snapshot = edit.View.FullSnapshot;
            var bag = snapshot.EquippedBag?.Contents;
            var remaining = quantity;
            if (definition.MaximumStackSize > 1)
            {
                foreach (var container in new[] { snapshot.PermanentInventory, bag }.Where(container => container != null))
                {
                    foreach (var slot in container.Slots.OrderBy(slot => slot.SlotIndex))
                    {
                        if (remaining == 0)
                        {
                            break;
                        }

                        var stack = slot.Item;
                        if (stack == null
                            || !string.Equals(stack.DefinitionId, definitionId, StringComparison.Ordinal)
                            || stack.Policies.Count > 0
                            || stack.Quantity >= definition.MaximumStackSize)
                        {
                            continue;
                        }

                        var added = Math.Min(remaining, definition.MaximumStackSize - stack.Quantity);
                        edit.Editor.TryReplace(stack.ItemInstanceId, InventoryPredictionEdit.WithQuantity(stack, stack.Quantity + added));
                        remaining -= added;
                    }
                }
            }

            if (remaining > 0)
            {
                // One purchase is at most one stack, so the rest needs one free slot.
                var filled = new HashSet<(Guid, int)>();
                var slot = edit.FindFreeSlot(bag, definition, filled, IsSpecialized);
                var container = slot == null ? null : bag;
                if (slot == null)
                {
                    slot = edit.FindFreeSlot(snapshot.PermanentInventory, definition, filled);
                    container = slot == null ? null : snapshot.PermanentInventory;
                }

                if (slot == null)
                {
                    slot = edit.FindFreeSlot(bag, definition, filled, candidate => !IsSpecialized(candidate));
                    container = slot == null ? null : bag;
                }

                if (slot == null)
                {
                    reason = "Your inventory has no free slot for this purchase.";
                    return false;
                }

                edit.Editor.TryPlace(
                    container.ContainerId,
                    slot.SlotIndex,
                    new InventoryItem(createdId, definitionId, remaining, 0, Array.Empty<InventoryPolicy>()));
            }

            edit.MoveWeight(definition, quantity, null, ItemContainerTypeIds.PermanentInventory);
            reason = "The purchase would exceed the 140 percent carry cap.";
            return edit.FitsHardCap();
        }

        private static bool TrySell(
            InventoryPredictionEdit edit,
            Guid itemInstanceId,
            int quantity,
            long price,
            out string reason)
        {
            reason = "The item is no longer in carried storage.";
            if (!edit.Editor.TryFind(itemInstanceId, out var item, out var source)
                || source.Kind != InventoryItemLocationKind.Container
                || !ItemContainerTypeIds.IsCarried(source.ContainerType)
                || quantity <= 0
                || quantity > item.Quantity
                || !edit.TryGetDefinition(item.DefinitionId, out var definition))
            {
                return false;
            }

            if (quantity < item.Quantity)
            {
                edit.Editor.TryReplace(itemInstanceId, InventoryPredictionEdit.WithQuantity(item, item.Quantity - quantity));
            }
            else
            {
                edit.Editor.Remove(itemInstanceId);
            }

            edit.MoveWeightFrom(definition, quantity, source, null);
            edit.Currency += price;
            return true;
        }

        private static bool IsSpecialized(InventorySlot slot)
        {
            return !string.Equals(slot.SlotKind, BagSlotKindIds.General, StringComparison.Ordinal);
        }
    }
}
