using System.Diagnostics.Metrics;
using Mmorpg.GameService.Platform.Retention;

namespace Mmorpg.GameService.Platform.Tests;

// The retention metrics without a database: the failure counter and what the lag gauge keeps.
public sealed class RetentionMetricsTests
{
    [Fact]
    public void AFailedPassCountsByTaskAndReason()
    {
        using var capture = new RetentionMetricsCapture();
        using var metrics = new RetentionMetrics();

        metrics.RecordPass("item_operations", 0, TimeSpan.FromSeconds(1), null, RetentionFailureReasons.LockTimeout);
        metrics.RecordPass("item_operations", 0, TimeSpan.FromSeconds(1), null, RetentionFailureReasons.LockTimeout);
        metrics.RecordPass("item_operations", 0, TimeSpan.FromSeconds(1), null, RetentionFailureReasons.StatementTimeout);
        metrics.RecordPass("account_sessions", 0, TimeSpan.FromSeconds(1), null, RetentionFailureReasons.Error);
        metrics.RecordPass("account_sessions", 3, TimeSpan.FromSeconds(1), TimeSpan.Zero);

        Assert.Equal(
            ["account_sessions/error=1", "item_operations/lock_timeout=2", "item_operations/statement_timeout=1"],
            capture.Failures
                .Select(failure => $"{failure.Key.Task}/{failure.Key.Reason}={failure.Value}")
                .Order(StringComparer.Ordinal));
    }

    // The lag is the age of the oldest due row, which only the failed batch would have read, so a
    // task that failed before it measured one has no lag, instead of the value of an older pass.
    [Fact]
    public void TheLagOfATaskWhoseLastPassFailedIsDroppedNotKept()
    {
        using var capture = new RetentionMetricsCapture();
        using var metrics = new RetentionMetrics();
        metrics.RecordPass("item_operations", 5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(90));
        metrics.RecordPass("account_sessions", 5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(7));
        var before = capture.ObserveLags();

        metrics.RecordPass("item_operations", 0, TimeSpan.FromSeconds(1), null, RetentionFailureReasons.Error);
        var capturedAfterFailure = metrics.CaptureLag();
        var after = capture.ObserveLags();
        metrics.RecordPass("item_operations", 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4));
        var recovered = capture.ObserveLags();

        Assert.Equal(90d, before["item_operations"]);
        Assert.Equal(7d, before["account_sessions"]);
        Assert.False(after.ContainsKey("item_operations"));
        Assert.Equal(7d, after["account_sessions"]);
        Assert.Equal(4d, recovered["item_operations"]);
        Assert.False(capturedAfterFailure.ContainsKey("item_operations"));
    }

    // A pass that failed after its first batch measured the lag has a real age to report.
    [Fact]
    public void ALagThatAFailedPassMeasuredIsReported()
    {
        using var capture = new RetentionMetricsCapture();
        using var metrics = new RetentionMetrics();

        metrics.RecordPass("item_operations", 1_000, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), RetentionFailureReasons.Error);

        Assert.Equal(30d, capture.ObserveLags()["item_operations"]);
    }

    // The server ends a slow statement a margin before the client gives up on its command.
    [Theory]
    [InlineData(30, 25)]
    [InlineData(120, 30)]
    [InlineData(10, 5)]
    [InlineData(6, 1)]
    [InlineData(2, 1)]
    [InlineData(0, 30)]
    public void TheStatementTimeoutEndsABatchBeforeTheClientsCommandTimeout(int clientSeconds, int statementSeconds)
    {
        var statement = RetentionRunner.StatementTimeoutFor(clientSeconds);

        Assert.Equal(TimeSpan.FromSeconds(statementSeconds), statement);
        Assert.True(clientSeconds <= 0 || statement < TimeSpan.FromSeconds(clientSeconds));
    }
}

// Listens to the retention meter: the failure counter by task and reason, and the lag gauge.
internal sealed class RetentionMetricsCapture : IDisposable
{
    private readonly MeterListener listener = new();
    private readonly Dictionary<(string Task, string Reason), long> failures = [];
    private readonly Dictionary<string, double> lags = new(StringComparer.Ordinal);

    public RetentionMetricsCapture()
    {
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == RetentionMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "game_service.retention.failures")
            {
                return;
            }

            var task = (string)tags.ToArray().Single(tag => tag.Key == "task").Value!;
            var reason = (string)tags.ToArray().Single(tag => tag.Key == "reason").Value!;
            lock (failures)
            {
                failures[(task, reason)] = failures.GetValueOrDefault((task, reason)) + value;
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "game_service.retention.lag")
            {
                lock (lags)
                {
                    lags[(string)tags.ToArray().Single(tag => tag.Key == "task").Value!] = value;
                }
            }
        });
        listener.Start();
    }

    // The failures counted so far.
    public IReadOnlyDictionary<(string Task, string Reason), long> Failures
    {
        get
        {
            lock (failures)
            {
                return new Dictionary<(string Task, string Reason), long>(failures);
            }
        }
    }

    // The lag series that the gauge reports now: one observation of every series.
    public IReadOnlyDictionary<string, double> ObserveLags()
    {
        lock (lags)
        {
            lags.Clear();
        }

        listener.RecordObservableInstruments();
        lock (lags)
        {
            return new Dictionary<string, double>(lags, StringComparer.Ordinal);
        }
    }

    public void Dispose()
    {
        listener.Dispose();
    }
}
