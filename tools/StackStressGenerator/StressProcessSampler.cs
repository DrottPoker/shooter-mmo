using System.Diagnostics;

namespace Mmorpg.Tools.StackStressGenerator;

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
    int MaximumThreadCount)
{
    // The working set, and for the generator its own managed heap, over the steady interval.
    public StressMemoryTrend? Memory { get; init; }
}

// Samples one process once a second. It keeps sums, extremes, and the steady interval's trend
// accumulators instead of the samples, so a long run does not grow the generator's memory with
// its own measurements. The steady windows are the first and the last fifth of the planned
// steady interval.
public sealed class StressProcessSampler : IDisposable
{
    private const double Mebibyte = 1024d * 1024d;

    private readonly Process process;
    private readonly bool current;
    private readonly string label;
    private TimeSpan previousProcessorTime;
    private long previousTimestamp;
    private bool hasBaseline;
    private int samples;
    private double singleCoreCpuSum;
    private double singleCoreCpuMaximum;
    private double machineCpuSum;
    private double machineCpuMaximum;
    private long initialWorkingSet;
    private double workingSetSum;
    private long maximumWorkingSet;
    private long finalWorkingSet;
    private long initialPrivateMemory;
    private double privateMemorySum;
    private long maximumPrivateMemory;
    private long finalPrivateMemory;
    private int maximumThreads;
    private SteadyWindow? steady;
    private bool steadyEnded;

    // The generator's own collections and allocation over the steady interval.
    private int steadyStartGeneration2;
    private long steadyStartAllocated;
    private int steadyEndGeneration2;
    private long steadyEndAllocated;
    private long steadyEndTimestamp;

    private StressProcessSampler(Process process, bool current, string label)
    {
        this.process = process;
        this.current = current;
        this.label = label;
    }

    public static StressProcessSampler AttachCurrent()
    {
        return new StressProcessSampler(Process.GetCurrentProcess(), true, "generator");
    }

    public static StressProcessSampler? TryAttach(DateTime workerStartedAt)
    {
        return TryAttach("Mmorpg.SimulationWorker", workerStartedAt);
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
        return new StressProcessSampler(
            selected,
            false,
            processName switch
            {
                "Mmorpg.SimulationWorker" => "worker",
                "Mmorpg.GameService" => "gameService",
                _ => processName
            });
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
                Add(
                    now,
                    singleCoreCpuPercent,
                    machineCpuPercent,
                    process.WorkingSet64,
                    process.PrivateMemorySize64,
                    process.Threads.Count,
                    current ? GC.GetGCMemoryInfo().HeapSizeBytes : null);
            }

            previousTimestamp = now;
            previousProcessorTime = processorTime;
            hasBaseline = true;
        }
        catch (InvalidOperationException)
        {
        }
    }

    // The steady interval starts; its windows are fifths of the planned duration.
    // The run's steady interval starts at this Stopwatch timestamp; its windows are fifths of the
    // planned duration.
    public void MarkSteadyState(long startTimestamp, TimeSpan plannedDuration)
    {
        if (steady is not null)
        {
            return;
        }

        steady = new SteadyWindow(startTimestamp, plannedDuration);
        steadyStartGeneration2 = GC.CollectionCount(2);
        steadyStartAllocated = GC.GetTotalAllocatedBytes();
    }

    // The run's steady interval ended at this Stopwatch timestamp; later samples count only for
    // the whole run.
    public void MarkSteadyEnd(long endTimestamp)
    {
        if (steady is null || steadyEnded)
        {
            return;
        }

        steadyEnded = true;
        steadyEndGeneration2 = GC.CollectionCount(2);
        steadyEndAllocated = GC.GetTotalAllocatedBytes();
        steadyEndTimestamp = endTimestamp;
    }

    public StressProcessSummary? CreateSummary()
    {
        if (samples == 0)
        {
            return null;
        }

        var window = steady is { WorkingSet.Samples: > 0 } ? steady : null;
        return new StressProcessSummary(
            process.Id,
            process.ProcessName,
            samples,
            singleCoreCpuSum / samples,
            singleCoreCpuMaximum,
            machineCpuSum / samples,
            machineCpuMaximum,
            initialWorkingSet,
            (long)(workingSetSum / samples),
            maximumWorkingSet,
            finalWorkingSet,
            initialPrivateMemory,
            (long)(privateMemorySum / samples),
            maximumPrivateMemory,
            finalPrivateMemory,
            window?.WorkingSet.Samples ?? 0,
            window?.InitialWorkingSet ?? 0,
            window?.FinalWorkingSet ?? 0,
            window?.MaximumWorkingSet ?? 0,
            (long)(window?.WorkingSet.FirstFifth() ?? 0d),
            (long)(window?.WorkingSet.LastFifth() ?? 0d),
            (window?.WorkingSet.SlopePerHour() ?? 0d) / 60d,
            window?.InitialPrivateMemory ?? 0,
            window?.FinalPrivateMemory ?? 0,
            window?.MaximumPrivateMemory ?? 0,
            (long)(window?.PrivateMemory.FirstFifth() ?? 0d),
            (long)(window?.PrivateMemory.LastFifth() ?? 0d),
            (window?.PrivateMemory.SlopePerHour() ?? 0d) / 60d,
            maximumThreads)
        {
            Memory = window is null
                ? null
                : new StressMemoryTrend(
                    label,
                    window.WorkingSet.Samples,
                    window.WorkingSet.SlopePerHour() / Mebibyte,
                    window.WorkingSet.Fifths(1d / Mebibyte),
                    window.ManagedHeap?.SlopePerHour() / Mebibyte,
                    window.ManagedHeap?.Fifths(1d / Mebibyte),
                    SteadySeconds() is { } seconds ? (steadyEndGeneration2 - steadyStartGeneration2) / seconds * 3600d : null,
                    SteadySeconds() is { } allocationSeconds ? (steadyEndAllocated - steadyStartAllocated) / allocationSeconds : null)
        };
    }

    // The length of the steady interval for the generator's own counters, or null for another
    // process or before the interval ended.
    private double? SteadySeconds()
    {
        return current && steadyEnded && steady is not null && steadyEndTimestamp > steady.StartTimestamp
            ? (steadyEndTimestamp - steady.StartTimestamp) / (double)Stopwatch.Frequency
            : null;
    }

    public void Dispose()
    {
        process.Dispose();
    }

    private void Add(
        long timestamp,
        double singleCoreCpuPercent,
        double machineCpuPercent,
        long workingSet,
        long privateMemory,
        int threads,
        long? managedHeap)
    {
        if (samples == 0)
        {
            initialWorkingSet = workingSet;
            initialPrivateMemory = privateMemory;
        }

        samples++;
        singleCoreCpuSum += singleCoreCpuPercent;
        singleCoreCpuMaximum = Math.Max(singleCoreCpuMaximum, singleCoreCpuPercent);
        machineCpuSum += machineCpuPercent;
        machineCpuMaximum = Math.Max(machineCpuMaximum, machineCpuPercent);
        workingSetSum += workingSet;
        maximumWorkingSet = Math.Max(maximumWorkingSet, workingSet);
        finalWorkingSet = workingSet;
        privateMemorySum += privateMemory;
        maximumPrivateMemory = Math.Max(maximumPrivateMemory, privateMemory);
        finalPrivateMemory = privateMemory;
        maximumThreads = Math.Max(maximumThreads, threads);
        if (steady is not null && !steadyEnded && timestamp >= steady.StartTimestamp)
        {
            steady.Add(timestamp, workingSet, privateMemory, managedHeap);
        }
    }

    private sealed class SteadyWindow(long startTimestamp, TimeSpan plannedDuration)
    {
        public long StartTimestamp { get; } = startTimestamp;

        public StressTrendAccumulator WorkingSet { get; } = new(startTimestamp, plannedDuration);

        public StressTrendAccumulator PrivateMemory { get; } = new(startTimestamp, plannedDuration);

        public StressTrendAccumulator? ManagedHeap { get; private set; }

        public long InitialWorkingSet { get; private set; }

        public long FinalWorkingSet { get; private set; }

        public long MaximumWorkingSet { get; private set; }

        public long InitialPrivateMemory { get; private set; }

        public long FinalPrivateMemory { get; private set; }

        public long MaximumPrivateMemory { get; private set; }

        public void Add(long timestamp, long workingSet, long privateMemory, long? managedHeap)
        {
            if (WorkingSet.Samples == 0)
            {
                InitialWorkingSet = workingSet;
                InitialPrivateMemory = privateMemory;
            }

            WorkingSet.Add(timestamp, workingSet);
            PrivateMemory.Add(timestamp, privateMemory);
            FinalWorkingSet = workingSet;
            FinalPrivateMemory = privateMemory;
            MaximumWorkingSet = Math.Max(MaximumWorkingSet, workingSet);
            MaximumPrivateMemory = Math.Max(MaximumPrivateMemory, privateMemory);
            if (managedHeap is { } heap)
            {
                ManagedHeap ??= new StressTrendAccumulator(StartTimestamp, plannedDuration);
                ManagedHeap.Add(timestamp, heap);
            }
        }
    }
}
