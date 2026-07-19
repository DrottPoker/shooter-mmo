using ShooterMmo.Tools.StackStressGenerator;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class StressLatencyAccumulatorTests
{
    [Fact]
    public void BoundedReservoirPreservesExactAggregateStatistics()
    {
        var accumulator = new StressLatencyAccumulator(seed: 1337, reservoirCapacity: 100);
        for (var value = 1; value <= 1_000; value++)
        {
            accumulator.Record(value);
        }

        var summary = accumulator.CaptureSummary();

        Assert.NotNull(summary);
        Assert.Equal(1_000, summary.Samples);
        Assert.Equal(1d, summary.MinimumMs);
        Assert.Equal(1_000d, summary.MaximumMs);
        Assert.Equal(500.5d, summary.AverageMs, 3);
        Assert.InRange(summary.P50Ms, 350d, 650d);
        Assert.InRange(summary.P95Ms, 850d, 1_000d);
    }

    [Fact]
    public void EmptyAccumulatorHasNoSummary()
    {
        var accumulator = new StressLatencyAccumulator(seed: 1);

        Assert.Null(accumulator.CaptureSummary());
    }

    [Fact]
    public void ResetStartsAnIndependentInterval()
    {
        var accumulator = new StressLatencyAccumulator(seed: 1);
        accumulator.Record(10d);
        accumulator.Reset();
        accumulator.Record(20d);

        var summary = accumulator.CaptureSummary();

        Assert.NotNull(summary);
        Assert.Equal(1, summary.Samples);
        Assert.Equal(20d, summary.P95Ms);
        Assert.Equal(20d, summary.AverageMs);
    }
}
