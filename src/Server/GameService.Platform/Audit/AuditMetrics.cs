using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Mmorpg.GameService.Platform.Audit;

// The economy audit's metrics: when the last run took its snapshot, how long it took, the runs by
// outcome, and the violations that each check found in its latest run. A check that could not run
// keeps the count of its latest run that did.
internal sealed class AuditMetrics : IDisposable
{
    public const string MeterName = "Mmorpg.GameService.Audit";

    private readonly Meter meter = new(MeterName, "1.0.0");
    private readonly Counter<long> runs;
    private readonly ConcurrentDictionary<string, long> violations = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private double? lastRunTimestampSeconds;
    private double? lastRunDurationSeconds;

    public AuditMetrics()
    {
        runs = meter.CreateCounter<long>("game_service.audit.runs");
        meter.CreateObservableGauge("game_service.audit.last_run_timestamp", ObserveLastRun, "s");
        meter.CreateObservableGauge("game_service.audit.last_run_duration", ObserveLastDuration, "s");
        meter.CreateObservableGauge("game_service.audit.violations", ObserveViolations);
    }

    public void RecordRun(AuditReport report, DateTimeOffset finishedAt)
    {
        ArgumentNullException.ThrowIfNull(report);
        runs.Add(1, new KeyValuePair<string, object?>("outcome", AuditReportWriter.OutcomeName(report.Outcome)));
        foreach (var check in report.Checks.Where(check => check.Ran))
        {
            violations[check.Id] = check.Violations;
        }

        lock (gate)
        {
            lastRunTimestampSeconds = finishedAt.ToUnixTimeMilliseconds() / 1000d;
            lastRunDurationSeconds = report.Duration.TotalSeconds;
        }
    }

    // The violations of every check by its latest run that ran.
    public IReadOnlyDictionary<string, long> CaptureViolations()
    {
        return new Dictionary<string, long>(violations, StringComparer.Ordinal);
    }

    public void Dispose()
    {
        meter.Dispose();
    }

    private IEnumerable<Measurement<double>> ObserveLastRun()
    {
        lock (gate)
        {
            return lastRunTimestampSeconds is { } value ? [new Measurement<double>(value)] : [];
        }
    }

    private IEnumerable<Measurement<double>> ObserveLastDuration()
    {
        lock (gate)
        {
            return lastRunDurationSeconds is { } value ? [new Measurement<double>(value)] : [];
        }
    }

    private IEnumerable<Measurement<long>> ObserveViolations()
    {
        foreach (var entry in violations)
        {
            yield return new Measurement<long>(
                entry.Value,
                new KeyValuePair<string, object?>("check", entry.Key));
        }
    }
}
