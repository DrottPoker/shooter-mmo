namespace AuthService.Items;

public sealed record ItemOperationsOptions(
    TimeSpan TransactionTimeout,
    TimeSpan LockTimeout,
    int MaximumCommandPayloadBytes,
    long MaximumHttpRequestBodyBytes,
    TimeSpan MetricsInterval,
    TimeSpan MaintenanceInterval,
    int CleanupBatchSize,
    TimeSpan AuditRetention,
    TimeSpan ClosedCorpseRetention)
{
    public static ItemOperationsOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = new List<string>();
        var transactionTimeoutMilliseconds = ReadBoundedInt(
            configuration,
            "Items:Operations:TransactionTimeoutMilliseconds",
            10_000,
            100,
            120_000,
            errors);
        var lockTimeoutMilliseconds = ReadBoundedInt(
            configuration,
            "Items:Operations:LockTimeoutMilliseconds",
            2_000,
            10,
            120_000,
            errors);
        var maximumCommandPayloadBytes = ReadBoundedInt(
            configuration,
            "Items:Operations:MaximumCommandPayloadBytes",
            65_536,
            1_024,
            1_048_576,
            errors);
        var maximumHttpRequestBodyBytes = ReadBoundedInt(
            configuration,
            "Items:Operations:MaximumHttpRequestBodyBytes",
            262_144,
            4_096,
            4_194_304,
            errors);
        var metricsIntervalSeconds = ReadBoundedInt(
            configuration,
            "Items:Operations:MetricsIntervalSeconds",
            30,
            1,
            3_600,
            errors);
        var maintenanceIntervalSeconds = ReadBoundedInt(
            configuration,
            "Items:Operations:MaintenanceIntervalSeconds",
            60,
            1,
            3_600,
            errors);
        var cleanupBatchSize = ReadBoundedInt(
            configuration,
            "Items:Operations:CleanupBatchSize",
            64,
            1,
            1_000,
            errors);
        var auditRetentionDays = ReadBoundedInt(
            configuration,
            "Items:Operations:AuditRetentionDays",
            30,
            1,
            3_650,
            errors);
        var closedCorpseRetentionDays = ReadBoundedInt(
            configuration,
            "Items:Operations:ClosedCorpseRetentionDays",
            30,
            1,
            3_650,
            errors);

        if (lockTimeoutMilliseconds > transactionTimeoutMilliseconds)
        {
            errors.Add(
                "Items:Operations:LockTimeoutMilliseconds cannot exceed TransactionTimeoutMilliseconds.");
        }

        if (closedCorpseRetentionDays < auditRetentionDays)
        {
            errors.Add(
                "Items:Operations:ClosedCorpseRetentionDays cannot be shorter than AuditRetentionDays.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Item operations configuration is invalid:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return new ItemOperationsOptions(
            TimeSpan.FromMilliseconds(transactionTimeoutMilliseconds),
            TimeSpan.FromMilliseconds(lockTimeoutMilliseconds),
            maximumCommandPayloadBytes,
            maximumHttpRequestBodyBytes,
            TimeSpan.FromSeconds(metricsIntervalSeconds),
            TimeSpan.FromSeconds(maintenanceIntervalSeconds),
            cleanupBatchSize,
            TimeSpan.FromDays(auditRetentionDays),
            TimeSpan.FromDays(closedCorpseRetentionDays));
    }

    public static ItemOperationsOptions CreateDefaults()
    {
        return new ItemOperationsOptions(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(2),
            65_536,
            262_144,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            64,
            TimeSpan.FromDays(30),
            TimeSpan.FromDays(30));
    }

    private static int ReadBoundedInt(
        IConfiguration configuration,
        string key,
        int fallback,
        int minimum,
        int maximum,
        ICollection<string> errors)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
        {
            errors.Add($"{key} must be an integer between {minimum} and {maximum}.");
            return fallback;
        }

        return value;
    }
}
