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
            if (!InventoryDropPlanner.CanMoveItem(state, item))
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
                if (interactions.CanOfferToVendor(vendor, payload, out var reason))
                {
                    OnVendorDrop(payload);
                }
                else
                {
                    RejectDrop(reason);
                }

                return;
            }

            ShowResult(interactions.QuickMoveItem(item, IsBankShown(state)), true);
        }

        private void OnCorpseItemRightClicked(CorpseLootItem item)
        {
            ShowResult(interactions.QuickMoveCorpseItem(item), false);
        }

        private void OnRecoveryDeliveryRightClicked(RecoveryDelivery delivery)
        {
            ShowResult(interactions.QuickMoveRecoveryDelivery(delivery), true);
        }

        private void ExecuteLootAll()
        {
            ShowResult(interactions.LootAll(), false);
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
