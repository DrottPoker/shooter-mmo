using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Accounts.Redis;
using Mmorpg.GameService.Platform.Redis;
using StackExchange.Redis;

namespace Mmorpg.GameService.Accounts.Tests;

// The revocation tombstones go to Redis together, a transient failure is tried once more, and
// the whole write keeps to its deadline, so a revocation either has its tombstones or fails.
public sealed class RedisSessionRevocationTests
{
    [Fact]
    public async Task ATransientFailureIsTriedOnceMore()
    {
        var database = TombstoneDatabase.Create(TombstoneDatabase.FailFirst(new RedisTimeoutException(CommandFlags.None, "Timeout awaiting response.", CommandStatus.Sent)));
        using var metrics = new RedisAccelerationMetrics();

        var prepared = await Cache(database, metrics).PrepareRevocationsAsync([Revocation()], CancellationToken.None);

        Assert.True(prepared);
        Assert.Equal(2, TombstoneDatabase.Of(database).Writes);
        Assert.Equal(1, metrics.Capture().RedisFailures);
        Assert.Equal(1, metrics.Capture().SessionCacheInvalidations);
    }

    [Fact]
    public async Task TwoTransientFailuresRefuseTheRevocation()
    {
        var database = TombstoneDatabase.Create(_ => Task.FromException<bool>(new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "No connection.", null, CommandStatus.Unknown)));
        using var metrics = new RedisAccelerationMetrics();

        var prepared = await Cache(database, metrics).PrepareRevocationsAsync([Revocation()], CancellationToken.None);

        Assert.False(prepared);
        Assert.Equal(2, TombstoneDatabase.Of(database).Writes);
    }

    // A failure that is not transient, such as an error answer, is not tried again.
    [Fact]
    public async Task AServerErrorIsNotTriedAgain()
    {
        var database = TombstoneDatabase.Create(_ => Task.FromException<bool>(new RedisServerException(RedisErrorKind.UnknownError, CommandFlags.None, "WRONGTYPE")));
        using var metrics = new RedisAccelerationMetrics();

        var prepared = await Cache(database, metrics).PrepareRevocationsAsync([Revocation()], CancellationToken.None);

        Assert.False(prepared);
        Assert.Equal(1, TombstoneDatabase.Of(database).Writes);
    }

    // A Redis that never answers costs the revocation its deadline, not more.
    [Fact]
    public async Task AWriteThatNeverAnswersEndsAtTheDeadline()
    {
        var database = TombstoneDatabase.Create(_ => new TaskCompletionSource<bool>().Task);
        using var metrics = new RedisAccelerationMetrics();
        var started = Stopwatch.GetTimestamp();

        var prepared = await Cache(database, metrics, TimeSpan.FromMilliseconds(300))
            .PrepareRevocationsAsync([Revocation()], CancellationToken.None);

        Assert.False(prepared);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(3));
    }

    // Every tombstone of one revocation is sent before any answer is awaited: the database
    // answers only once both writes arrived, so writes one after the other would time out.
    [Fact]
    public async Task TheTombstonesOfOneRevocationAreSentTogether()
    {
        var bothSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = 0;
        var database = TombstoneDatabase.Create(_ =>
        {
            if (Interlocked.Increment(ref sent) == 2)
            {
                bothSent.SetResult(true);
            }

            return bothSent.Task;
        });
        using var metrics = new RedisAccelerationMetrics();

        var prepared = await Cache(database, metrics, TimeSpan.FromSeconds(2))
            .PrepareRevocationsAsync([Revocation(), Revocation()], CancellationToken.None);

        Assert.True(prepared);
        Assert.Equal(2, TombstoneDatabase.Of(database).Writes);
        Assert.Equal(2, TombstoneDatabase.Of(database).Deletes);
    }

    private static RedisAccountSessionCache Cache(
        IDatabase database,
        RedisAccelerationMetrics metrics,
        TimeSpan? deadline = null)
    {
        var options = new RedisAccelerationOptions(
            true,
            "shooter-mmo-revocation-test",
            true,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
            true,
            TimeSpan.FromSeconds(30))
        {
            SessionRevocationDeadline = deadline ?? TimeSpan.FromSeconds(5)
        };
        return new RedisAccountSessionCache(
            new FixedDatabaseProvider(database),
            options,
            metrics,
            TimeProvider.System,
            NullLogger<RedisAccountSessionCache>.Instance);
    }

    private static AccountSessionCacheRevocation Revocation()
    {
        return new AccountSessionCacheRevocation(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            AccountSessionRevocationReason.Logout);
    }

    private sealed class FixedDatabaseProvider(IDatabase database) : IRedisConnectionProvider
    {
        public ValueTask<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(database);
        }
    }

    // Answers a tombstone write with the test's task and every key delete with true.
    internal class TombstoneDatabase : DispatchProxy
    {
        private Func<int, Task<bool>> write = _ => Task.FromResult(true);
        private int writes;
        private int deletes;

        public int Writes => Volatile.Read(ref writes);

        public int Deletes => Volatile.Read(ref deletes);

        public static IDatabase Create(Func<int, Task<bool>> write)
        {
            var database = DispatchProxy.Create<IDatabase, TombstoneDatabase>();
            ((TombstoneDatabase)(object)database).write = write;
            return database;
        }

        public static TombstoneDatabase Of(IDatabase database)
        {
            return (TombstoneDatabase)(object)database;
        }

        // The first write fails with the exception, every later one succeeds.
        public static Func<int, Task<bool>> FailFirst(Exception exception)
        {
            return attempt => attempt == 1 ? Task.FromException<bool>(exception) : Task.FromResult(true);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDatabaseAsync.StringSetAsync) && targetMethod.ReturnType == typeof(Task<bool>))
            {
                return write(Interlocked.Increment(ref writes));
            }

            if (targetMethod?.Name == nameof(IDatabaseAsync.KeyDeleteAsync) && targetMethod.ReturnType == typeof(Task<bool>))
            {
                Interlocked.Increment(ref deletes);
                return Task.FromResult(true);
            }

            throw new NotSupportedException($"The test database does not support {targetMethod?.Name}.");
        }
    }
}
