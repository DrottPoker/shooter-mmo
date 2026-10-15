using Mmorpg.SimulationWorker.Registry;

namespace Mmorpg.Backend.Tests.Unit;

public sealed class SimulationWorkerRegistrationLeaseTests
{
    [Fact]
    public void RenewExpireAndClearUseMonotonicServerLeaseDuration()
    {
        var timeProvider = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero));
        var lease = new SimulationWorkerRegistrationLease(timeProvider);

        Assert.False(lease.HasLease);
        Assert.False(lease.IsValid);

        // The request was sent 3 seconds before the response arrived.
        var renewalStart = lease.CaptureRenewalStart();
        timeProvider.Advance(TimeSpan.FromSeconds(3));
        lease.Renew(renewalStart, TimeSpan.FromSeconds(30));

        Assert.True(lease.HasLease);
        Assert.True(lease.IsValid);
        // The lease runs from the send time, minus a one second safety margin, so it can
        // never outlive the database lease that started after the request was sent.
        Assert.Equal(renewalStart.AddSeconds(29), lease.ExpiresAtUtc);

        timeProvider.Advance(TimeSpan.FromSeconds(26));

        Assert.True(lease.HasLease);
        Assert.False(lease.IsValid);

        lease.Clear();

        Assert.False(lease.HasLease);
        Assert.Equal(DateTimeOffset.MinValue, lease.ExpiresAtUtc);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration)
        {
            utcNow = utcNow.Add(duration);
        }
    }
}
