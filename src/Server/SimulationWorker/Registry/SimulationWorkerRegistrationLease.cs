namespace Mmorpg.SimulationWorker.Registry;

// The worker's local view of its registry lease. The deadline is a monotonic timestamp of the
// time provider plus the duration that GameService granted, never a wall clock time, so a step of
// the host clock, a VM resume or a manual correction, neither ends the lease early nor extends it
// past what GameService honours, as GameServiceAvailability and the outage monitor already count.
public sealed class SimulationWorkerRegistrationLease(TimeProvider timeProvider)
{
    public static readonly TimeSpan MaximumSafetyMargin = TimeSpan.FromSeconds(1);

    // The time provider's timestamp at which the lease ends; 0 while there is none.
    private long expiresAtTimestamp;

    public bool HasLease => Interlocked.Read(ref expiresAtTimestamp) != 0;

    public bool IsValid
    {
        get
        {
            var expires = Interlocked.Read(ref expiresAtTimestamp);
            return expires != 0 && timeProvider.GetTimestamp() < expires;
        }
    }

    // How long the lease still holds; zero when there is none or it ended.
    public TimeSpan Remaining
    {
        get
        {
            var expires = Interlocked.Read(ref expiresAtTimestamp);
            if (expires == 0)
            {
                return TimeSpan.Zero;
            }

            var remaining = timeProvider.GetElapsedTime(timeProvider.GetTimestamp(), expires);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    // The timestamp that a renewal counts from: taken before the request is sent.
    public long CaptureRenewalStart()
    {
        return timeProvider.GetTimestamp();
    }

    // The database lease starts no earlier than the request was sent, so a local lease measured
    // from the send timestamp, minus a small margin, never outlives it.
    public void Renew(long renewalStartTimestamp, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var margin = TimeSpan.FromTicks(Math.Min(MaximumSafetyMargin.Ticks, duration.Ticks / 10));
        var lifetime = duration - margin;
        var lifetimeTimestamps = (long)(lifetime.Ticks
            * ((double)timeProvider.TimestampFrequency / TimeSpan.TicksPerSecond));
        // Zero marks "no lease", so a deadline never lands on it.
        Interlocked.Exchange(ref expiresAtTimestamp, Math.Max(1, renewalStartTimestamp + lifetimeTimestamps));
    }

    public void Clear()
    {
        Interlocked.Exchange(ref expiresAtTimestamp, 0);
    }
}
