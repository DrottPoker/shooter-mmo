using SimulationWorker.Registry;

namespace ShooterMmo.Backend.Tests.Unit;

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

        lease.Renew(TimeSpan.FromSeconds(30));

        Assert.True(lease.HasLease);
        Assert.True(lease.IsValid);
        Assert.Equal(timeProvider.GetUtcNow().AddSeconds(30), lease.ExpiresAtUtc);

        timeProvider.Advance(TimeSpan.FromSeconds(30));

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
