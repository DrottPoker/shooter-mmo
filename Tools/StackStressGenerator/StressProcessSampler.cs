using System.Diagnostics;

namespace ShooterMmo.Tools.StackStressGenerator;

public sealed record StressProcessSummary(
    int ProcessId,
    string ProcessName,
    int Samples,
    double AverageSingleCoreCpuPercent,
    double MaximumSingleCoreCpuPercent,
    double AverageMachineCpuPercent,
    double MaximumMachineCpuPercent,
    long InitialWorkingSetBytes,
    long AverageWorkingSetBytes,
    long MaximumWorkingSetBytes,
    long FinalWorkingSetBytes,
    long InitialPrivateMemoryBytes,
    long AveragePrivateMemoryBytes,
    long MaximumPrivateMemoryBytes,
    long FinalPrivateMemoryBytes,
    int SteadyStateSamples,
    long SteadyStateInitialWorkingSetBytes,
    long SteadyStateFinalWorkingSetBytes,
    long SteadyStateMaximumWorkingSetBytes,
    long SteadyStateFirstWindowAverageWorkingSetBytes,
    long SteadyStateLastWindowAverageWorkingSetBytes,
    double SteadyStateWorkingSetTrendBytesPerMinute,
    long SteadyStateInitialPrivateMemoryBytes,
    long SteadyStateFinalPrivateMemoryBytes,
    long SteadyStateMaximumPrivateMemoryBytes,
    long SteadyStateFirstWindowAveragePrivateMemoryBytes,
    long SteadyStateLastWindowAveragePrivateMemoryBytes,
    double SteadyStatePrivateMemoryTrendBytesPerMinute,
    int MaximumThreadCount);

public sealed class StressProcessSampler : IDisposable
{
    private readonly Process process;
    private readonly List<ProcessSample> samples = [];
    private TimeSpan previousProcessorTime;
    private long previousTimestamp;
    private bool hasBaseline;
    private int? steadyStateStartIndex;

    private StressProcessSampler(Process process)
    {
        this.process = process;
    }

    public static StressProcessSampler AttachCurrent()
    {
        return new StressProcessSampler(Process.GetCurrentProcess());
    }

    public static StressProcessSampler? TryAttach(DateTime workerStartedAt)
    {
        return TryAttach("SimulationWorker", workerStartedAt);
    }

    public static StressProcessSampler? TryAttach(
        string processName,
        DateTime? expectedStartedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        var candidates = Process.GetProcessesByName(processName)
            .Where(process => !process.HasExited)
            .ToArray();
        var selected = expectedStartedAt.HasValue
            ? candidates
                .Select(process => new
                {
                    Process = process,
                    StartDistance = GetStartDistance(process, expectedStartedAt.Value)
                })
                .Where(candidate => candidate.StartDistance <= TimeSpan.FromMinutes(2))
                .OrderBy(candidate => candidate.StartDistance)
                .Select(candidate => candidate.Process)
                .FirstOrDefault()
            : candidates.Length == 1
                ? candidates[0]
                : null;
        if (selected is null)
        {
            foreach (var candidate in candidates)
            {
                candidate.Dispose();
            }

            Console.WriteLine(
                candidates.Length == 0
                    ? $"{processName} process metrics are unavailable because no native process was found."
                    : expectedStartedAt.HasValue
                        ? $"{processName} process metrics are unavailable because no native process matched the expected start time."
                        : $"{processName} process metrics are unavailable because multiple native processes are running.");
            return null;
        }

        foreach (var candidate in candidates.Where(candidate => candidate.Id != selected.Id))
        {
            candidate.Dispose();
        }

        Console.WriteLine($"Monitoring {processName} process {selected.Id}.");
        return new StressProcessSampler(selected);
    }

    private static TimeSpan GetStartDistance(Process process, DateTime workerStartedAt)
    {
        try
        {
            return (process.StartTime.ToUniversalTime() - workerStartedAt).Duration();
        }
        catch (InvalidOperationException)
        {
            return TimeSpan.MaxValue;
        }
    }

    public void Sample()
    {
        try
        {
            process.Refresh();
            if (process.HasExited)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var processorTime = process.TotalProcessorTime;
            if (hasBaseline)
            {
                var elapsedSeconds = (now - previousTimestamp) / (double)Stopwatch.Frequency;
                var processorSeconds = (processorTime - previousProcessorTime).TotalSeconds;
                var machineCpuPercent = elapsedSeconds <= 0d
                    ? 0d
                    : Math.Max(
                        0d,
                        processorSeconds / elapsedSeconds / Environment.ProcessorCount * 100d);
                var singleCoreCpuPercent = elapsedSeconds <= 0d
                    ? 0d
                    : Math.Max(0d, processorSeconds / elapsedSeconds * 100d);
                samples.Add(new ProcessSample(
                    now,
                    singleCoreCpuPercent,
                    machineCpuPercent,
                    process.WorkingSet64,
                    process.PrivateMemorySize64,
                    process.Threads.Count));
            }

            previousTimestamp = now;
            previousProcessorTime = processorTime;
            hasBaseline = true;
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void MarkSteadyState()
    {
        steadyStateStartIndex ??= samples.Count;
    }

    public StressProcessSummary? CreateSummary()
    {
        if (samples.Count == 0)
        {
            return null;
        }

        var steadyStateSamples = samples
            .Skip(Math.Min(steadyStateStartIndex ?? 0, samples.Count - 1))
            .ToArray();
        var steadyWindowSize = Math.Clamp(steadyStateSamples.Length / 5, 1, 30);
        var firstSteadyWindow = steadyStateSamples.AsSpan(0, steadyWindowSize);
        var lastSteadyWindow = steadyStateSamples.AsSpan(
            steadyStateSamples.Length - steadyWindowSize,
            steadyWindowSize);

        return new StressProcessSummary(
            process.Id,
            process.ProcessName,
            samples.Count,
            samples.Average(sample => sample.SingleCoreCpuPercent),
            samples.Max(sample => sample.SingleCoreCpuPercent),
            samples.Average(sample => sample.MachineCpuPercent),
            samples.Max(sample => sample.MachineCpuPercent),
            samples[0].WorkingSetBytes,
            (long)samples.Average(sample => sample.WorkingSetBytes),
            samples.Max(sample => sample.WorkingSetBytes),
            samples[^1].WorkingSetBytes,
            samples[0].PrivateMemoryBytes,
            (long)samples.Average(sample => sample.PrivateMemoryBytes),
            samples.Max(sample => sample.PrivateMemoryBytes),
            samples[^1].PrivateMemoryBytes,
            steadyStateSamples.Length,
            steadyStateSamples[0].WorkingSetBytes,
            steadyStateSamples[^1].WorkingSetBytes,
            steadyStateSamples.Max(sample => sample.WorkingSetBytes),
            (long)Average(firstSteadyWindow, static sample => sample.WorkingSetBytes),
            (long)Average(lastSteadyWindow, static sample => sample.WorkingSetBytes),
            CalculateTrendPerMinute(
                steadyStateSamples,
                static sample => sample.WorkingSetBytes),
            steadyStateSamples[0].PrivateMemoryBytes,
            steadyStateSamples[^1].PrivateMemoryBytes,
            steadyStateSamples.Max(sample => sample.PrivateMemoryBytes),
            (long)Average(firstSteadyWindow, static sample => sample.PrivateMemoryBytes),
            (long)Average(lastSteadyWindow, static sample => sample.PrivateMemoryBytes),
            CalculateTrendPerMinute(
                steadyStateSamples,
                static sample => sample.PrivateMemoryBytes),
            samples.Max(sample => sample.ThreadCount));
    }

    private static double Average(
        ReadOnlySpan<ProcessSample> source,
        Func<ProcessSample, long> selector)
    {
        double total = 0d;
        foreach (var sample in source)
        {
            total += selector(sample);
        }

        return total / source.Length;
    }

    private static double CalculateTrendPerMinute(
        IReadOnlyList<ProcessSample> source,
        Func<ProcessSample, long> selector)
    {
        if (source.Count < 2)
        {
            return 0d;
        }

        var origin = source[0].Timestamp;
        double sumX = 0d;
        double sumY = 0d;
        double sumXX = 0d;
        double sumXY = 0d;
        for (var index = 0; index < source.Count; index++)
        {
            var x = (source[index].Timestamp - origin) / (double)Stopwatch.Frequency;
            var y = selector(source[index]);
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;
        }

        var denominator = (source.Count * sumXX) - (sumX * sumX);
        return denominator <= 0d
            ? 0d
            : (((source.Count * sumXY) - (sumX * sumY)) / denominator) * 60d;
    }

    public void Dispose()
    {
        process.Dispose();
    }

    private sealed record ProcessSample(
        long Timestamp,
        double SingleCoreCpuPercent,
        double MachineCpuPercent,
        long WorkingSetBytes,
        long PrivateMemoryBytes,
        int ThreadCount);
}
