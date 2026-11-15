using System.Diagnostics.Metrics;

namespace Mmorpg.SimulationWorker.Realtime;

public sealed record RealtimeDurationSummary(
    long Samples,
    double AverageMilliseconds,
    double P95UpperBoundMilliseconds,
    double P99UpperBoundMilliseconds,
    double MaximumMilliseconds);

public sealed record RealtimePerformanceMetricsSnapshot(
    RealtimeDurationSummary? NetworkPoll,
    RealtimeDurationSummary? CompletedOperations,
    RealtimeDurationSummary? JoinQueueDelay,
    RealtimeDurationSummary? JoinFinalization,
    RealtimeDurationSummary? SimulationTick,
    RealtimeDurationSummary? SimulationTickLag,
    RealtimeDurationSummary? CollisionStreaming,
    RealtimeDurationSummary? MovementSimulation,
    RealtimeDurationSummary? InterestRefresh,
    RealtimeDurationSummary? SnapshotBroadcast,
    int CompletedOperationBacklog,
    int MaximumCompletedOperationBacklog,
    int CorpseMutationBacklog,
    int MaximumCorpseMutationBacklog,
    int ActiveCorpseMutations,
    long TickResynchronizations,
    long TickOverruns,
    long ProcessAllocatedBytes,
    int Generation0Collections,
    int Generation1Collections,
    int Generation2Collections,
    long ManagedHeapSizeBytes,
    long ManagedHeapFragmentedBytes,
    long TotalManagedMemoryBytes,
    long SnapshotRemoteRecipients,
    long SnapshotPacketsEncoded,
    long SnapshotPacketsSent,
    long SnapshotEntityUpdatesDeferred,
    long LoopStepFailures,
    long LoopIsolatedFailures)
{
    // Every tick phase that did its work in the interval, in the order the phases were timed
    // first, which is the tick order.
    public IReadOnlyList<RealtimeNamedDurationSummary> TickPhases { get; init; } = [];

    // Every step of a loop iteration outside the tick: network poll, completed operations, peer
    // supervision, orphan sweep, and send flush.
    public IReadOnlyList<RealtimeNamedDurationSummary> LoopSteps { get; init; } = [];

    // The time the garbage collector paused the process in the interval.
    public double GcPauseMilliseconds { get; init; }

    // The time the loop thread spent in its timed work: every tick and every loop step.
    public double LoopBusyMilliseconds { get; init; }
}

public sealed record RealtimeNamedDurationSummary(string Name, RealtimeDurationSummary Summary);

public sealed class RealtimePerformanceMetrics : IDisposable
{
    public const string MeterName = "Mmorpg.SimulationWorker.Performance";
    public const string TickPhaseTag = "phase";
    public const string LoopStepTag = "step";

    private readonly Meter meter = new(MeterName);
    private readonly Histogram<double> tickPhaseHistogram;
    private readonly Histogram<double> loopStepHistogram;
    private readonly object timerGate = new();
    private readonly List<DurationTimer> tickPhaseTimers = [];
    private readonly List<DurationTimer> loopStepTimers = [];
    private readonly Histogram<double> networkPollHistogram;
    private readonly Histogram<double> completedOperationsHistogram;
    private readonly Histogram<double> joinQueueDelayHistogram;
    private readonly Histogram<double> joinFinalizationHistogram;
    private readonly Histogram<double> simulationTickHistogram;
    private readonly Histogram<double> simulationTickLagHistogram;
    private readonly Histogram<double> collisionStreamingHistogram;
    private readonly Histogram<double> movementSimulationHistogram;
    private readonly Histogram<double> interestRefreshHistogram;
    private readonly Histogram<double> snapshotBroadcastHistogram;
    private readonly Counter<long> tickResynchronizationCounter;
    private readonly Counter<long> tickOverrunCounter;
    private readonly Counter<long> snapshotRemoteRecipientCounter;
    private readonly Counter<long> snapshotPacketEncodingCounter;
    private readonly Counter<long> snapshotPacketSendCounter;
    private readonly Counter<long> snapshotEntityUpdateDeferredCounter;
    private readonly Counter<long> loopFailureCounter;
    private readonly ObservableGauge<int> corpseMutationBacklogGauge;
    private readonly ObservableGauge<int> activeCorpseMutationGauge;
    private readonly DurationAccumulator networkPoll = new();
    private readonly DurationAccumulator completedOperations = new();
    private readonly DurationAccumulator joinQueueDelay = new();
    private readonly DurationAccumulator joinFinalization = new();
    private readonly DurationAccumulator simulationTick = new();
    private readonly DurationAccumulator simulationTickLag = new();
    private readonly DurationAccumulator collisionStreaming = new();
    private readonly DurationAccumulator movementSimulation = new();
    private readonly DurationAccumulator interestRefresh = new();
    private readonly DurationAccumulator snapshotBroadcast = new();
    private int completedOperationBacklog;
    private int maximumCompletedOperationBacklog;
    private int corpseMutationBacklog;
    private int maximumCorpseMutationBacklog;
    private int activeCorpseMutations;
    private long tickResynchronizations;
    private long tickOverruns;
    private long previousAllocatedBytes = GC.GetTotalAllocatedBytes(false);
    private int previousGeneration0Collections = GC.CollectionCount(0);
    private int previousGeneration1Collections = GC.CollectionCount(1);
    private int previousGeneration2Collections = GC.CollectionCount(2);
    private TimeSpan previousGcPause = GC.GetTotalPauseDuration();
    private long snapshotRemoteRecipients;
    private long snapshotPacketsEncoded;
    private long snapshotPacketsSent;
    private long snapshotEntityUpdatesDeferred;
    private long loopStepFailures;
    private long loopIsolatedFailures;

    public RealtimePerformanceMetrics()
    {
        networkPollHistogram = CreateHistogram("simulation_worker.loop.network_poll.duration");
        completedOperationsHistogram = CreateHistogram("simulation_worker.loop.completed_operations.duration");
        joinQueueDelayHistogram = CreateHistogram("simulation_worker.join.queue_delay.duration");
        joinFinalizationHistogram = CreateHistogram("simulation_worker.join.finalization.duration");
        simulationTickHistogram = CreateHistogram("simulation_worker.simulation.tick.duration");
        simulationTickLagHistogram = CreateHistogram("simulation_worker.simulation.tick.lag");
        collisionStreamingHistogram = CreateHistogram("simulation_worker.simulation.collision_streaming.duration");
        movementSimulationHistogram = CreateHistogram("simulation_worker.simulation.movement.duration");
        interestRefreshHistogram = CreateHistogram("simulation_worker.simulation.interest.duration");
        snapshotBroadcastHistogram = CreateHistogram("simulation_worker.simulation.snapshot_broadcast.duration");
        tickPhaseHistogram = CreateHistogram("simulation_worker.simulation.tick_phase.duration");
        loopStepHistogram = CreateHistogram("simulation_worker.loop.step.duration");
        tickResynchronizationCounter = meter.CreateCounter<long>(
            "simulation_worker.simulation.tick.resynchronizations");
        tickOverrunCounter = meter.CreateCounter<long>(
            "simulation_worker.simulation.tick.overruns");
        snapshotRemoteRecipientCounter = meter.CreateCounter<long>(
            "simulation_worker.simulation.snapshot.remote_recipients");
        snapshotPacketEncodingCounter = meter.CreateCounter<long>(
            "simulation_worker.simulation.snapshot.packets_encoded");
        snapshotPacketSendCounter = meter.CreateCounter<long>(
            "simulation_worker.simulation.snapshot.packets_sent");
        snapshotEntityUpdateDeferredCounter = meter.CreateCounter<long>(
            "simulation_worker.simulation.snapshot.entity_updates_deferred");
        loopFailureCounter = meter.CreateCounter<long>("simulation_worker.loop.failures");
        meter.CreateObservableGauge(
            "simulation_worker.loop.completed_operations.backlog",
            () => Volatile.Read(ref completedOperationBacklog));
        corpseMutationBacklogGauge = meter.CreateObservableGauge(
            "simulation_worker.corpse.mutation.backlog",
            () => Volatile.Read(ref corpseMutationBacklog));
        activeCorpseMutationGauge = meter.CreateObservableGauge(
            "simulation_worker.corpse.mutation.active_aggregates",
            () => Volatile.Read(ref activeCorpseMutations));
    }

    public void RecordNetworkPoll(TimeSpan duration) =>
        Record(networkPoll, networkPollHistogram, duration);

    public void RecordCompletedOperations(TimeSpan duration) =>
        Record(completedOperations, completedOperationsHistogram, duration);

    public void RecordJoinFinalization(TimeSpan duration) =>
        Record(joinFinalization, joinFinalizationHistogram, duration);

    public void RecordJoinQueueDelay(TimeSpan duration) =>
        Record(joinQueueDelay, joinQueueDelayHistogram, duration);

    public void ObserveCompletedOperationBacklog(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Volatile.Write(ref completedOperationBacklog, count);
        var observedMaximum = Volatile.Read(ref maximumCompletedOperationBacklog);
        while (count > observedMaximum)
        {
            var previous = Interlocked.CompareExchange(
                ref maximumCompletedOperationBacklog,
                count,
                observedMaximum);
            if (previous == observedMaximum)
            {
                break;
            }

            observedMaximum = previous;
        }
    }

    public void ObserveCorpseMutationCoordinator(int backlog, int activeCorpses)
    {
        if (backlog < 0 || activeCorpses < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(backlog));
        }

        Volatile.Write(ref corpseMutationBacklog, backlog);
        Volatile.Write(ref activeCorpseMutations, activeCorpses);
        var observedMaximum = Volatile.Read(ref maximumCorpseMutationBacklog);
        while (backlog > observedMaximum)
        {
            var previous = Interlocked.CompareExchange(
                ref maximumCorpseMutationBacklog,
                backlog,
                observedMaximum);
            if (previous == observedMaximum)
            {
                break;
            }

            observedMaximum = previous;
        }
    }

    // A tick that takes longer than its fixed interval is an overrun, even when the
    // catch-up budget later absorbs the delay without a resynchronization.
    public void RecordSimulationTick(TimeSpan duration, TimeSpan budget)
    {
        Record(simulationTick, simulationTickHistogram, duration);
        if (duration > budget)
        {
            Interlocked.Increment(ref tickOverruns);
            tickOverrunCounter.Add(1);
        }
    }

    public void RecordSimulationTickLag(TimeSpan duration) =>
        Record(simulationTickLag, simulationTickLagHistogram, duration);

    public void RecordCollisionStreaming(TimeSpan duration) =>
        Record(collisionStreaming, collisionStreamingHistogram, duration);

    public void RecordMovementSimulation(TimeSpan duration) =>
        Record(movementSimulation, movementSimulationHistogram, duration);

    public void RecordInterestRefresh(TimeSpan duration) =>
        Record(interestRefresh, interestRefreshHistogram, duration);

    public void RecordSnapshotBroadcast(TimeSpan duration) =>
        Record(snapshotBroadcast, snapshotBroadcastHistogram, duration);

    // The timer of one tick phase, labeled with its name; one per name, created during
    // composition, so the loop records without allocating.
    internal DurationTimer TickPhaseTimer(string name) =>
        GetOrAddTimer(tickPhaseTimers, tickPhaseHistogram, TickPhaseTag, name);

    // The timer of one step of a loop iteration, labeled with its name.
    internal DurationTimer LoopStepTimer(string name) =>
        GetOrAddTimer(loopStepTimers, loopStepHistogram, LoopStepTag, name);

    public void RecordTickResynchronization()
    {
        Interlocked.Increment(ref tickResynchronizations);
        tickResynchronizationCounter.Add(1);
    }

    public void RecordSnapshotSends(
        int remoteRecipients,
        int packetsEncoded,
        int packetsSent,
        long entityUpdatesDeferred)
    {
        if (remoteRecipients < 0
            || packetsEncoded < 0
            || packetsSent < 0
            || entityUpdatesDeferred < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remoteRecipients));
        }

        Interlocked.Add(ref snapshotRemoteRecipients, remoteRecipients);
        Interlocked.Add(ref snapshotPacketsEncoded, packetsEncoded);
        Interlocked.Add(ref snapshotPacketsSent, packetsSent);
        Interlocked.Add(ref snapshotEntityUpdatesDeferred, entityUpdatesDeferred);
        snapshotRemoteRecipientCounter.Add(remoteRecipients);
        snapshotPacketEncodingCounter.Add(packetsEncoded);
        snapshotPacketSendCounter.Add(packetsSent);
        snapshotEntityUpdateDeferredCounter.Add(entityUpdatesDeferred);
    }

    // A tick phase or loop step that threw, labeled with its name and with what it took out.
    internal void RecordLoopFailure(string step, RealtimeLoopFailureScope scope)
    {
        Interlocked.Increment(ref scope == RealtimeLoopFailureScope.Step ? ref loopStepFailures : ref loopIsolatedFailures);
        loopFailureCounter.Add(
            1,
            new KeyValuePair<string, object?>("step", step),
            new KeyValuePair<string, object?>(
                "scope",
                scope switch
                {
                    RealtimeLoopFailureScope.Step => "step",
                    RealtimeLoopFailureScope.Player => "player",
                    _ => "peer"
                }));
    }

    public RealtimePerformanceMetricsSnapshot CaptureAndReset()
    {
        var allocatedBytes = GC.GetTotalAllocatedBytes(false);
        var generation0Collections = GC.CollectionCount(0);
        var generation1Collections = GC.CollectionCount(1);
        var generation2Collections = GC.CollectionCount(2);
        var memoryInfo = GC.GetGCMemoryInfo();
        var currentCompletedOperationBacklog = Volatile.Read(
            ref completedOperationBacklog);
        var maximumBacklog = Math.Max(
            currentCompletedOperationBacklog,
            Interlocked.Exchange(
                ref maximumCompletedOperationBacklog,
                currentCompletedOperationBacklog));
        var currentCorpseMutationBacklog = Volatile.Read(ref corpseMutationBacklog);
        var maximumCorpseBacklog = Math.Max(
            currentCorpseMutationBacklog,
            Interlocked.Exchange(
                ref maximumCorpseMutationBacklog,
                currentCorpseMutationBacklog));
        var gcPause = GC.GetTotalPauseDuration();
        var gcPauseMilliseconds = Math.Max(0d, (gcPause - previousGcPause).TotalMilliseconds);
        previousGcPause = gcPause;
        var tick = simulationTick.CaptureAndReset();
        IReadOnlyList<RealtimeNamedDurationSummary> tickPhases;
        IReadOnlyList<RealtimeNamedDurationSummary> loopSteps;
        lock (timerGate)
        {
            tickPhases = CaptureAndReset(tickPhaseTimers);
            loopSteps = CaptureAndReset(loopStepTimers);
        }

        // A tick holds its phases, so the busy time adds ticks and loop steps only.
        var loopBusyMilliseconds = TotalMilliseconds(tick);
        foreach (var step in loopSteps)
        {
            loopBusyMilliseconds += TotalMilliseconds(step.Summary);
        }

        return new RealtimePerformanceMetricsSnapshot(
            networkPoll.CaptureAndReset(),
            completedOperations.CaptureAndReset(),
            joinQueueDelay.CaptureAndReset(),
            joinFinalization.CaptureAndReset(),
            tick,
            simulationTickLag.CaptureAndReset(),
            collisionStreaming.CaptureAndReset(),
            movementSimulation.CaptureAndReset(),
            interestRefresh.CaptureAndReset(),
            snapshotBroadcast.CaptureAndReset(),
            currentCompletedOperationBacklog,
            maximumBacklog,
            currentCorpseMutationBacklog,
            maximumCorpseBacklog,
            Volatile.Read(ref activeCorpseMutations),
            Interlocked.Exchange(ref tickResynchronizations, 0),
            Interlocked.Exchange(ref tickOverruns, 0),
            Math.Max(0, allocatedBytes - Interlocked.Exchange(ref previousAllocatedBytes, allocatedBytes)),
            Math.Max(
                0,
                generation0Collections - Interlocked.Exchange(
                    ref previousGeneration0Collections,
                    generation0Collections)),
            Math.Max(
                0,
                generation1Collections - Interlocked.Exchange(
                    ref previousGeneration1Collections,
                    generation1Collections)),
            Math.Max(
                0,
                generation2Collections - Interlocked.Exchange(
                    ref previousGeneration2Collections,
                    generation2Collections)),
            memoryInfo.HeapSizeBytes,
            memoryInfo.FragmentedBytes,
            GC.GetTotalMemory(false),
            Interlocked.Exchange(ref snapshotRemoteRecipients, 0),
            Interlocked.Exchange(ref snapshotPacketsEncoded, 0),
            Interlocked.Exchange(ref snapshotPacketsSent, 0),
            Interlocked.Exchange(ref snapshotEntityUpdatesDeferred, 0),
            Interlocked.Exchange(ref loopStepFailures, 0),
            Interlocked.Exchange(ref loopIsolatedFailures, 0))
        {
            TickPhases = tickPhases,
            LoopSteps = loopSteps,
            GcPauseMilliseconds = gcPauseMilliseconds,
            LoopBusyMilliseconds = loopBusyMilliseconds
        };
    }

    public void Dispose()
    {
        meter.Dispose();
    }

    private Histogram<double> CreateHistogram(string name)
    {
        return meter.CreateHistogram<double>(name, "ms");
    }

    private DurationTimer GetOrAddTimer(
        List<DurationTimer> timers,
        Histogram<double> histogram,
        string tagName,
        string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (timerGate)
        {
            foreach (var timer in timers)
            {
                if (string.Equals(timer.Name, name, StringComparison.Ordinal))
                {
                    return timer;
                }
            }

            var created = new DurationTimer(name, histogram, new KeyValuePair<string, object?>(tagName, name));
            timers.Add(created);
            return created;
        }
    }

    private static List<RealtimeNamedDurationSummary> CaptureAndReset(List<DurationTimer> timers)
    {
        var captured = new List<RealtimeNamedDurationSummary>(timers.Count);
        foreach (var timer in timers)
        {
            if (timer.CaptureAndReset() is { } summary)
            {
                captured.Add(new RealtimeNamedDurationSummary(timer.Name, summary));
            }
        }

        return captured;
    }

    private static double TotalMilliseconds(RealtimeDurationSummary? summary)
    {
        return summary is null ? 0d : summary.AverageMilliseconds * summary.Samples;
    }

    // Times one named tick phase or loop step into its own accumulator and into the shared
    // histogram under its name tag. Recording takes the accumulator's lock and allocates nothing.
    internal sealed class DurationTimer
    {
        private readonly DurationAccumulator accumulator = new();
        private readonly Histogram<double> histogram;
        private readonly KeyValuePair<string, object?> tag;

        internal DurationTimer(string name, Histogram<double> histogram, KeyValuePair<string, object?> tag)
        {
            Name = name;
            this.histogram = histogram;
            this.tag = tag;
        }

        public string Name { get; }

        public void Record(TimeSpan duration)
        {
            var milliseconds = Math.Max(0d, duration.TotalMilliseconds);
            accumulator.Record(milliseconds);
            histogram.Record(milliseconds, tag);
        }

        internal RealtimeDurationSummary? CaptureAndReset()
        {
            return accumulator.CaptureAndReset();
        }
    }

    private static void Record(
        DurationAccumulator accumulator,
        Histogram<double> histogram,
        TimeSpan duration)
    {
        var milliseconds = Math.Max(0d, duration.TotalMilliseconds);
        accumulator.Record(milliseconds);
        histogram.Record(milliseconds);
    }

    private sealed class DurationAccumulator
    {
        private static readonly double[] UpperBoundsMilliseconds =
        [
            0.1,
            0.25,
            0.5,
            1,
            2,
            4,
            8,
            16,
            24,
            33.34,
            50,
            100,
            250,
            500,
            double.PositiveInfinity
        ];

        private readonly long[] buckets = new long[UpperBoundsMilliseconds.Length];
        private readonly object gate = new();
        private long samples;
        private long totalMicroseconds;
        private long maximumMicroseconds;

        public void Record(double milliseconds)
        {
            var microseconds = (long)Math.Ceiling(milliseconds * 1000d);
            var bucketIndex = FindBucketIndex(milliseconds);
            lock (gate)
            {
                samples++;
                totalMicroseconds += microseconds;
                maximumMicroseconds = Math.Max(maximumMicroseconds, microseconds);
                buckets[bucketIndex]++;
            }
        }

        // A plain loop, because a predicate lambda would capture the sample and
        // allocate on every tick.
        private static int FindBucketIndex(double milliseconds)
        {
            for (var index = 0; index < UpperBoundsMilliseconds.Length; index++)
            {
                if (milliseconds <= UpperBoundsMilliseconds[index])
                {
                    return index;
                }
            }

            return UpperBoundsMilliseconds.Length - 1;
        }

        public RealtimeDurationSummary? CaptureAndReset()
        {
            lock (gate)
            {
                if (samples == 0)
                {
                    return null;
                }

                var capturedSamples = samples;
                var capturedTotalMicroseconds = totalMicroseconds;
                var capturedMaximumMicroseconds = maximumMicroseconds;
                var capturedBuckets = buckets.ToArray();
                samples = 0;
                totalMicroseconds = 0;
                maximumMicroseconds = 0;
                Array.Clear(buckets);

                return new RealtimeDurationSummary(
                    capturedSamples,
                    capturedTotalMicroseconds / 1000d / capturedSamples,
                    FindPercentileUpperBound(capturedBuckets, capturedSamples, 0.95, capturedMaximumMicroseconds),
                    FindPercentileUpperBound(capturedBuckets, capturedSamples, 0.99, capturedMaximumMicroseconds),
                    capturedMaximumMicroseconds / 1000d);
            }
        }

        private static double FindPercentileUpperBound(
            IReadOnlyList<long> capturedBuckets,
            long capturedSamples,
            double percentile,
            long capturedMaximumMicroseconds)
        {
            var target = (long)Math.Ceiling(capturedSamples * percentile);
            long cumulative = 0;
            for (var index = 0; index < capturedBuckets.Count; index++)
            {
                cumulative += capturedBuckets[index];
                if (cumulative >= target)
                {
                    var upperBound = UpperBoundsMilliseconds[index];
                    return double.IsPositiveInfinity(upperBound)
                        ? capturedMaximumMicroseconds / 1000d
                        : upperBound;
                }
            }

            return capturedMaximumMicroseconds / 1000d;
        }
    }
}
