using System.Diagnostics;
using Mmorpg.Server.Common.Timing;

namespace Mmorpg.Server.Common.Tests;

public sealed class PreciseLoopTimerTests
{
    [Fact]
    public void WaitLastsAtLeastTheDelayAndReturnsTrue()
    {
        using var timer = new PreciseLoopTimer();
        var started = Stopwatch.GetTimestamp();

        Assert.True(timer.Wait(TimeSpan.FromMilliseconds(5), CancellationToken.None));

        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(4.5), $"Waited only {elapsed.TotalMilliseconds} ms.");
        // A generous ceiling: a busy test machine may wake the thread late, but not by seconds.
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Waited {elapsed.TotalMilliseconds} ms.");
    }

    [Fact]
    public void ShortWaitsAreNotRoundedUpToTheSystemTimerTick()
    {
        using var timer = new PreciseLoopTimer();
        Assert.True(timer.IsHighResolution);

        // A 2 ms wait takes about 2 ms with a precise timer and at least 15.6 ms when it
        // rounds up to the Windows timer tick. The median ignores the few waits that a busy
        // machine wakes late.
        var waits = new double[21];
        for (var index = 0; index < waits.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            timer.Wait(TimeSpan.FromMilliseconds(2), CancellationToken.None);
            waits[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(waits);
        Assert.True(waits[0] >= 1.9d, $"The shortest 2 ms wait took {waits[0]} ms.");
        Assert.True(waits[waits.Length / 2] < 8d, $"The median 2 ms wait took {waits[waits.Length / 2]} ms.");
    }

    [Fact]
    public void CancellationEndsTheWaitAndReturnsFalse()
    {
        using var timer = new PreciseLoopTimer();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var started = Stopwatch.GetTimestamp();

        Assert.False(timer.Wait(TimeSpan.FromSeconds(30), cancellation.Token));
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
        Assert.False(timer.Wait(TimeSpan.Zero, cancellation.Token));
    }

    [Fact]
    public void NonPositiveDelaysReturnAtOnce()
    {
        using var timer = new PreciseLoopTimer();

        Assert.True(timer.Wait(TimeSpan.Zero, CancellationToken.None));
        Assert.True(timer.Wait(TimeSpan.FromMilliseconds(-5), CancellationToken.None));
    }
}
