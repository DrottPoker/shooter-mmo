namespace Mmorpg.SimulationWorker.Registry;

public sealed class SimulationWorkerRegistrationLease(TimeProvider timeProvider)
{
    private long expiresAtUtcTicks;

    public bool HasLease => Interlocked.Read(ref expiresAtUtcTicks) > 0;

    public bool IsValid => Interlocked.Read(ref expiresAtUtcTicks) > timeProvider.GetUtcNow().UtcTicks;

    public DateTimeOffset ExpiresAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref expiresAtUtcTicks);
            return ticks > 0
                ? new DateTimeOffset(ticks, TimeSpan.Zero)
                : DateTimeOffset.MinValue;
        }
    }

    private static readonly TimeSpan MaximumSafetyMargin = TimeSpan.FromSeconds(1);

    public DateTimeOffset CaptureRenewalStart()
    {
        return timeProvider.GetUtcNow();
    }

    // The database lease starts no earlier than the request was sent, so a local lease
    // measured from renewalStart, minus a small margin, never outlives it.
    public void Renew(DateTimeOffset renewalStart, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var margin = TimeSpan.FromTicks(Math.Min(MaximumSafetyMargin.Ticks, duration.Ticks / 10));
        var expiresAt = renewalStart.Add(duration - margin);
        Interlocked.Exchange(ref expiresAtUtcTicks, expiresAt.UtcTicks);
    }

    public void Clear()
    {
        Interlocked.Exchange(ref expiresAtUtcTicks, 0);
    }
}
