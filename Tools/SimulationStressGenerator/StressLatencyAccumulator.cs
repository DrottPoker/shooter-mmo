namespace ShooterMmo.Tools.SimulationStressGenerator;

public sealed class StressLatencyAccumulator
{
    public const int DefaultReservoirCapacity = 100_000;

    private readonly double[] reservoir;
    private readonly Random random;
    private long samples;
    private double totalMilliseconds;
    private double minimumMilliseconds = double.PositiveInfinity;
    private double maximumMilliseconds;

    public StressLatencyAccumulator(
        int seed,
        int reservoirCapacity = DefaultReservoirCapacity)
    {
        if (reservoirCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reservoirCapacity));
        }

        reservoir = new double[reservoirCapacity];
        random = new Random(seed);
    }

    public void Record(double milliseconds)
    {
        if (double.IsNaN(milliseconds)
            || double.IsInfinity(milliseconds)
            || milliseconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        }

        samples++;
        totalMilliseconds += milliseconds;
        minimumMilliseconds = Math.Min(minimumMilliseconds, milliseconds);
        maximumMilliseconds = Math.Max(maximumMilliseconds, milliseconds);
        if (samples <= reservoir.Length)
        {
            reservoir[samples - 1] = milliseconds;
            return;
        }

        var replacementIndex = random.NextInt64(samples);
        if (replacementIndex < reservoir.Length)
        {
            reservoir[replacementIndex] = milliseconds;
        }
    }

    public StressLatencySummary? CaptureSummary()
    {
        if (samples == 0)
        {
            return null;
        }

        var reservoirCount = (int)Math.Min(samples, reservoir.Length);
        var sortedSample = reservoir.AsSpan(0, reservoirCount).ToArray();
        Array.Sort(sortedSample);
        return StressLatencySummary.CreateFromSample(
            samples,
            minimumMilliseconds,
            maximumMilliseconds,
            totalMilliseconds / samples,
            sortedSample);
    }

    public void Reset()
    {
        samples = 0;
        totalMilliseconds = 0d;
        minimumMilliseconds = double.PositiveInfinity;
        maximumMilliseconds = 0d;
    }
}
