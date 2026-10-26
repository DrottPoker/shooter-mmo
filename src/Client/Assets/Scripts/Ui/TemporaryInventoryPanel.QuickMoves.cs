using System;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Ui
{
    // Right-click moves and Loot all. InventoryQuickMoves decides where an item goes; the panel
    // only knows which window is open: an open Bank takes the player's items, and an open vendor
    // puts a carried item into the Sell slot.
    public sealed partial class TemporaryInventoryPanel
    {
        private void OnItemRightClicked(InventoryItem item)
        {
            var state = controller.State;
            if (!CanMoveItem(state, item))
            {
                RejectDrop(state.IsPredictedItem(item.ItemInstanceId)
                    ? "Wait a moment until the stack is confirmed."
                    : "Inventory mutation is unavailable while state is loading.");
                return;
            }

            if (TryGetShownVendor(state, out var vendor)
                && state.TryFindItem(item.ItemInstanceId, out _, out var location)
                && location.Kind == InventoryItemLocationKind.Container
                && ItemContainerTypeIds.IsCarried(location.ContainerType))
            {
                var payload = InventoryDragPayload.ForItem(item.ItemInstanceId);
                if (CanDropOnVendor(state, vendor, payload, out var reason))
                {
                    OnVendorDrop(payload);
                }
                else
                {
                    RejectDrop(reason);
                }

                return;
            }

            RunQuickMove(InventoryQuickMoves.ForItem(state, item, IsBankShown(state)));
        }

        private void OnCorpseItemRightClicked(CorpseLootItem item)
        {
            if (!corpseController.State.CanShowOperations || !CanMoveItem(controller.State, null))
            {
                RejectDrop("Inventory or corpse mutation is unavailable while state is loading or busy.");
                return;
            }

            RunQuickMove(InventoryQuickMoves.ForCorpseItem(controller.State, corpseController.State.ShownView, item));
        }

        private void RunQuickMove(InventoryQuickMove move)
        {
            var error = string.Empty;
            switch (move.Kind)
            {
                case InventoryQuickMoveKind.Equip:
                    // The items that block the slot leave first; if one cannot, nothing is equipped.
                    foreach (var unequip in move.Before)
                    {
                        if (!controller.TryUnequip(unequip.Item, unequip.Container.ContainerId, unequip.Slot.SlotIndex, out error))
                        {
                            Execute(false, error, string.Empty);
                            return;
                        }
                    }

                    Execute(controller.TryEquip(move.Item, move.EquipmentSlotId, out error), error, "Equip submitted.");
                    return;
                case InventoryQuickMoveKind.Unequip:
                    Execute(
                        controller.TryUnequip(move.Item, move.Container.ContainerId, move.Slot.SlotIndex, out error),
                        error,
                        "Move submitted.");
                    return;
                case InventoryQuickMoveKind.Relocate:
                    Execute(
                        controller.TryRelocate(move.Item, move.Container.ContainerId, move.Slot.SlotIndex, out error),
                        error,
                        "Move submitted.");
                    return;
                case InventoryQuickMoveKind.Merge:
                    Execute(controller.TryMerge(move.Item, move.Target, out error), error, "Merge submitted.");
                    return;
                case InventoryQuickMoveKind.ClaimRecovery:
                    ClaimRecovery(move.Delivery, move.Container.ContainerId);
                    return;
                case InventoryQuickMoveKind.Loot:
                    ExecuteCorpse(
                        corpseController.TryLoot(move.CorpseItem, move.Container, move.Slot, move.CorpseItem.Quantity, out error),
                        error,
                        "Corpse loot submitted.");
                    return;
                case InventoryQuickMoveKind.LootToEquipment:
                {
                    var equipmentSlot = controller.State.FullSnapshot?.Equipment.FirstOrDefault(slot =>
                        string.Equals(slot.SlotId, move.EquipmentSlotId, StringComparison.Ordinal));
                    ExecuteCorpse(
                        equipmentSlot != null
                            && corpseController.TryLootToEquipment(move.CorpseItem, equipmentSlot, out error),
                        string.IsNullOrEmpty(error) ? "The equipment slot is unavailable." : error,
                        "Corpse equip submitted.");
                    return;
                }

                default:
                    RejectDrop(move.Reason);
                    return;
            }
        }

        private void ExecuteLootAll()
        {
            ExecuteCorpse(corpseController.TryLootAll(out var error), error, "Loot all submitted.");
        }

        // The Bank takes right-clicked items only while its tab shows.
        private bool IsBankShown(InventoryClientState state)
        {
            return IsContextModuleVisible && context == InventoryContextKind.Bank && state.Bank != null;
        }

        private bool TryGetShownVendor(InventoryClientState state, out VendorProfileDefinition vendor)
        {
            vendor = null;
            var capability = IsContextModuleVisible && context == InventoryContextKind.Vendor
                ? ActiveVendorCapability()
                : null;
            return capability != null
                && VendorClientRules.TryResolve(state.Catalog, capability.ProfileId, out vendor, out _);
        }
    }
}
