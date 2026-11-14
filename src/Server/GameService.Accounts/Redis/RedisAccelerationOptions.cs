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
    public const int DefaultSessionRevocationDeadlineMilliseconds = 5_000;

    // How long storing the revocation tombstones of one logout or login may take, a retry of a
    // transient failure included.
    public TimeSpan SessionRevocationDeadline { get; init; } =
        TimeSpan.FromMilliseconds(DefaultSessionRevocationDeadlineMilliseconds);

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
            SessionRevocationDeadline = TimeSpan.FromMilliseconds(sessionRevocationDeadlineMilliseconds)
        };
    }
}
