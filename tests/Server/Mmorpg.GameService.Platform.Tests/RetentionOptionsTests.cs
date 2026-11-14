using Microsoft.Extensions.Configuration;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.GameService.Platform.Retention;

namespace Mmorpg.GameService.Platform.Tests;

public sealed class RetentionOptionsTests
{
    // A short window is for soaks and tests: outside Development a retried operation could meet
    // a row that retention already deleted.
    [Fact]
    public void AWindowBelowADayIsRefusedOutsideDevelopment()
    {
        var soak = RetentionOptions.Default with
        {
            ItemHistory = TimeSpan.FromMinutes(4),
            SessionHistory = TimeSpan.FromHours(23)
        };

        Assert.Empty(soak.WindowErrors(isDevelopment: true));
        Assert.Empty(RetentionOptions.Default.WindowErrors(isDevelopment: false));
        Assert.Empty((soak with { ItemHistory = TimeSpan.FromDays(1), SessionHistory = TimeSpan.FromDays(1) })
            .WindowErrors(isDevelopment: false));
        var errors = soak.WindowErrors(isDevelopment: false);
        Assert.Equal(2, errors.Count);
        Assert.Contains("Retention:ItemHistory is 00:04:00", errors[0], StringComparison.Ordinal);
        Assert.Contains("Retention:SessionHistory is 23:00:00", errors[1], StringComparison.Ordinal);
        Assert.All(errors, error => Assert.Contains("at least 1 day", error, StringComparison.Ordinal));
    }

    [Fact]
    public void UnsetKeysKeepTheDefaults()
    {
        var errors = new List<string>();

        var options = RetentionOptions.Read(Settings(new()), errors);

        Assert.Empty(errors);
        Assert.Equal(RetentionOptions.Default, options);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Interval);
        Assert.Equal(1_000, options.BatchSize);
        Assert.Equal(TimeSpan.FromSeconds(48), options.PassBudget);
        Assert.Equal(TimeSpan.FromDays(14), options.WindowOf(RetentionClass.ItemHistory));
        Assert.Equal(TimeSpan.FromDays(14), options.WindowOf(RetentionClass.SessionHistory));
    }

    [Fact]
    public void WindowsAreTimeSpansSoATestOrASoakCanUseMinutes()
    {
        var errors = new List<string>();

        var options = RetentionOptions.Read(
            Settings(new()
            {
                [RetentionOptions.IntervalSecondsKey] = "15",
                [RetentionOptions.BatchSizeKey] = "250",
                [RetentionOptions.PassBudgetPercentKey] = "50",
                [RetentionOptions.ItemHistoryKey] = "00:04:00",
                [RetentionOptions.SessionHistoryKey] = "2.12:00:00"
            }),
            errors);

        Assert.Empty(errors);
        Assert.Equal(TimeSpan.FromSeconds(15), options.Interval);
        Assert.Equal(250, options.BatchSize);
        Assert.Equal(TimeSpan.FromSeconds(7.5), options.PassBudget);
        Assert.Equal(TimeSpan.FromMinutes(4), options.ItemHistory);
        Assert.Equal(TimeSpan.FromHours(60), options.SessionHistory);
    }

    [Theory]
    [InlineData(RetentionOptions.IntervalSecondsKey, "0")]
    [InlineData(RetentionOptions.IntervalSecondsKey, "3601")]
    [InlineData(RetentionOptions.BatchSizeKey, "0")]
    [InlineData(RetentionOptions.BatchSizeKey, "10001")]
    [InlineData(RetentionOptions.PassBudgetPercentKey, "101")]
    [InlineData(RetentionOptions.ItemHistoryKey, "00:02:59")]
    [InlineData(RetentionOptions.ItemHistoryKey, "3651.00:00:00")]
    [InlineData(RetentionOptions.SessionHistoryKey, "two weeks")]
    public void AnInvalidValueIsAnErrorThatNamesItsKey(string key, string value)
    {
        var errors = new List<string>();

        RetentionOptions.Read(Settings(new() { [key] = value }), errors);

        Assert.Contains(key, Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformOptionsCarryTheRetentionSectionAndItsErrors()
    {
        var errors = new List<string>();
        var configuration = Settings(new()
        {
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Database=retention_test;Username=test;Password=test",
            ["ConnectionStrings:Redis"] = "127.0.0.1:6379",
            ["Database:RunMigrationsOnStartup"] = "true",
            ["Database:MaximumPoolSize"] = "10",
            ["HealthChecks:TimeoutMilliseconds"] = "1000",
            [RetentionOptions.ItemHistoryKey] = "00:10:00",
            [RetentionOptions.BatchSizeKey] = "-1"
        });

        var options = PlatformOptions.Read(configuration, errors);

        Assert.Equal(TimeSpan.FromMinutes(10), options.Retention.ItemHistory);
        Assert.Contains(RetentionOptions.BatchSizeKey, Assert.Single(errors), StringComparison.Ordinal);
    }

    private static IConfiguration Settings(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
