using System.Globalization;

namespace Mmorpg.GameService.Platform.Retention;

// The Retention section: how often the retention host runs a pass, how many rows one batch deletes,
// how much of the interval a pass may use, and how long each class of rows stays after it ended.
public sealed record RetentionOptions(
    TimeSpan Interval,
    int BatchSize,
    int PassBudgetPercent,
    TimeSpan ItemHistory,
    TimeSpan SessionHistory)
{
    public const string IntervalSecondsKey = "Retention:IntervalSeconds";
    public const string BatchSizeKey = "Retention:BatchSize";
    public const string PassBudgetPercentKey = "Retention:PassBudgetPercent";
    public const string ItemHistoryKey = "Retention:ItemHistory";
    public const string SessionHistoryKey = "Retention:SessionHistory";

    // A window this short only suits a soak or a test. It still outlasts the two minutes of a live
    // Mob corpse, whose loot grants it keeps.
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(3_650);

    // The shortest window outside the Development environment. A row of history is also what
    // answers a retried operation, and a worker retries an uncertain operation for as long as its
    // session lives, so a window must be far longer than any outage that a session survives.
    public static readonly TimeSpan MinimumWindowOutsideDevelopment = TimeSpan.FromDays(1);

    public static RetentionOptions Default { get; } = new(
        TimeSpan.FromSeconds(60),
        1_000,
        80,
        TimeSpan.FromDays(14),
        TimeSpan.FromDays(14));

    // The time a pass may use, so a pass ends before the next one is due.
    public TimeSpan PassBudget => Interval * PassBudgetPercent / 100d;

    // The errors of windows that only the Development environment may use.
    public IReadOnlyList<string> WindowErrors(bool isDevelopment)
    {
        if (isDevelopment)
        {
            return [];
        }

        var errors = new List<string>();
        foreach (var (key, window) in new[] { (ItemHistoryKey, ItemHistory), (SessionHistoryKey, SessionHistory) })
        {
            if (window < MinimumWindowOutsideDevelopment)
            {
                errors.Add(
                    $"{key} is {window:c}; outside the Development environment it must be at least {MinimumWindowOutsideDevelopment.TotalDays:0} day, because its rows also answer retried operations.");
            }
        }

        return errors;
    }

    public TimeSpan WindowOf(RetentionClass retentionClass)
    {
        return retentionClass switch
        {
            RetentionClass.ItemHistory => ItemHistory,
            RetentionClass.SessionHistory => SessionHistory,
            _ => throw new ArgumentOutOfRangeException(nameof(retentionClass), retentionClass, "Unknown retention class.")
        };
    }

    public static RetentionOptions Read(IConfiguration configuration, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(errors);

        return new RetentionOptions(
            TimeSpan.FromSeconds(ReadInt(configuration, IntervalSecondsKey, 60, 1, 3_600, errors)),
            ReadInt(configuration, BatchSizeKey, Default.BatchSize, 1, 10_000, errors),
            ReadInt(configuration, PassBudgetPercentKey, Default.PassBudgetPercent, 1, 100, errors),
            ReadWindow(configuration, ItemHistoryKey, Default.ItemHistory, errors),
            ReadWindow(configuration, SessionHistoryKey, Default.SessionHistory, errors));
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

    private static TimeSpan ReadWindow(
        IConfiguration configuration,
        string key,
        TimeSpan fallback,
        ICollection<string> errors)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            || value < MinimumWindow
            || value > MaximumWindow)
        {
            errors.Add(
                $"{key} must be a time span such as 14.00:00:00 from {MinimumWindow:c} to {MaximumWindow.TotalDays:0} days.");
            return fallback;
        }

        return value;
    }
}
