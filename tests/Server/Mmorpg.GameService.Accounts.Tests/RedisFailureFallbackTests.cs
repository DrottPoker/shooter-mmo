using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameService.Accounts;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Accounts.Redis;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.GameService.Platform.Redis;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.Server.Common.Networking;

namespace Mmorpg.GameService.Accounts.Tests;

public sealed class RedisFailureFallbackTests
{
    [Fact]
    public async Task UnavailableRedisFallsBackForReadsAndRateLimitsButRejectsRevocationPreparation()
    {
        var config = new PlatformOptions(
            "Host=127.0.0.1;Database=test;Username=test;Password=test",
            "127.0.0.1:1",
            TimeSpan.FromMilliseconds(100),
            RunMigrationsOnStartup: false);
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
