using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace AuthService.Redis;

public sealed record RedisRateLimitDecision(
    bool Available,
    bool Allowed,
    TimeSpan RetryAfter);

public sealed class RedisAuthenticationRateLimiter(
    IRedisConnectionProvider connectionProvider,
    RedisAccelerationOptions options,
    RedisAccelerationMetrics metrics,
    TimeProvider timeProvider,
    ILogger<RedisAuthenticationRateLimiter> logger)
{
    private const string FixedWindowScript =
        "local count = redis.call('INCR', KEYS[1]); "
        + "if count == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]); end; "
        + "local ttl = redis.call('PTTL', KEYS[1]); "
        + "return {count, ttl};";

    public async ValueTask<RedisRateLimitDecision> AcquireAsync(
        string policyName,
        string remoteAddress,
        int permitLimit,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteAddress);
        if (!options.Enabled || !options.AuthenticationRateLimitingEnabled)
        {
            return new RedisRateLimitDecision(false, true, TimeSpan.Zero);
        }

        var windowMilliseconds = checked((long)window.TotalMilliseconds);
        var bucket = timeProvider.GetUtcNow().ToUnixTimeMilliseconds() / windowMilliseconds;
        var addressHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(remoteAddress)));
        var key = (RedisKey)$"{options.KeyPrefix}:rate-limit:{policyName}:{addressHash}:{bucket}";

        try
        {
            var database = await connectionProvider.GetDatabaseAsync(cancellationToken);
            var raw = await database.ScriptEvaluateAsync(
                    FixedWindowScript,
                    [key],
                    [windowMilliseconds.ToString(CultureInfo.InvariantCulture)])
                .WaitAsync(cancellationToken);
            var values = (RedisResult[]?)raw;
            if (values is null || values.Length != 2)
            {
                throw new RedisServerException("Redis returned an invalid rate-limit result.");
            }

            var count = (long)values[0];
            var ttlMilliseconds = Math.Max(0L, (long)values[1]);
            if (count <= permitLimit)
            {
                metrics.RecordDistributedRateLimitAllowed();
                return new RedisRateLimitDecision(true, true, TimeSpan.Zero);
            }

            metrics.RecordDistributedRateLimitRejected();
            return new RedisRateLimitDecision(
                true,
                false,
                TimeSpan.FromMilliseconds(ttlMilliseconds));
        }
        catch (Exception exception) when (IsRedisFailure(exception, cancellationToken))
        {
            var shouldLog = metrics.RecordRedisFailure("authentication_rate_limit");
            metrics.RecordDistributedRateLimitFallback();
            if (shouldLog)
            {
                logger.LogWarning(
                    exception,
                    "The distributed authentication rate limiter is unavailable. The process-local limiter remains active. Repeated warnings are throttled.");
            }

            return new RedisRateLimitDecision(false, true, TimeSpan.Zero);
        }
    }

    private static bool IsRedisFailure(
        Exception exception,
        CancellationToken cancellationToken)
    {
        return exception is RedisException or TimeoutException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);
    }
}
