namespace Mmorpg.Client.Items
{
    // Decides when the inventory refreshes to an item-state revision that the worker
    // committed without a client intent, such as a death that moved every item into the
    // corpse. Such a change arrives only as a newer carry state, so the inventory asks for
    // it once per revision, after its own operation and refresh are done.
    public static class InventoryServerRevisionRule
    {
        public static bool ShouldRefresh(
            long serverRevision,
            long knownRevision,
            long lastRequestedRevision,
            bool operationPending,
            bool refreshInProgress)
        {
            return serverRevision > knownRevision
                && serverRevision > lastRequestedRevision
                && !operationPending
                && !refreshInProgress;
        }
    }
}
