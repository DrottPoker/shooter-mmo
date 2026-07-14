namespace SimulationWorker.Registry;

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

    public void Renew(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var expiresAt = timeProvider.GetUtcNow().Add(duration);
        Interlocked.Exchange(ref expiresAtUtcTicks, expiresAt.UtcTicks);
    }

    public void Clear()
    {
        Interlocked.Exchange(ref expiresAtUtcTicks, 0);
    }
}
