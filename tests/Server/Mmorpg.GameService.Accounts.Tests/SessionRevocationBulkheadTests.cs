using Microsoft.Extensions.Configuration;
using Mmorpg.GameService.Accounts.Redis;

namespace Mmorpg.GameService.Accounts.Tests;

// A login or logout writes its revocation tombstones to Redis inside its PostgreSQL transaction, so
// a Redis that stalls would hold a pooled connection and row locks of every such caller for the
// revocation deadline. The bulkhead bounds how many callers may be in that section at once, takes
// its slot before the connection is opened, and refuses the rest with auth_busy.
public sealed class SessionRevocationBulkheadTests
{
    [Fact]
    public async Task OnlyAsManyCallersAsTheSlotsAreInsideAndTheRestAreRefusedAfterTheWait()
    {
        using var metrics = new RedisAccelerationMetrics();
        using var bulkhead = new SessionRevocationBulkhead(Options(maxConcurrency: 2, waitMilliseconds: 50), metrics);

        var first = await bulkhead.TryEnterAsync(CancellationToken.None);
        var second = await bulkhead.TryEnterAsync(CancellationToken.None);
        var refused = await bulkhead.TryEnterAsync(CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(refused);
        Assert.Equal(0, bulkhead.Available);
        Assert.Equal(1, metrics.Capture().SessionRevocationRefusals);

        first.Dispose();
        first.Dispose();
        Assert.Equal(1, bulkhead.Available);
        var next = await bulkhead.TryEnterAsync(CancellationToken.None);
        Assert.NotNull(next);
        Assert.Equal(0, bulkhead.Available);
        second.Dispose();
        next.Dispose();
        Assert.Equal(2, bulkhead.Available);
    }

    // A caller that comes while all slots are taken waits for one for the short wait, so a slot that
    // frees within it is not refused.
    [Fact]
    public async Task ACallerThatWaitsGetsASlotThatFreesWithinTheWait()
    {
        using var bulkhead = new SessionRevocationBulkhead(Options(maxConcurrency: 1, waitMilliseconds: 2_000));
        var held = await bulkhead.TryEnterAsync(CancellationToken.None);
        var waiting = bulkhead.TryEnterAsync(CancellationToken.None).AsTask();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.False(waiting.IsCompleted);
        held!.Dispose();
        var slot = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.NotNull(slot);
    }

    // Without Redis acceleration nothing is written to Redis, so nothing is bounded.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task NothingIsBoundedWhenNoTombstoneIsWritten(bool enabled, bool sessionCacheEnabled)
    {
        using var bulkhead = new SessionRevocationBulkhead(
            Options(maxConcurrency: 1, waitMilliseconds: 0, enabled: enabled, sessionCacheEnabled: sessionCacheEnabled));

        var slots = new List<IDisposable?>();
        for (var index = 0; index < 5; index++)
        {
            slots.Add(await bulkhead.TryEnterAsync(CancellationToken.None));
        }

        Assert.All(slots, Assert.NotNull);
        Assert.Equal(int.MaxValue, bulkhead.Available);
    }

    [Fact]
    public void TheBoundIsAQuarterOfThePoolByDefaultAndAtMostHalfOfIt()
    {
        var defaults = RedisAccelerationOptions.FromConfiguration(Configuration(("Database:MaximumPoolSize", "64")));
        var small = RedisAccelerationOptions.FromConfiguration(Configuration(("Database:MaximumPoolSize", "3")));
        var chosen = RedisAccelerationOptions.FromConfiguration(Configuration(
            ("Database:MaximumPoolSize", "10"),
            ("RedisAcceleration:SessionRevocationMaxConcurrency", "5"),
            ("RedisAcceleration:SessionRevocationWaitMilliseconds", "0")));
        var noPool = RedisAccelerationOptions.FromConfiguration(Configuration());

        Assert.Equal(16, defaults.SessionRevocationMaxConcurrency);
        Assert.Equal(TimeSpan.FromMilliseconds(250), defaults.SessionRevocationWait);
        Assert.Equal(1, small.SessionRevocationMaxConcurrency);
        Assert.Equal(5, chosen.SessionRevocationMaxConcurrency);
        Assert.Equal(TimeSpan.Zero, chosen.SessionRevocationWait);
        Assert.Equal(RedisAccelerationOptions.AssumedPoolSize / 4, noPool.SessionRevocationMaxConcurrency);
    }

    [Theory]
    [InlineData("10", "6", "SessionRevocationMaxConcurrency must be between 1 and 5, half of Database:MaximumPoolSize (10)")]
    [InlineData("10", "0", "SessionRevocationMaxConcurrency must be between 1 and 5")]
    [InlineData("1", "2", "SessionRevocationMaxConcurrency must be between 1 and 1")]
    public void TheBoundIsValidatedAgainstThePool(string pool, string bound, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => RedisAccelerationOptions.FromConfiguration(Configuration(
            ("Database:MaximumPoolSize", pool),
            ("RedisAcceleration:SessionRevocationMaxConcurrency", bound))));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("5001")]
    public void TheWaitIsBounded(string wait)
    {
        var error = Assert.Throws<InvalidOperationException>(() => RedisAccelerationOptions.FromConfiguration(Configuration(
            ("RedisAcceleration:SessionRevocationWaitMilliseconds", wait))));

        Assert.Contains("SessionRevocationWaitMilliseconds must be between 0 and 5000", error.Message, StringComparison.Ordinal);
    }

    private static RedisAccelerationOptions Options(
        int maxConcurrency,
        int waitMilliseconds,
        bool enabled = true,
        bool sessionCacheEnabled = true)
    {
        return new RedisAccelerationOptions(
            enabled,
            "bulkhead-test",
            sessionCacheEnabled,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
            true,
            TimeSpan.FromSeconds(30))
        {
            SessionRevocationMaxConcurrency = maxConcurrency,
            SessionRevocationWait = TimeSpan.FromMilliseconds(waitMilliseconds)
        };
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(setting => setting.Key, setting => (string?)setting.Value))
            .Build();
    }
}
