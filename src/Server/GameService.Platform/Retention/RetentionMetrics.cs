using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Mmorpg.GameService.Platform.Audit;

namespace Mmorpg.GameService.Platform.Retention;

// The retention host's metrics, labeled with the task: the rows deleted, the time each task took
// in a pass, the passes that failed by reason, and the lag, how long the oldest due row had been due
// when the task's pass began.
//
// The lag of a task whose pass failed before it measured one is dropped, not kept and not guessed.
// The lag is the age of the oldest due row, which only the batch that failed would have read, so
// the real age is unknown while the task cannot run, and a kept value would show a healthy task
// that is stuck. The failure counter and its alert report the stall until the task runs again.
internal sealed class RetentionMetrics : IDisposable
{
    public const string MeterName = "Mmorpg.GameService.Retention";

    private readonly Meter meter = new(MeterName, "1.0.0");
    private readonly Counter<long> deleted;
    private readonly Counter<long> failures;
    private readonly Histogram<double> duration;
    private readonly ConcurrentDictionary<string, double> lagSeconds = new(StringComparer.Ordinal);

    public RetentionMetrics()
    {
        deleted = meter.CreateCounter<long>("game_service.retention.deleted");
        failures = meter.CreateCounter<long>("game_service.retention.failures");
        duration = meter.CreateHistogram<double>("game_service.retention.duration", "ms");
        meter.CreateObservableGauge("game_service.retention.lag", ObserveLag, "s");
    }

    // failureReason is one of RetentionFailureReasons when the task's batch failed in this pass.
    public void RecordPass(string task, int deletedRows, TimeSpan taskDuration, TimeSpan? lag, string? failureReason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        var tag = new KeyValuePair<string, object?>("task", task);
        if (deletedRows > 0)
        {
            deleted.Add(deletedRows, tag);
        }

        duration.Record(Math.Max(0d, taskDuration.TotalMilliseconds), tag);
        if (failureReason is not null)
        {
            failures.Add(
                1,
                tag,
                new KeyValuePair<string, object?>("reason", failureReason));
        }

        if (lag is { } value)
        {
            lagSeconds[task] = Math.Max(0d, value.TotalSeconds);
        }
        else if (failureReason is not null)
        {
            lagSeconds.TryRemove(task, out _);
        }
    }

    // The latest lag of every task that measured one.
    public IReadOnlyDictionary<string, double> CaptureLag()
    {
        return new Dictionary<string, double>(lagSeconds, StringComparer.Ordinal);
    }

    public void Dispose()
    {
        meter.Dispose();
    }

    private IEnumerable<Measurement<double>> ObserveLag()
    {
        foreach (var entry in lagSeconds)
        {
            yield return new Measurement<double>(
                entry.Value,
                new KeyValuePair<string, object?>("task", entry.Key));
        }
    }
}

// The meters that Platform creates, which the host exports beside every module's.
public static class PlatformMeters
{
    public static IReadOnlyList<string> Names { get; } = [RetentionMetrics.MeterName, AuditMetrics.MeterName];
}
