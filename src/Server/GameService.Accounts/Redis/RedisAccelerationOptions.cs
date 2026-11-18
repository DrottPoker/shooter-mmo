namespace Mmorpg.GameService.Accounts.Redis;

public sealed record RedisAccelerationOptions(
    bool Enabled,
    string KeyPrefix,
    bool SessionCacheEnabled,
    TimeSpan SessionCacheTtl,
    TimeSpan SessionRevocationTombstoneLifetime,
    bool AuthenticationRateLimitingEnabled,
    TimeSpan MetricsInterval)
{
    public const string SectionName = "RedisAcceleration";
    public const string PoolSizeKey = "Database:MaximumPoolSize";

    // Well below the 5 seconds that a worker gives each call to GameService by default, since a
    // login or logout that waits for Redis holds a pooled connection and the row locks of the
    // account and its sessions for as long, and a worker call that waits for one of them would
    // time out with it. A test pins the relation to the worker's default.
    public const int DefaultSessionRevocationDeadlineMilliseconds = 2_000;

    // How long a login or logout waits for one of the slots below before it is refused.
    public const int DefaultSessionRevocationWaitMilliseconds = 250;

    // The pool size that the share of the default is taken from when none is configured.
    public const int AssumedPoolSize = 64;

    // How long storing the revocation tombstones of one logout or login may take, a retry of a
    // transient failure included.
    public TimeSpan SessionRevocationDeadline { get; init; } =
        TimeSpan.FromMilliseconds(DefaultSessionRevocationDeadlineMilliseconds);

    // How many logins and logouts at most may be between taking their pooled connection and the
    // end of their transaction at once, so that a Redis that stalls, not refuses, cannot hold the
    // whole pool and starve the worker's heartbeat. A quarter of the pool by default, and at most
    // half of it.
    public int SessionRevocationMaxConcurrency { get; init; } = DefaultMaxConcurrency(AssumedPoolSize);

    // How long a login or logout waits for a slot before it is refused with auth_busy.
    public TimeSpan SessionRevocationWait { get; init; } =
        TimeSpan.FromMilliseconds(DefaultSessionRevocationWaitMilliseconds);

    public static int DefaultMaxConcurrency(int poolSize)
    {
        return Math.Max(1, poolSize / 4);
    }

    // The most that the setting may be for a pool: half of it, never less than one.
    public static int MaximumMaxConcurrency(int poolSize)
    {
        return Math.Max(1, poolSize / 2);
    }

    // Redis is never contacted. PostgreSQL alone validates sessions and the in-process
    // limiter alone applies the authentication rate limits.
    public static RedisAccelerationOptions Disabled { get; } = new(
        false,
        "shooter-mmo",
        false,
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
        false,
        TimeSpan.FromSeconds(30));

    public static RedisAccelerationOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var enabled = section.GetValue("Enabled", true);
        var keyPrefix = section.GetValue("KeyPrefix", "shooter-mmo")?.Trim();
        var sessionCacheEnabled = section.GetValue("SessionCacheEnabled", true);
        var sessionCacheTtlSeconds = section.GetValue("SessionCacheTtlSeconds", 60);
        var sessionRevocationTombstoneSeconds = section.GetValue(
            "SessionRevocationTombstoneSeconds",
            120);
        var authenticationRateLimitingEnabled = section.GetValue(
            "AuthenticationRateLimitingEnabled",
            true);
        var metricsIntervalSeconds = section.GetValue("MetricsIntervalSeconds", 30);
        var sessionRevocationDeadlineMilliseconds = section.GetValue(
            "SessionRevocationDeadlineMilliseconds",
            DefaultSessionRevocationDeadlineMilliseconds);
        var poolSize = int.TryParse(
                configuration[PoolSizeKey],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var configuredPoolSize)
            && configuredPoolSize > 0
            ? configuredPoolSize
            : AssumedPoolSize;
        var sessionRevocationMaxConcurrency = section.GetValue(
            "SessionRevocationMaxConcurrency",
            DefaultMaxConcurrency(poolSize));
        var sessionRevocationWaitMilliseconds = section.GetValue(
            "SessionRevocationWaitMilliseconds",
            DefaultSessionRevocationWaitMilliseconds);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(keyPrefix)
            || keyPrefix.Length > 64
            || !keyPrefix.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            errors.Add(
                $"{SectionName}:KeyPrefix must contain 1 to 64 ASCII letters, digits, hyphens, underscores, or periods.");
        }

        if (sessionCacheTtlSeconds is < 1 or > 300)
        {
            errors.Add($"{SectionName}:SessionCacheTtlSeconds must be between 1 and 300.");
        }

        if (sessionRevocationTombstoneSeconds < sessionCacheTtlSeconds
            || sessionRevocationTombstoneSeconds > 3600)
        {
            errors.Add(
                $"{SectionName}:SessionRevocationTombstoneSeconds must be at least SessionCacheTtlSeconds and no more than 3600.");
        }

        if (metricsIntervalSeconds is < 1 or > 3600)
        {
            errors.Add($"{SectionName}:MetricsIntervalSeconds must be between 1 and 3600.");
        }

        if (sessionRevocationDeadlineMilliseconds is < 100 or > 30_000)
        {
            errors.Add($"{SectionName}:SessionRevocationDeadlineMilliseconds must be between 100 and 30000.");
        }

        if (sessionRevocationMaxConcurrency < 1
            || sessionRevocationMaxConcurrency > MaximumMaxConcurrency(poolSize))
        {
            errors.Add(
                $"{SectionName}:SessionRevocationMaxConcurrency must be between 1 and {MaximumMaxConcurrency(poolSize)}, half of {PoolSizeKey} ({poolSize}), so logins and logouts that wait for Redis can never hold the pool that the workers' calls need.");
        }

        if (sessionRevocationWaitMilliseconds is < 0 or > 5_000)
        {
            errors.Add($"{SectionName}:SessionRevocationWaitMilliseconds must be between 0 and 5000.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        return new RedisAccelerationOptions(
            enabled,
            keyPrefix!,
            sessionCacheEnabled,
            TimeSpan.FromSeconds(sessionCacheTtlSeconds),
            TimeSpan.FromSeconds(sessionRevocationTombstoneSeconds),
            authenticationRateLimitingEnabled,
            TimeSpan.FromSeconds(metricsIntervalSeconds))
        {
            SessionRevocationDeadline = TimeSpan.FromMilliseconds(sessionRevocationDeadlineMilliseconds),
            SessionRevocationMaxConcurrency = sessionRevocationMaxConcurrency,
            SessionRevocationWait = TimeSpan.FromMilliseconds(sessionRevocationWaitMilliseconds)
        };
    }
}
