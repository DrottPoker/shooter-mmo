using System.Diagnostics.Metrics;

namespace SimulationWorker.Corpses;

public sealed record CorpseRuntimeMetricsSnapshot(
    long DurableCorpses,
    long LiveCorpses);

public sealed class CorpseRuntimeMetrics : IDisposable
{
    public const string MeterName = "ShooterMmo.SimulationWorker.Corpses";

    private readonly Meter meter = new(MeterName);
    private long durableCorpses;
    private long liveCorpses;

    public CorpseRuntimeMetrics()
    {
        meter.CreateObservableGauge(
            "simulation_worker.corpses.active",
            ObserveActiveCorpses);
    }

    public void Observe(long durableCount, long liveCount)
    {
        if (durableCount < 0 || liveCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durableCount),
                "Corpse counts cannot be negative.");
        }

        Interlocked.Exchange(ref durableCorpses, durableCount);
        Interlocked.Exchange(ref liveCorpses, liveCount);
    }

    public CorpseRuntimeMetricsSnapshot Capture()
    {
        return new CorpseRuntimeMetricsSnapshot(
            Volatile.Read(ref durableCorpses),
            Volatile.Read(ref liveCorpses));
    }

    public void Dispose()
    {
        meter.Dispose();
    }

    private IEnumerable<Measurement<long>> ObserveActiveCorpses()
    {
        yield return new Measurement<long>(
            Volatile.Read(ref durableCorpses),
            new KeyValuePair<string, object?>("persistence", "durable"));
        yield return new Measurement<long>(
            Volatile.Read(ref liveCorpses),
            new KeyValuePair<string, object?>("persistence", "live"));
    }
}
