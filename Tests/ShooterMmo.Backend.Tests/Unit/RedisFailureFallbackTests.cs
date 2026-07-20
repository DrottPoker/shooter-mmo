using AuthService.Auth;
using AuthService.Config;
using AuthService.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using ShooterMmo.Shared.Networking;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class RedisFailureFallbackTests
{
    [Fact]
    public async Task UnavailableRedisFallsBackForReadsAndRateLimitsButRejectsRevocationPreparation()
    {
        var config = new AuthServiceConfig(
            "Host=127.0.0.1;Database=test;Username=test;Password=test",
            "127.0.0.1:1",
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30),
            new SimulationTopologyConfig([], [], []));
        var options = new RedisAccelerationOptions(
            true,
            $"shooter-mmo-failure-test-{Guid.NewGuid():N}",
            true,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
            true,
            TimeSpan.FromSeconds(30));
        using var metrics = new RedisAccelerationMetrics();
        await using var provider = new RedisConnectionProvider(config);
        var cache = new RedisAccountSessionCache(
            provider,
            options,
            metrics,
            TimeProvider.System,
            NullLogger<RedisAccountSessionCache>.Instance);
        var limiter = new RedisAuthenticationRateLimiter(
            provider,
            options,
            metrics,
            TimeProvider.System,
            NullLogger<RedisAuthenticationRateLimiter>.Instance);

        var lookup = await cache.GetAsync(
            TokenGenerator.HashToken("unavailable-redis-token"),
            CancellationToken.None);
        var prepared = await cache.PrepareRevocationsAsync(
            [new AccountSessionCacheRevocation(
                Guid.NewGuid(),
                TokenGenerator.HashToken("unavailable-redis-token"),
                AccountSessionRevocationReason.Logout)],
            CancellationToken.None);
        var rateLimit = await limiter.AcquireAsync(
            "login",
            "127.0.0.1",
            1,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(AccountSessionCacheLookupStatus.Unavailable, lookup.Status);
        Assert.False(prepared);
        Assert.False(rateLimit.Available);
        Assert.True(rateLimit.Allowed);
        Assert.True(metrics.Capture().RedisFailures >= 3);
    }
}
