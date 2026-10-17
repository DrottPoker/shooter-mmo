using System;
using System.Linq;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
    // Chooses how much state an operation refreshes. An operation that stays inside the
    // Bank refreshes only the Bank; anything that touches carried storage, equipment,
    // Recovery Storage, or a corpse refreshes the full character state.
    public static class InventoryRefreshScopes
    {
        public static InventoryRefreshScope ForMove(
            InventoryClientState state,
            InventoryItemLocation source,
            Guid destinationContainerId)
        {
            return IsBank(state, source) && state.Bank.ContainerId == destinationContainerId
                ? InventoryRefreshScope.Bank
                : InventoryRefreshScope.Full;
        }

        public static InventoryRefreshScope ForLocations(
            InventoryClientState state,
            params InventoryItemLocation[] locations)
        {
            return locations != null
                && locations.Length > 0
                && locations.All(location => IsBank(state, location))
                    ? InventoryRefreshScope.Bank
                    : InventoryRefreshScope.Full;
        }

        private static bool IsBank(InventoryClientState state, InventoryItemLocation location)
        {
            return location != null
                && location.Kind == InventoryItemLocationKind.Container
                && string.Equals(location.ContainerType, ItemContainerTypeIds.Bank, StringComparison.Ordinal)
                && state?.Bank != null
                && state.Bank.ContainerId == location.ContainerId;
        }
    }
}
