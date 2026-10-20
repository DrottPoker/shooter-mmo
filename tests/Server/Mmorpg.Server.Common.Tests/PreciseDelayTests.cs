using System.Diagnostics;
using Mmorpg.Server.Common.Timing;

namespace Mmorpg.Server.Common.Tests;

public sealed class PreciseDelayTests
{
    // Task.Delay(2) takes at least 15.6 ms on Windows, so a coarse timer never reaches a median
    // below 8 ms. A machine busy with parallel test projects can complete a whole round late,
    // so one round of several must reach it; no delay may ever end early.
    [Fact]
    public async Task RepeatedShortDelaysKeepTheirLength()
    {
        using var delay = new PreciseDelay("precise delay test");
        var medians = new List<double>();

        for (var round = 0; round < 5; round++)
        {
            var delays = new double[21];
            for (var index = 0; index < delays.Length; index++)
            {
                var started = Stopwatch.GetTimestamp();
                await delay.DelayAsync(TimeSpan.FromMilliseconds(2), CancellationToken.None);
                delays[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }

            Array.Sort(delays);
            Assert.True(delays[0] >= 1.9d, $"The shortest 2 ms delay took {delays[0]} ms.");
            medians.Add(delays[delays.Length / 2]);
            if (medians[^1] < 8d)
            {
                return;
            }
        }

        Assert.Fail("The median 2 ms delay took " + string.Join(", ", medians) + " ms in every round.");
    }

    [Fact]
    public async Task CancellationCancelsThePendingDelay()
    {
        using var delay = new PreciseDelay("precise delay test");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => delay.DelayAsync(TimeSpan.FromSeconds(30), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => delay.DelayAsync(TimeSpan.FromMilliseconds(1), cancellation.Token));
    }

    [Fact]
    public async Task OnlyOneDelayCanBePending()
    {
        using var delay = new PreciseDelay("precise delay test");
        var first = delay.DelayAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        // The second request is refused at once, not through its task.
        Assert.Throws<InvalidOperationException>(
            () => { _ = delay.DelayAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None); });
        await first;
        await delay.DelayAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None);
    }
}
