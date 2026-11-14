using System.Diagnostics.Metrics;

namespace Mmorpg.GameService.Accounts.PasswordHashing;

internal sealed record PasswordHashingMetricsSnapshot(
    int InFlight,
    int Queued,
    long Hashes,
    long Refusals);

// The password hashing lane: hashes in flight, the queue, how long a hash waited and took, and
// the requests the lane refused with auth_busy.
internal sealed class PasswordHashingMetrics : IDisposable
{
    public const string MeterName = "Mmorpg.GameService.Accounts";

    private readonly Meter meter = new(MeterName, "1.0.0");
    private readonly Histogram<double> waitDuration;
    private readonly Histogram<double> hashDuration;
    private readonly Counter<long> refusalCounter;
    private int inFlight;
    private int queued;
    private long hashes;
    private long refusals;

    public PasswordHashingMetrics()
    {
        meter.CreateObservableGauge(
            "game_service.accounts.password_hashing.in_flight",
            () => Volatile.Read(ref inFlight));
        meter.CreateObservableGauge(
            "game_service.accounts.password_hashing.queue_length",
            () => Volatile.Read(ref queued));
        waitDuration = meter.CreateHistogram<double>(
            "game_service.accounts.password_hashing.wait.duration",
            "ms");
        hashDuration = meter.CreateHistogram<double>(
            "game_service.accounts.password_hashing.duration",
            "ms");
        refusalCounter = meter.CreateCounter<long>(
            "game_service.accounts.password_hashing.refusals");
    }

    public void SetLevels(int hashing, int waiting)
    {
        Volatile.Write(ref inFlight, hashing);
        Volatile.Write(ref queued, waiting);
    }

    public void RecordWait(TimeSpan wait)
    {
        waitDuration.Record(wait.TotalMilliseconds);
    }

    public void RecordHash(TimeSpan duration)
    {
        Interlocked.Increment(ref hashes);
        hashDuration.Record(duration.TotalMilliseconds);
    }

    public void RecordRefusal(string operation)
    {
        Interlocked.Increment(ref refusals);
        refusalCounter.Add(1, new KeyValuePair<string, object?>("operation", operation));
    }

    public PasswordHashingMetricsSnapshot Capture()
    {
        return new PasswordHashingMetricsSnapshot(
            Volatile.Read(ref inFlight),
            Volatile.Read(ref queued),
            Interlocked.Read(ref hashes),
            Interlocked.Read(ref refusals));
    }

    public void Dispose()
    {
        meter.Dispose();
    }
}
