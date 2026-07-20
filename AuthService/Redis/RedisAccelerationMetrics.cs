using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace AuthService.Redis;

public sealed record RedisAccelerationMetricsSnapshot(
    long SessionCacheHits,
    long SessionCacheMisses,
    long SessionCacheTombstoneBypasses,
    long SessionCacheDatabaseFallbacks,
    long SessionCacheStores,
    long SessionCacheInvalidations,
    long RedisFailures,
    long DistributedRateLimitAllowed,
    long DistributedRateLimitRejected,
    long DistributedRateLimitFallbacks);

public sealed class RedisAccelerationMetrics : IDisposable
{
    private const long FailureLogIntervalMilliseconds = 30_000;
    private readonly ConcurrentDictionary<string, long> lastFailureLogAt =
        new(StringComparer.Ordinal);
    private readonly Meter meter = new("ShooterMmo.AuthService.Redis", "1.0.0");
    private readonly Counter<long> sessionCacheCounter;
    private readonly Counter<long> redisFailureCounter;
    private readonly Counter<long> rateLimitCounter;
    private long sessionCacheHits;
    private long sessionCacheMisses;
    private long sessionCacheTombstoneBypasses;
    private long sessionCacheDatabaseFallbacks;
    private long sessionCacheStores;
    private long sessionCacheInvalidations;
    private long redisFailures;
    private long distributedRateLimitAllowed;
    private long distributedRateLimitRejected;
    private long distributedRateLimitFallbacks;

    public RedisAccelerationMetrics()
    {
        sessionCacheCounter = meter.CreateCounter<long>(
            "auth_service.redis.session_cache.operations");
        redisFailureCounter = meter.CreateCounter<long>(
            "auth_service.redis.failures");
        rateLimitCounter = meter.CreateCounter<long>(
            "auth_service.redis.authentication_rate_limit.operations");
    }

    public void RecordSessionCacheHit()
    {
        Interlocked.Increment(ref sessionCacheHits);
        sessionCacheCounter.Add(1, new KeyValuePair<string, object?>("result", "hit"));
    }

    public void RecordSessionCacheMiss()
    {
        Interlocked.Increment(ref sessionCacheMisses);
        sessionCacheCounter.Add(1, new KeyValuePair<string, object?>("result", "miss"));
    }

    public void RecordSessionCacheTombstoneBypass()
    {
        Interlocked.Increment(ref sessionCacheTombstoneBypasses);
        sessionCacheCounter.Add(1, new KeyValuePair<string, object?>("result", "tombstone_bypass"));
    }

    public void RecordSessionCacheDatabaseFallback()
    {
        Interlocked.Increment(ref sessionCacheDatabaseFallbacks);
        sessionCacheCounter.Add(1, new KeyValuePair<string, object?>("result", "database_fallback"));
    }

    public void RecordSessionCacheStore()
    {
        Interlocked.Increment(ref sessionCacheStores);
        sessionCacheCounter.Add(1, new KeyValuePair<string, object?>("result", "store"));
    }

    public void RecordSessionCacheInvalidation()
    {
        Interlocked.Increment(ref sessionCacheInvalidations);
        sessionCacheCounter.Add(1, new KeyValuePair<string, object?>("result", "invalidation"));
    }

    public bool RecordRedisFailure(string operation)
    {
        Interlocked.Increment(ref redisFailures);
        redisFailureCounter.Add(1, new KeyValuePair<string, object?>("operation", operation));
        var now = Environment.TickCount64;
        while (true)
        {
            if (!lastFailureLogAt.TryGetValue(operation, out var previous))
            {
                if (lastFailureLogAt.TryAdd(operation, now))
                {
                    return true;
                }

                continue;
            }

            if (now - previous < FailureLogIntervalMilliseconds)
            {
                return false;
            }

            if (lastFailureLogAt.TryUpdate(operation, now, previous))
            {
                return true;
            }
        }
    }

    public void RecordDistributedRateLimitAllowed()
    {
        Interlocked.Increment(ref distributedRateLimitAllowed);
        rateLimitCounter.Add(1, new KeyValuePair<string, object?>("result", "allowed"));
    }

    public void RecordDistributedRateLimitRejected()
    {
        Interlocked.Increment(ref distributedRateLimitRejected);
        rateLimitCounter.Add(1, new KeyValuePair<string, object?>("result", "rejected"));
    }

    public void RecordDistributedRateLimitFallback()
    {
        Interlocked.Increment(ref distributedRateLimitFallbacks);
        rateLimitCounter.Add(1, new KeyValuePair<string, object?>("result", "local_fallback"));
    }

    public RedisAccelerationMetricsSnapshot Capture()
    {
        return new RedisAccelerationMetricsSnapshot(
            Volatile.Read(ref sessionCacheHits),
            Volatile.Read(ref sessionCacheMisses),
            Volatile.Read(ref sessionCacheTombstoneBypasses),
            Volatile.Read(ref sessionCacheDatabaseFallbacks),
            Volatile.Read(ref sessionCacheStores),
            Volatile.Read(ref sessionCacheInvalidations),
            Volatile.Read(ref redisFailures),
            Volatile.Read(ref distributedRateLimitAllowed),
            Volatile.Read(ref distributedRateLimitRejected),
            Volatile.Read(ref distributedRateLimitFallbacks));
    }

    public void Dispose()
    {
        meter.Dispose();
    }
}
