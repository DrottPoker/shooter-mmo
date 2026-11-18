using Mmorpg.SimulationWorker.Registry;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class SimulationWorkerRegistrationLeaseTests
{
    [Fact]
    public void RenewExpireAndClearUseTheDurationThatGameServiceGrants()
    {
        var time = new SkewableTimeProvider(new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero));
        var lease = new SimulationWorkerRegistrationLease(time);

        Assert.False(lease.HasLease);
        Assert.False(lease.IsValid);
        Assert.Equal(TimeSpan.Zero, lease.Remaining);

        // The request was sent 3 seconds before the response arrived.
        var renewalStart = lease.CaptureRenewalStart();
        time.Advance(TimeSpan.FromSeconds(3));
        lease.Renew(renewalStart, TimeSpan.FromSeconds(30));

        Assert.True(lease.HasLease);
        Assert.True(lease.IsValid);
        // The lease runs from the send time, minus a one second safety margin, so it can never
        // outlive the database lease that started after the request was sent: 29 s from the send,
        // 26 s from now.
        Assert.Equal(TimeSpan.FromSeconds(26), lease.Remaining);

        time.Advance(TimeSpan.FromSeconds(25));
        Assert.True(lease.IsValid);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(lease.HasLease);
        Assert.False(lease.IsValid);
        Assert.Equal(TimeSpan.Zero, lease.Remaining);

        lease.Renew(lease.CaptureRenewalStart(), TimeSpan.FromSeconds(30));
        Assert.True(lease.IsValid);

        lease.Clear();

        Assert.False(lease.HasLease);
        Assert.False(lease.IsValid);
    }

    // The wall clock of the host may step: a VM that resumes, a manual correction, or a time service.
    // The lease counts the monotonic timestamps, so a step forward does not end it and a step
    // backward does not extend it past what GameService honours.
    [Fact]
    public void AStepOfTheWallClockNeitherEndsNorExtendsTheLease()
    {
        var time = new SkewableTimeProvider(new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero));
        var lease = new SimulationWorkerRegistrationLease(time);
        lease.Renew(lease.CaptureRenewalStart(), TimeSpan.FromSeconds(30));

        time.StepWallClock(TimeSpan.FromHours(3));

        Assert.True(lease.IsValid, "A step forward of the wall clock dropped the lease.");
        Assert.Equal(TimeSpan.FromSeconds(29), lease.Remaining);

        time.Advance(TimeSpan.FromSeconds(28));
        time.StepWallClock(TimeSpan.FromDays(-1));

        Assert.True(lease.IsValid);
        Assert.Equal(TimeSpan.FromSeconds(1), lease.Remaining);

        time.Advance(TimeSpan.FromSeconds(1));
        time.StepWallClock(TimeSpan.FromDays(-30));

        Assert.False(lease.IsValid, "A step backward of the wall clock extended the lease.");
    }

    // The margin is a tenth of a lease that is shorter than ten seconds, never more than a second.
    [Theory]
    [InlineData(30, 29d)]
    [InlineData(10, 9d)]
    [InlineData(5, 4.5d)]
    public void TheSafetyMarginIsAtMostASecondAndAtMostATenthOfTheLease(int seconds, double expected)
    {
        var time = new SkewableTimeProvider(DateTimeOffset.UnixEpoch);
        var lease = new SimulationWorkerRegistrationLease(time);

        lease.Renew(lease.CaptureRenewalStart(), TimeSpan.FromSeconds(seconds));

        Assert.Equal(TimeSpan.FromSeconds(expected), lease.Remaining);
    }

    [Fact]
    public void ALeaseNeedsAPositiveDuration()
    {
        var lease = new SimulationWorkerRegistrationLease(TimeProvider.System);

        Assert.Throws<ArgumentOutOfRangeException>(() => lease.Renew(lease.CaptureRenewalStart(), TimeSpan.Zero));
    }

    // A clock whose timestamps move only when advanced, and whose wall clock can also step on its
    // own, as a host clock does.
    internal sealed class SkewableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private long utcNowTicks = utcNow.UtcTicks;
        private long elapsedTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        // Time passes on both clocks.
        public void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref utcNowTicks, duration.Ticks);
            Interlocked.Add(ref elapsedTicks, duration.Ticks);
        }

        // Only the wall clock moves, as when a host clock is corrected.
        public void StepWallClock(TimeSpan step)
        {
            Interlocked.Add(ref utcNowTicks, step.Ticks);
        }

        public override DateTimeOffset GetUtcNow()
        {
            return new DateTimeOffset(Interlocked.Read(ref utcNowTicks), TimeSpan.Zero);
        }

        public override long GetTimestamp()
        {
            return TimeSpan.TicksPerDay + Interlocked.Read(ref elapsedTicks);
        }
    }
}
