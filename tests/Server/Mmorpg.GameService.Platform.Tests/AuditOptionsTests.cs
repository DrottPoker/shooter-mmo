using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Mmorpg.GameService.Platform.Audit;

namespace Mmorpg.GameService.Platform.Tests;

public sealed class AuditOptionsTests
{
    // The scheduled audit stays off while a developer works and runs everywhere else.
    [Fact]
    public void TheScheduleIsOffInDevelopmentAndOnElsewhereUnlessSet()
    {
        var unset = Settings(new());

        var development = AuditOptions.FromConfiguration(unset, isDevelopment: true);
        var production = AuditOptions.FromConfiguration(unset, isDevelopment: false);

        Assert.False(development.Enabled);
        Assert.True(production.Enabled);
        Assert.Equal(AuditOptions.Default with { Enabled = false }, development);
        Assert.Equal(AuditOptions.Default, production);
        Assert.Equal(TimeSpan.FromHours(1), production.Interval);
        Assert.Equal(TimeSpan.FromSeconds(60), production.StatementTimeout);
        Assert.Equal(5, production.ExampleLimit);
        Assert.Equal(TimeSpan.FromMinutes(5), production.PendingOperationAge);
        Assert.True(AuditOptions.FromConfiguration(
            Settings(new() { [AuditOptions.EnabledKey] = "true" }),
            isDevelopment: true).Enabled);
        Assert.False(AuditOptions.FromConfiguration(
            Settings(new() { [AuditOptions.EnabledKey] = "false" }),
            isDevelopment: false).Enabled);
    }

    [Fact]
    public void EverySettingIsReadAndBounded()
    {
        var options = AuditOptions.FromConfiguration(
            Settings(new()
            {
                [AuditOptions.IntervalSecondsKey] = "600",
                [AuditOptions.StatementTimeoutSecondsKey] = "30",
                [AuditOptions.ExampleLimitKey] = "0",
                [AuditOptions.PendingOperationSecondsKey] = "120"
            }),
            isDevelopment: false);

        Assert.Equal(new AuditOptions(true, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30), 0, TimeSpan.FromMinutes(2)), options);

        var exception = Assert.Throws<InvalidOperationException>(() => AuditOptions.FromConfiguration(
            Settings(new()
            {
                [AuditOptions.EnabledKey] = "sometimes",
                [AuditOptions.IntervalSecondsKey] = "59",
                [AuditOptions.StatementTimeoutSecondsKey] = "0",
                [AuditOptions.ExampleLimitKey] = "101",
                [AuditOptions.PendingOperationSecondsKey] = "-1"
            }),
            isDevelopment: false));

        Assert.Contains("Audit:Enabled must be true or false.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Audit:IntervalSeconds must be an integer from 60 to 86400.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Audit:StatementTimeoutSeconds must be an integer from 1 to 3600.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Audit:ExampleLimit must be an integer from 0 to 100.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Audit:PendingOperationSeconds must be an integer from 0 to 86400.", exception.Message, StringComparison.Ordinal);
    }

    // The metrics keep the latest run: its time, its duration, its outcome, and each check's count.
    // A check that could not run keeps the count of its latest run that did.
    [Fact]
    public void TheMetricsKeepTheLatestRunOfEveryCheck()
    {
        var measurements = new Dictionary<string, double>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == AuditMetrics.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            measurements[instrument.Name] = value);
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var key = instrument.Name + "/" + tags[0].Value;
            measurements[key] = instrument is Counter<long>
                ? measurements.GetValueOrDefault(key) + value
                : value;
        });
        listener.Start();
        using var metrics = new AuditMetrics();
        var finished = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

        metrics.RecordRun(Report(("custody", 2, null), ("ledger", 0, null)), finished);
        metrics.RecordRun(Report(("custody", 0, "timeout"), ("ledger", 1, null)), finished.AddHours(1));
        listener.RecordObservableInstruments();

        Assert.Equal(finished.AddHours(1).ToUnixTimeSeconds(), measurements["game_service.audit.last_run_timestamp"]);
        Assert.Equal(0.25, measurements["game_service.audit.last_run_duration"]);
        Assert.Equal(2, measurements["game_service.audit.violations/custody"]);
        Assert.Equal(1, measurements["game_service.audit.violations/ledger"]);
        Assert.Equal(2, measurements["game_service.audit.runs/violations"]);
        Assert.Equal(new Dictionary<string, long> { ["custody"] = 2, ["ledger"] = 1 }, metrics.CaptureViolations());
    }

    private static AuditReport Report(params (string Id, long Violations, string? Error)[] checks)
    {
        return new AuditReport(
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMilliseconds(250),
            checks.Select(check => new AuditCheckReport(
                check.Id,
                "A check.",
                10,
                check.Violations,
                [],
                TimeSpan.Zero,
                check.Error)).ToArray(),
            null);
    }

    private static IConfiguration Settings(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
