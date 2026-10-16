using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameService.Accounts;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Accounts.Redis;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.GameService.Platform.Redis;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.Server.Common.Networking;
using StackExchange.Redis;

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

    [Fact]
    public async Task MalformedRateLimitScriptResultFallsBackToTheLocalLimiter()
    {
        var options = new RedisAccelerationOptions(
            true,
            $"shooter-mmo-malformed-test-{Guid.NewGuid():N}",
            true,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
            true,
            TimeSpan.FromSeconds(30));
        using var metrics = new RedisAccelerationMetrics();
        var limiter = new RedisAuthenticationRateLimiter(
            new FixedDatabaseProvider(MalformedScriptDatabase.Create()),
            options,
            metrics,
            TimeProvider.System,
            NullLogger<RedisAuthenticationRateLimiter>.Instance);

        var rateLimit = await limiter.AcquireAsync(
            "login",
            "127.0.0.1",
            1,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.False(rateLimit.Available);
        Assert.True(rateLimit.Allowed);
        var snapshot = metrics.Capture();
        Assert.Equal(1, snapshot.RedisFailures);
        Assert.Equal(1, snapshot.DistributedRateLimitFallbacks);
    }

    private sealed class FixedDatabaseProvider(IDatabase database) : IRedisConnectionProvider
    {
        public ValueTask<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(database);
        }
    }

    // Answers every script with one value instead of the {count, 0} pair the limiter expects.
    internal class MalformedScriptDatabase : DispatchProxy
    {
        public static IDatabase Create()
        {
            return DispatchProxy.Create<IDatabase, MalformedScriptDatabase>();
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDatabaseAsync.ScriptEvaluateAsync)
                && targetMethod.ReturnType == typeof(Task<RedisResult>))
            {
                return Task.FromResult(RedisResult.Create(new[] { RedisResult.Create(1) }));
            }

            throw new NotSupportedException($"The test database does not support {targetMethod?.Name}.");
        }
    }
}
