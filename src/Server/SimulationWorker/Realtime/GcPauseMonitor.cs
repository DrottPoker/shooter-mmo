using System.Diagnostics.Tracing;
using System.Runtime;

namespace Mmorpg.SimulationWorker.Realtime;

// The garbage collection pauses of one metrics interval: how many there were, by the generation
// of the collection that paused the process, and the longest one.
public sealed record RealtimeGcPauseCounts(
    long Pauses,
    long Generation0Pauses,
    long Generation1Pauses,
    long Generation2Pauses,
    double LongestPauseMilliseconds);

// Counts every garbage collection pause of the process from the runtime's own GC events, with
// the generation of the collection that paused it and the longest pause. A pause runs from the
// suspension of the managed threads to their restart, as the runtime reports them. A pause
// without a collection start of its own, such as the last one of a background collection, counts
// as generation 2. The events arrive on the runtime's event dispatch thread, which alone writes
// the counts; the metrics reporter takes them with Interlocked exchanges, so the realtime loop
// never touches the monitor.
internal sealed class GcPauseMonitor : EventListener
{
    private const string RuntimeEventSourceName = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords GcKeyword = (EventKeywords)0x1;
    private const int GcStartEventId = 1;
    private const int GcRestartExecutionEngineEndEventId = 3;
    private const int GcSuspendExecutionEngineBeginEventId = 9;

    // Field initializers run before the base constructor, which may already enable the source.
    private readonly long[] pausesByGeneration = new long[3];
    private long longestPauseTicks;

    // The pause in progress, kept by the dispatch thread alone.
    private DateTime suspendedAt;
    private bool suspended;
    private int pauseGeneration = -1;

    // How the process collects garbage, for the worker's start log line.
    public static string DescribeConfiguration()
    {
        var concurrent = GC.GetConfigurationVariables().TryGetValue("ConcurrentGC", out var value)
            && value is true;
        return $"{(GCSettings.IsServerGC ? "server" : "workstation")}, "
            + $"{(concurrent ? "concurrent" : "non-concurrent")}, latency mode {GCSettings.LatencyMode}";
    }

    // The pauses since the last call, which start the next interval.
    public RealtimeGcPauseCounts CaptureAndReset()
    {
        var generation0 = Interlocked.Exchange(ref pausesByGeneration[0], 0);
        var generation1 = Interlocked.Exchange(ref pausesByGeneration[1], 0);
        var generation2 = Interlocked.Exchange(ref pausesByGeneration[2], 0);
        var longest = Interlocked.Exchange(ref longestPauseTicks, 0);
        return new RealtimeGcPauseCounts(
            generation0 + generation1 + generation2,
            generation0,
            generation1,
            generation2,
            TimeSpan.FromTicks(longest).TotalMilliseconds);
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (string.Equals(eventSource.Name, RuntimeEventSourceName, StringComparison.Ordinal))
        {
            EnableEvents(eventSource, EventLevel.Informational, GcKeyword);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        switch (eventData.EventId)
        {
            case GcSuspendExecutionEngineBeginEventId:
                suspendedAt = eventData.TimeStamp;
                suspended = true;
                pauseGeneration = -1;
                break;
            case GcStartEventId when suspended:
                pauseGeneration = DepthOf(eventData);
                break;
            case GcRestartExecutionEngineEndEventId when suspended:
                suspended = false;
                RecordPause(
                    Math.Max(0L, (eventData.TimeStamp - suspendedAt).Ticks),
                    pauseGeneration < 0 ? 2 : pauseGeneration);
                break;
        }
    }

    private void RecordPause(long ticks, int generation)
    {
        Interlocked.Increment(ref pausesByGeneration[generation]);
        var longest = Volatile.Read(ref longestPauseTicks);
        while (ticks > longest)
        {
            var previous = Interlocked.CompareExchange(ref longestPauseTicks, ticks, longest);
            if (previous == longest)
            {
                break;
            }

            longest = previous;
        }
    }

    // The generation that a collection start names, 0 to 2.
    private static int DepthOf(EventWrittenEventArgs eventData)
    {
        var names = eventData.PayloadNames;
        var payload = eventData.Payload;
        if (names is null || payload is null)
        {
            return -1;
        }

        var index = names.IndexOf("Depth");
        return index >= 0 && index < payload.Count && payload[index] is uint depth
            ? (int)Math.Min(depth, 2u)
            : -1;
    }
}
