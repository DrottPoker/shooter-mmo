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

    // A window this short only suits a soak. It still outlasts the two minutes of a live Mob corpse,
    // whose loot grants it keeps, and every retry of a worker operation, as
    // docs/GAME_SERVICE_MODULES.md says under Retention.
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(3_650);

    public static RetentionOptions Default { get; } = new(
        TimeSpan.FromSeconds(60),
        1_000,
        80,
        TimeSpan.FromDays(14),
        TimeSpan.FromDays(14));

    // The time a pass may use, so a pass ends before the next one is due.
    public TimeSpan PassBudget => Interval * PassBudgetPercent / 100d;

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
