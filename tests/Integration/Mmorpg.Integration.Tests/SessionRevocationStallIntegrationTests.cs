using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Accounts;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Accounts.PasswordHashing;
using Mmorpg.GameService.Accounts.Redis;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.Integration.Tests;

// A Redis that stalls, not refuses, keeps every login and logout inside the section that writes the
// revocation tombstones for the revocation deadline, with a pooled connection and the row locks of
// the account. These tests build the services on a pool of six connections and a Redis stand-in that
// never answers, and measure what the logins hold and what a worker's registry heartbeat gets.
public sealed class SessionRevocationStallIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const int PoolSize = 6;
    private const int Players = 6;

    [PostgresIntegrationFact]
    public async Task AStalledRedisNeverHoldsMoreConnectionsThanTheBoundAndAHeartbeatStillGetsOneAtOnce()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var logins = await RegisterPlayersAsync(context);
        await using var pool = CreatePool(context);
        var stall = new StallingSessionCache();
        using var bulkhead = new SessionRevocationBulkhead(Options(maxConcurrency: 2, waitMilliseconds: 200));
        var accounts = CreateAccountService(context, pool, stall, bulkhead);
        var registry = ActivatorUtilities.CreateInstance<SimulationWorkerRegistryService>(context.Services, pool);

        var attempts = logins.Select(login => Task.Run(() => accounts.LoginAsync(
            new LoginAccountRequest(login, "TestPass123!"),
            CancellationToken.None))).ToArray();
        await stall.WaitUntilEnteredAsync(2);

        // The callers beyond the bound are told to come back, and none of them waited for Redis.
        await WaitUntilAsync(() => attempts.Count(attempt => attempt.IsCompleted) == Players - 2);
        var refused = await Task.WhenAll(attempts.Where(attempt => attempt.IsCompleted));
        Assert.All(refused, result =>
        {
            Assert.False(result.Succeeded);
            Assert.Equal(503, result.StatusCode);
            Assert.Equal(AccountService.AuthBusyCode, result.Error!.Code);
            Assert.Equal(SessionRevocationBulkhead.RetryAfter, result.RetryAfter);
        });
        Assert.Equal(2, stall.Inside);

        // While two logins are stalled in Redis, a worker's registry heartbeat gets a connection at
        // once and renews its registration.
        var started = Stopwatch.GetTimestamp();
        var heartbeat = await registry.HeartbeatAsync(
            WorkerId,
            PostgresIntegrationTestContext.CreateHeartbeatRequest(),
            CancellationToken.None);
        var heartbeatTime = Stopwatch.GetElapsedTime(started);
        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.True(
            heartbeatTime < TimeSpan.FromSeconds(1),
            $"The heartbeat took {heartbeatTime.TotalMilliseconds:0} ms with two logins stalled.");

        // Only the bound ever held a connection in the section, and the two that were inside finish
        // once Redis answers.
        stall.Release();
        var completed = await Task.WhenAll(attempts);
        Assert.Equal(2, completed.Count(result => result.Succeeded));
        Assert.Equal(2, stall.MaximumInside);
        Assert.Equal(0, stall.Inside);
        Assert.Equal(2, bulkhead.Available);
    }

    // Without the bound the same stall holds every connection of the pool, and the heartbeat waits for
    // one until its connection timeout, which is what ended a Shard in the review. The measurement
    // above is only worth something if this setup shows the starvation when the bound is gone.
    [PostgresIntegrationFact]
    public async Task WithoutTheBoundTheSameStallStarvesTheHeartbeat()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var logins = await RegisterPlayersAsync(context);
        await using var pool = CreatePool(context);
        var stall = new StallingSessionCache();
        using var bulkhead = new SessionRevocationBulkhead(Options(maxConcurrency: PoolSize, waitMilliseconds: 200));
        var accounts = CreateAccountService(context, pool, stall, bulkhead);
        var registry = ActivatorUtilities.CreateInstance<SimulationWorkerRegistryService>(context.Services, pool);
        var attempts = logins.Select(login => Task.Run(() => accounts.LoginAsync(
            new LoginAccountRequest(login, "TestPass123!"),
            CancellationToken.None))).ToArray();
        await stall.WaitUntilEnteredAsync(PoolSize);

        var starved = await Assert.ThrowsAnyAsync<Exception>(() => registry.HeartbeatAsync(
            WorkerId,
            PostgresIntegrationTestContext.CreateHeartbeatRequest(),
            CancellationToken.None));

        Assert.Equal(PoolSize, stall.MaximumInside);
        Assert.Contains("pool", starved.ToString(), StringComparison.OrdinalIgnoreCase);
        stall.Release();
        await Task.WhenAll(attempts);
    }

    // A logout is bounded the same way: its slot comes before its connection, and a refused logout is
    // answered auth_busy and revokes nothing.
    [PostgresIntegrationFact]
    public async Task ALogoutBeyondTheBoundIsRefusedBeforeItOpensAConnectionAndRevokesNothing()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var first = await context.RegisterPlayerAsync("first@example.com", "first_player", "First Hero");
        var second = await context.RegisterPlayerAsync("second@example.com", "second_player", "Second Hero");
        await using var pool = CreatePool(context);
        var stall = new StallingSessionCache();
        using var bulkhead = new SessionRevocationBulkhead(Options(maxConcurrency: 1, waitMilliseconds: 50));
        var sessions = ActivatorUtilities.CreateInstance<SessionService>(context.Services, pool, stall, bulkhead);
        var firstLogout = Task.Run(() => sessions.RevokeAsync(
            first.Registration.AccountId,
            first.Registration.SessionId,
            AccountSessionRevocationReason.Logout,
            CancellationToken.None));
        await stall.WaitUntilEnteredAsync(1);

        var refused = await sessions.RevokeAsync(
            second.Registration.AccountId,
            second.Registration.SessionId,
            AccountSessionRevocationReason.Logout,
            CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal(503, refused.StatusCode);
        Assert.Equal(AccountService.AuthBusyCode, refused.Error!.Code);
        Assert.Equal(SessionRevocationBulkhead.RetryAfter, refused.RetryAfter);
        Assert.NotNull(await context.SessionService.AuthenticateTokenAsync(second.Registration.SessionToken, CancellationToken.None));
        stall.Release();
        Assert.True((await firstLogout).Succeeded);
        Assert.Null(await context.SessionService.AuthenticateTokenAsync(first.Registration.SessionToken, CancellationToken.None));
    }

    private static async Task<string[]> RegisterPlayersAsync(PostgresIntegrationTestContext context)
    {
        var logins = new string[Players];
        for (var index = 0; index < Players; index++)
        {
            logins[index] = $"stall_player_{index}";
            var registration = await context.AccountService.RegisterAsync(
                new RegisterAccountRequest($"stall{index}@example.com", logins[index], "TestPass123!"),
                CancellationToken.None);
            Assert.True(registration.Succeeded, registration.Error?.Message);
        }

        return logins;
    }

    // Six connections and a short wait for one, as the starvation of a pool shows up.
    private static NpgsqlDataSource CreatePool(PostgresIntegrationTestContext context)
    {
        var builder = new NpgsqlConnectionStringBuilder(context.ConnectionString)
        {
            MaxPoolSize = PoolSize,
            Timeout = 2
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    private static AccountService CreateAccountService(
        PostgresIntegrationTestContext context,
        NpgsqlDataSource pool,
        IAccountSessionCache cache,
        SessionRevocationBulkhead bulkhead)
    {
        var sessions = ActivatorUtilities.CreateInstance<SessionService>(context.Services, pool, cache, bulkhead);
        return ActivatorUtilities.CreateInstance<AccountService>(context.Services, pool, sessions, bulkhead);
    }

    private static RedisAccelerationOptions Options(int maxConcurrency, int waitMilliseconds)
    {
        return new RedisAccelerationOptions(
            true,
            "stall-test",
            true,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
            true,
            TimeSpan.FromSeconds(30))
        {
            SessionRevocationMaxConcurrency = maxConcurrency,
            SessionRevocationWait = TimeSpan.FromMilliseconds(waitMilliseconds)
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(20))
            {
                throw new TimeoutException("The condition did not hold within 20 seconds.");
            }

            await Task.Delay(10);
        }
    }

    // A Redis that accepts the write and never answers: it counts the callers inside and holds them.
    private sealed class StallingSessionCache : IAccountSessionCache
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int inside;
        private int maximumInside;

        public int Inside => Volatile.Read(ref inside);

        public int MaximumInside => Volatile.Read(ref maximumInside);

        public async Task WaitUntilEnteredAsync(int count)
        {
            var deadline = Stopwatch.GetTimestamp();
            while (Inside < count)
            {
                if (Stopwatch.GetElapsedTime(deadline) > TimeSpan.FromSeconds(20))
                {
                    throw new TimeoutException($"Only {Inside} of {count} callers reached the stalled Redis.");
                }

                await Task.Delay(10);
            }
        }

        public void Release()
        {
            released.TrySetResult();
        }

        public ValueTask<AccountSessionCacheLookup> GetAsync(string tokenHash, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new AccountSessionCacheLookup(AccountSessionCacheLookupStatus.Miss));
        }

        public ValueTask TryStoreAsync(string tokenHash, AccountSessionCacheEntry entry, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public async ValueTask<bool> PrepareRevocationsAsync(
            IReadOnlyCollection<AccountSessionCacheRevocation> revocations,
            CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref inside);
            int seen;
            while ((seen = Volatile.Read(ref maximumInside)) < now
                && Interlocked.CompareExchange(ref maximumInside, now, seen) != seen)
            {
            }

            try
            {
                await released.Task.WaitAsync(cancellationToken);
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref inside);
            }
        }
    }
}
