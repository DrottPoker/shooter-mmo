using Mmorpg.GameService.Accounts.Redis;

namespace Mmorpg.GameService.Accounts;

// Bounds how many logins and logouts are between taking their pooled connection and the end of
// their transaction at once. Inside that section they write the revocation tombstones to Redis, on
// purpose: a revocation whose invalidation failed must not commit. A Redis that refuses answers at
// once, but one that stalls keeps every such caller there for the revocation deadline, with a
// pooled connection and the row locks of the account and its sessions, so logins could hold the
// whole pool until the worker's registry heartbeat timed out waiting for a connection and, after
// its outage tolerance, the Shard stopped, though Redis never decides simulation authority.
//
// The slot is taken BEFORE the connection is opened. A caller that cannot get one within
// SessionRevocationWait is refused with auth_busy and a Retry-After, as a full password hashing
// lane refuses, which the client retries. A deployment without Redis acceleration writes no
// tombstone, so nothing is bounded. Thread-safe.
internal sealed class SessionRevocationBulkhead : IDisposable
{
    // Whole seconds that a refused caller is told to wait; a slot is held for at most the
    // revocation deadline, which is shorter.
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim? slots;
    private readonly TimeSpan wait;
    private readonly RedisAccelerationMetrics? metrics;

    public SessionRevocationBulkhead(
        RedisAccelerationOptions options,
        RedisAccelerationMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SessionRevocationMaxConcurrency, 1);
        this.metrics = metrics;
        wait = options.SessionRevocationWait;
        if (options.Enabled && options.SessionCacheEnabled)
        {
            slots = new SemaphoreSlim(
                options.SessionRevocationMaxConcurrency,
                options.SessionRevocationMaxConcurrency);
        }
    }

    // Free slots; int.MaxValue when nothing is bounded.
    public int Available => slots?.CurrentCount ?? int.MaxValue;

    // The slot, which the caller disposes when its transaction ended, or null when none came free
    // within the wait.
    public async ValueTask<Slot?> TryEnterAsync(CancellationToken cancellationToken)
    {
        if (slots is null)
        {
            return new Slot(null);
        }

        if (!await slots.WaitAsync(wait, cancellationToken))
        {
            metrics?.RecordSessionRevocationRefused();
            return null;
        }

        return new Slot(slots);
    }

    public void Dispose()
    {
        slots?.Dispose();
    }

    internal sealed class Slot : IDisposable
    {
        private SemaphoreSlim? held;

        public Slot(SemaphoreSlim? held)
        {
            this.held = held;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref held, null)?.Release();
        }
    }
}
