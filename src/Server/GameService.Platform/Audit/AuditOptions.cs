using System.Globalization;

namespace Mmorpg.GameService.Platform.Audit;

// The Audit section: whether the service runs the economy audit on a schedule and how often,
// the statement timeout that bounds every check, how many example rows a violated check
// reports, and how long an item operation may stay pending before the audit reports it.
public sealed record AuditOptions(
    bool Enabled,
    TimeSpan Interval,
    TimeSpan StatementTimeout,
    int ExampleLimit,
    TimeSpan PendingOperationAge)
{
    public const string EnabledKey = "Audit:Enabled";
    public const string IntervalSecondsKey = "Audit:IntervalSeconds";
    public const string StatementTimeoutSecondsKey = "Audit:StatementTimeoutSeconds";
    public const string ExampleLimitKey = "Audit:ExampleLimit";
    public const string PendingOperationSecondsKey = "Audit:PendingOperationSeconds";

    public static AuditOptions Default { get; } = new(
        true,
        TimeSpan.FromHours(1),
        TimeSpan.FromSeconds(60),
        5,
        TimeSpan.FromMinutes(5));

    // The scheduled audit is off by default in the Development environment and on elsewhere;
    // Audit:Enabled decides in both.
    public static AuditOptions FromConfiguration(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = new List<string>();
        var enabled = !isDevelopment;
        var rawEnabled = configuration[EnabledKey];
        if (!string.IsNullOrWhiteSpace(rawEnabled) && !bool.TryParse(rawEnabled, out enabled))
        {
            errors.Add($"{EnabledKey} must be true or false.");
        }

        var options = new AuditOptions(
            enabled,
            TimeSpan.FromSeconds(ReadInt(configuration, IntervalSecondsKey, 3_600, 60, 86_400, errors)),
            TimeSpan.FromSeconds(ReadInt(configuration, StatementTimeoutSecondsKey, 60, 1, 3_600, errors)),
            ReadInt(configuration, ExampleLimitKey, Default.ExampleLimit, 0, 100, errors),
            TimeSpan.FromSeconds(ReadInt(configuration, PendingOperationSecondsKey, 300, 0, 86_400, errors)));
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Audit configuration is invalid:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return options;
    }

    private static int ReadInt(
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

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < minimum
            || value > maximum)
        {
            errors.Add($"{key} must be an integer from {minimum} to {maximum}.");
            return fallback;
        }

        return value;
    }
}
