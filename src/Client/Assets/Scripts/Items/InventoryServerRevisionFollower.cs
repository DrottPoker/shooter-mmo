using System;

namespace Mmorpg.Client.Items
{
    // Follows item-state revisions that the worker committed without a client intent, such
    // as a death that moved every item into the corpse. Such a change arrives only as a newer
    // carry state, so the inventory refreshes to it once its own operation and refresh are
    // done. A refresh that did not reach the revision is retried after a delay that doubles
    // up to a cap, so a failing GameService is never asked every frame.
    public sealed class InventoryServerRevisionFollower
    {
        public const float FirstRetryDelaySeconds = 1f;
        public const float MaximumRetryDelaySeconds = 30f;

        private long requestedRevision = -1;
        private float retryDelaySeconds = FirstRetryDelaySeconds;
        // Negative until a refresh for the requested revision has ended without reaching it.
        private float retryAtSeconds = -1f;

        // Returns true when the inventory should refresh to serverRevision now. The retry
        // delay starts at the first call after the previous refresh ended.
        public bool ShouldRefresh(
            long serverRevision,
            long knownRevision,
            bool operationPending,
            bool refreshInProgress,
            float nowSeconds)
        {
            if (serverRevision <= knownRevision || operationPending || refreshInProgress)
            {
                return false;
            }

            if (serverRevision > requestedRevision)
            {
                requestedRevision = serverRevision;
                retryDelaySeconds = FirstRetryDelaySeconds;
                retryAtSeconds = -1f;
                return true;
            }

            if (retryAtSeconds < 0f)
            {
                retryAtSeconds = nowSeconds + retryDelaySeconds;
                return false;
            }

            if (nowSeconds < retryAtSeconds)
            {
                return false;
            }

            retryDelaySeconds = Math.Min(retryDelaySeconds * 2f, MaximumRetryDelaySeconds);
            retryAtSeconds = -1f;
            return true;
        }

        public void Reset()
        {
            requestedRevision = -1;
            retryDelaySeconds = FirstRetryDelaySeconds;
            retryAtSeconds = -1f;
        }
    }
}
