using System.Text.Json;
using AuthService.Auth;
using StackExchange.Redis;

namespace AuthService.Redis;

public sealed class RedisAccountSessionCache(
    IRedisConnectionProvider connectionProvider,
    RedisAccelerationOptions options,
    RedisAccelerationMetrics metrics,
    TimeProvider timeProvider,
    ILogger<RedisAccountSessionCache> logger) : IAccountSessionCache
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

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

    public async ValueTask<bool> PrepareRevocationsAsync(
        IReadOnlyCollection<AccountSessionCacheRevocation> revocations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revocations);
        if (!options.Enabled || !options.SessionCacheEnabled || revocations.Count == 0)
        {
            return true;
        }

        try
        {
            var database = await connectionProvider.GetDatabaseAsync(cancellationToken);
            foreach (var revocation in revocations)
            {
                var marked = await database.StringSetAsync(
                        TombstoneKey(revocation.SessionId),
                        revocation.Reason,
                        options.SessionRevocationTombstoneLifetime)
                    .WaitAsync(cancellationToken);
                if (!marked)
                {
                    metrics.RecordRedisFailure("session_cache_tombstone");
                    return false;
                }
            }

            foreach (var revocation in revocations)
            {
                try
                {
                    await database.KeyDeleteAsync(TokenKey(revocation.TokenHash))
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

                metrics.RecordSessionCacheInvalidation();
            }

            return true;
        }
        catch (Exception exception) when (IsRedisFailure(exception, cancellationToken))
        {
            if (metrics.RecordRedisFailure("session_cache_revoke"))
            {
                logger.LogWarning(
                    exception,
                    "Redis session revocation tombstones could not be stored. The durable revocation must not commit while stale cache entries can remain valid. Repeated warnings are throttled.");
            }

            return false;
        }
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
