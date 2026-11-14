using System.Text.Json;
using Mmorpg.GameService.Platform.Redis;
using StackExchange.Redis;

namespace Mmorpg.GameService.Accounts.Redis;

internal sealed class RedisAccountSessionCache(
    IRedisConnectionProvider connectionProvider,
    RedisAccelerationOptions options,
    RedisAccelerationMetrics metrics,
    TimeProvider timeProvider,
    ILogger<RedisAccountSessionCache> logger) : IAccountSessionCache
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private const int MaximumTombstoneAttempts = 2;
    private static readonly TimeSpan TombstoneRetryPause = TimeSpan.FromMilliseconds(100);

    public async ValueTask<AccountSessionCacheLookup> GetAsync(
        string tokenHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        if (!options.Enabled || !options.SessionCacheEnabled)
        {
            metrics.RecordSessionCacheMiss();
            return new AccountSessionCacheLookup(AccountSessionCacheLookupStatus.Miss);
        }

        try
        {
            var database = await connectionProvider.GetDatabaseAsync(cancellationToken);
            var value = await database.StringGetAsync(TokenKey(tokenHash)).WaitAsync(cancellationToken);
            if (value.IsNullOrEmpty)
            {
                metrics.RecordSessionCacheMiss();
                return new AccountSessionCacheLookup(AccountSessionCacheLookupStatus.Miss);
            }

            AccountSessionCacheEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<AccountSessionCacheEntry>(
                    (byte[])value!,
                    JsonOptions);
            }
            catch (JsonException)
            {
                await database.KeyDeleteAsync(TokenKey(tokenHash)).WaitAsync(cancellationToken);
                metrics.RecordSessionCacheMiss();
                return new AccountSessionCacheLookup(AccountSessionCacheLookupStatus.Miss);
            }

            if (entry is null
                || entry.AccountId == Guid.Empty
                || entry.SessionId == Guid.Empty
                || string.IsNullOrWhiteSpace(entry.Username)
                || entry.ExpiresAt <= timeProvider.GetUtcNow().UtcDateTime)
            {
                await database.KeyDeleteAsync(TokenKey(tokenHash)).WaitAsync(cancellationToken);
                metrics.RecordSessionCacheMiss();
                return new AccountSessionCacheLookup(AccountSessionCacheLookupStatus.Miss);
            }

            if (await database.KeyExistsAsync(TombstoneKey(entry.SessionId))
                    .WaitAsync(cancellationToken))
            {
                metrics.RecordSessionCacheTombstoneBypass();
                return new AccountSessionCacheLookup(
                    AccountSessionCacheLookupStatus.TombstoneBypass);
            }

            metrics.RecordSessionCacheHit();
            return new AccountSessionCacheLookup(AccountSessionCacheLookupStatus.Hit, entry);
        }
        catch (Exception exception) when (IsRedisFailure(exception, cancellationToken))
        {
            if (metrics.RecordRedisFailure("session_cache_get"))
            {
                logger.LogWarning(
                    exception,
                    "Redis account-session cache read failed. PostgreSQL will validate the request. Repeated warnings are throttled.");
            }

            return new AccountSessionCacheLookup(AccountSessionCacheLookupStatus.Unavailable);
        }
    }

    public async ValueTask TryStoreAsync(
        string tokenHash,
        AccountSessionCacheEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentNullException.ThrowIfNull(entry);
        if (!options.Enabled || !options.SessionCacheEnabled)
        {
            return;
        }

        var remainingLifetime = entry.ExpiresAt - timeProvider.GetUtcNow().UtcDateTime;
        var ttl = remainingLifetime < options.SessionCacheTtl
            ? remainingLifetime
            : options.SessionCacheTtl;
        if (ttl <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            var database = await connectionProvider.GetDatabaseAsync(cancellationToken);
            if (await database.KeyExistsAsync(TombstoneKey(entry.SessionId))
                    .WaitAsync(cancellationToken))
            {
                metrics.RecordSessionCacheTombstoneBypass();
                return;
            }

            var value = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
            await database.StringSetAsync(TokenKey(tokenHash), value, ttl)
                .WaitAsync(cancellationToken);
            metrics.RecordSessionCacheStore();
        }
        catch (Exception exception) when (IsRedisFailure(exception, cancellationToken))
        {
            if (metrics.RecordRedisFailure("session_cache_store"))
            {
                logger.LogWarning(
                    exception,
                    "Redis account-session cache store failed. PostgreSQL remains authoritative. Repeated warnings are throttled.");
            }
        }
    }

    // Stores every tombstone of the revocation in one round trip. A transient failure, such as a
    // timeout or a lost connection, is tried once more within SessionRevocationDeadline; a
    // tombstone write is idempotent, so a second attempt is safe.
    public async ValueTask<bool> PrepareRevocationsAsync(
        IReadOnlyCollection<AccountSessionCacheRevocation> revocations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revocations);
        if (!options.Enabled || !options.SessionCacheEnabled || revocations.Count == 0)
        {
            return true;
        }

        var started = timeProvider.GetTimestamp();
        Exception? failure = null;
        for (var attempt = 1; attempt <= MaximumTombstoneAttempts; attempt++)
        {
            if (Remaining(started) <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                var database = await connectionProvider.GetDatabaseAsync(cancellationToken)
                    .AsTask()
                    .WaitAsync(Remaining(started), timeProvider, cancellationToken);
                var writes = Task.WhenAll(revocations.Select(revocation => database.StringSetAsync(
                    TombstoneKey(revocation.SessionId),
                    revocation.Reason,
                    options.SessionRevocationTombstoneLifetime)));
                ObserveLateFailure(writes);
                var marked = await writes.WaitAsync(Remaining(started), timeProvider, cancellationToken);
                if (!marked.All(stored => stored))
                {
                    metrics.RecordRedisFailure("session_cache_tombstone");
                    return false;
                }

                await DeleteTokenKeysAsync(database, revocations, cancellationToken);
                return true;
            }
            catch (Exception exception) when (IsRedisFailure(exception, cancellationToken))
            {
                failure = exception;
                if (!IsTransient(exception) || attempt == MaximumTombstoneAttempts)
                {
                    break;
                }

                metrics.RecordRedisFailure("session_cache_revoke_retry");
                // A lost connection fails at once; a short pause lets the client reconnect.
                var remaining = Remaining(started);
                await Task.Delay(
                    remaining < TombstoneRetryPause ? remaining : TombstoneRetryPause,
                    timeProvider,
                    cancellationToken);
            }
        }

        if (metrics.RecordRedisFailure("session_cache_revoke"))
        {
            logger.LogWarning(
                failure,
                "Redis session revocation tombstones could not be stored within {DeadlineMilliseconds} ms. The durable revocation must not commit while stale cache entries can remain valid. Repeated warnings are throttled.",
                options.SessionRevocationDeadline.TotalMilliseconds);
        }

        return false;
    }

    // The cached tokens go after their tombstones, together; a failure here leaves stale keys
    // that the tombstones already make useless.
    private async Task DeleteTokenKeysAsync(
        IDatabase database,
        IReadOnlyCollection<AccountSessionCacheRevocation> revocations,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(revocations.Select(revocation => database.KeyDeleteAsync(TokenKey(revocation.TokenHash))))
                .WaitAsync(cancellationToken);
        }
        catch (Exception exception) when (IsRedisFailure(exception, cancellationToken))
        {
            if (metrics.RecordRedisFailure("session_cache_delete"))
            {
                logger.LogWarning(
                    exception,
                    "A Redis session cache key could not be deleted after its revocation tombstone was stored. The tombstone still prevents stale authorization. Repeated warnings are throttled.");
            }
        }

        for (var index = 0; index < revocations.Count; index++)
        {
            metrics.RecordSessionCacheInvalidation();
        }
    }

    // What is left of the revocation deadline, never below zero.
    private TimeSpan Remaining(long started)
    {
        var remaining = options.SessionRevocationDeadline - timeProvider.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    // A write that the deadline gave up on may still fail later; its failure is observed here.
    private static void ObserveLateFailure(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool IsTransient(Exception exception)
    {
        return exception is RedisTimeoutException or RedisConnectionException or TimeoutException;
    }

    private string TokenKey(string tokenHash)
    {
        return $"{options.KeyPrefix}:auth-session:token:{tokenHash}";
    }

    private string TombstoneKey(Guid sessionId)
    {
        return $"{options.KeyPrefix}:auth-session:revoked:{sessionId:N}";
    }

    private static bool IsRedisFailure(
        Exception exception,
        CancellationToken cancellationToken)
    {
        return exception is RedisException or TimeoutException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);
    }
}
