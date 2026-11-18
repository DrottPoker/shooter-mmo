using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Mmorpg.GameService.Platform.Audit;

namespace Mmorpg.GameService.Platform.Retention;

// The retention host's metrics, labeled with the task: the rows deleted, the time each task took
// in a pass, and the lag, how long the oldest due row had been due when the task's pass began.
internal sealed class RetentionMetrics : IDisposable
{
    public const string MeterName = "Mmorpg.GameService.Retention";

    private readonly Meter meter = new(MeterName, "1.0.0");
    private readonly Counter<long> deleted;
    private readonly Histogram<double> duration;
    private readonly ConcurrentDictionary<string, double> lagSeconds = new(StringComparer.Ordinal);

    public RetentionMetrics()
    {
        deleted = meter.CreateCounter<long>("game_service.retention.deleted");
        duration = meter.CreateHistogram<double>("game_service.retention.duration", "ms");
        meter.CreateObservableGauge("game_service.retention.lag", ObserveLag, "s");
    }

    public void RecordPass(string task, int deletedRows, TimeSpan taskDuration, TimeSpan? lag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        var tag = new KeyValuePair<string, object?>("task", task);
        if (deletedRows > 0)
        {
            deleted.Add(deletedRows, tag);
        }

        duration.Record(Math.Max(0d, taskDuration.TotalMilliseconds), tag);
        if (lag is { } value)
        {
            lagSeconds[task] = Math.Max(0d, value.TotalSeconds);
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
