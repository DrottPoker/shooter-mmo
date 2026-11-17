using Mmorpg.Server.Common.Health;
using Mmorpg.Server.Common.Networking;
using Mmorpg.Testing;

namespace Mmorpg.Server.Common.Tests;

// Outside Development Redis has a password, so the readiness probe authenticates with the
// connection string's credentials before its PING.
public sealed class RedisHealthProbeTests
{
    private const string Password = "a-real-redis-secret-for-the-probe";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AProbeWithoutAPasswordPingsAnOpenRedis()
    {
        await using var redis = FakeRedisServer.Start();

        var health = await RedisHealthProbe.CheckAsync(redis.ConnectionString(), Timeout, TestContext.Current.CancellationToken);

        Assert.True(health.IsReachable, health.Error);
    }

    [Fact]
    public async Task AProbeAuthenticatesWithThePasswordAndTheUser()
    {
        await using var redis = FakeRedisServer.Start(Password);
        await using var withUser = FakeRedisServer.Start(Password, "game");

        var health = await RedisHealthProbe.CheckAsync(redis.ConnectionString(Password), Timeout, TestContext.Current.CancellationToken);
        var userHealth = await RedisHealthProbe.CheckAsync(
            withUser.ConnectionString(Password, "game"),
            Timeout,
            TestContext.Current.CancellationToken);

        Assert.True(health.IsReachable, health.Error);
        Assert.True(userHealth.IsReachable, userHealth.Error);
    }

    [Fact]
    public async Task AWrongOrMissingPasswordIsNotReady()
    {
        await using var redis = FakeRedisServer.Start(Password);

        var wrong = await RedisHealthProbe.CheckAsync(redis.ConnectionString("not-the-password"), Timeout, TestContext.Current.CancellationToken);
        var missing = await RedisHealthProbe.CheckAsync(redis.ConnectionString(), Timeout, TestContext.Current.CancellationToken);

        Assert.False(wrong.IsReachable);
        Assert.Equal("Redis refused the credentials of the connection string.", wrong.Error);
        Assert.False(missing.IsReachable);
        Assert.Equal("Redis did not return PONG.", missing.Error);
    }

    [Fact]
    public void CredentialsComeFromTheOptionsOfTheConnectionString()
    {
        Assert.Equal(
            new RedisCredentials("game", "secret=with=equals"),
            RedisConnectionString.ParseCredentials("redis:6379, User=game ,PASSWORD=secret=with=equals,ssl=false"));
        Assert.Equal(new RedisCredentials(null, null), RedisConnectionString.ParseCredentials("127.0.0.1:6379"));
        Assert.Equal(new RedisCredentials(null, null), RedisConnectionString.ParseCredentials("127.0.0.1:6379,password="));
    }
}
