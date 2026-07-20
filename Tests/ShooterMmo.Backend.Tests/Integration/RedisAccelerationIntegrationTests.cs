using System.Text.Json;
using AuthService.Auth;
using AuthService.Config;
using AuthService.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using ShooterMmo.Shared.Networking;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class RedisAccelerationIntegrationTests
{
    [RedisIntegrationFact]
    public async Task SessionCacheInvalidationAndDistributedRateLimitUseRedis()
    {
        var redisConnectionString = Environment.GetEnvironmentVariable(
            RedisIntegrationFactAttribute.ConnectionStringVariable)!;
        var prefix = $"shooter-mmo-test-{Guid.NewGuid():N}";
        var config = new AuthServiceConfig(
            "Host=127.0.0.1;Database=test;Username=test;Password=test",
            redisConnectionString,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30),
            new SimulationTopologyConfig([], [], []));
        var options = new RedisAccelerationOptions(
            true,
            prefix,
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
        var tokenHash = TokenGenerator.HashToken("redis-integration-token");
        var sessionId = Guid.NewGuid();
        var entry = new AccountSessionCacheEntry(
            Guid.NewGuid(),
            "redis_player",
            sessionId,
            DateTime.UtcNow.AddMinutes(5));

        await cache.TryStoreAsync(tokenHash, entry, CancellationToken.None);
        var hit = await cache.GetAsync(tokenHash, CancellationToken.None);
        Assert.Equal(AccountSessionCacheLookupStatus.Hit, hit.Status);

        var prepared = await cache.PrepareRevocationsAsync(
            [new AccountSessionCacheRevocation(sessionId, tokenHash, "integration_test")],
            CancellationToken.None);
        Assert.True(prepared);

        var database = await provider.GetDatabaseAsync(CancellationToken.None);
        var tokenKey = $"{prefix}:auth-session:token:{tokenHash}";
        await database.StringSetAsync(
            tokenKey,
            JsonSerializer.SerializeToUtf8Bytes(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            TimeSpan.FromSeconds(30));
        var bypass = await cache.GetAsync(tokenHash, CancellationToken.None);
        Assert.Equal(AccountSessionCacheLookupStatus.TombstoneBypass, bypass.Status);

        var first = await limiter.AcquireAsync(
            "login", "127.0.0.1", 2, TimeSpan.FromSeconds(30), CancellationToken.None);
        var second = await limiter.AcquireAsync(
            "login", "127.0.0.1", 2, TimeSpan.FromSeconds(30), CancellationToken.None);
        var rejected = await limiter.AcquireAsync(
            "login", "127.0.0.1", 2, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.False(rejected.Allowed);
        Assert.True(rejected.RetryAfter > TimeSpan.Zero);

        var server = database.Multiplexer.GetEndPoints().Single();
        var redisServer = database.Multiplexer.GetServer(server);
        await foreach (var key in redisServer.KeysAsync(pattern: $"{prefix}:*"))
        {
            await database.KeyDeleteAsync(key);
        }
    }
}
