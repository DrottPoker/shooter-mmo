namespace AuthService.Redis;

public sealed class RedisAccelerationMetricsReporterService(
    RedisAccelerationMetrics metrics,
    RedisAccelerationOptions options,
    ILogger<RedisAccelerationMetricsReporterService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.MetricsInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var snapshot = metrics.Capture();
            logger.LogInformation(
                "Redis acceleration cumulative: session hits {SessionHits}, misses {SessionMisses}, tombstone bypasses {TombstoneBypasses}, database fallbacks {DatabaseFallbacks}, stores {Stores}, invalidations {Invalidations}, failures {Failures}; distributed authentication rate limit allowed {RateAllowed}, rejected {RateRejected}, local fallbacks {RateFallbacks}.",
                snapshot.SessionCacheHits,
                snapshot.SessionCacheMisses,
                snapshot.SessionCacheTombstoneBypasses,
                snapshot.SessionCacheDatabaseFallbacks,
                snapshot.SessionCacheStores,
                snapshot.SessionCacheInvalidations,
                snapshot.RedisFailures,
                snapshot.DistributedRateLimitAllowed,
                snapshot.DistributedRateLimitRejected,
                snapshot.DistributedRateLimitFallbacks);
        }
    }
}
