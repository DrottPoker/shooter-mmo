using System.Diagnostics.Metrics;

namespace SimulationWorker.Realtime;

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
    long ProcessAllocatedBytes,
    int Generation0Collections,
    int Generation1Collections,
    int Generation2Collections,
    long ManagedHeapSizeBytes,
    long ManagedHeapFragmentedBytes,
    long TotalManagedMemoryBytes,
    long SnapshotVisibilityGroups,
    long SnapshotPacketsEncoded,
    long SnapshotPacketsSent,
    long SnapshotEntityUpdatesDeferred);

public sealed class RealtimePerformanceMetrics : IDisposable
{
    public const string MeterName = "ShooterMmo.SimulationWorker.Performance";

    private readonly Meter meter = new(MeterName);
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
    private readonly Counter<long> snapshotVisibilityGroupCounter;
    private readonly Counter<long> snapshotPacketEncodingCounter;
    private readonly Counter<long> snapshotPacketSendCounter;
    private readonly Counter<long> snapshotEntityUpdateDeferredCounter;
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
    private long previousAllocatedBytes = GC.GetTotalAllocatedBytes(false);
    private int previousGeneration0Collections = GC.CollectionCount(0);
    private int previousGeneration1Collections = GC.CollectionCount(1);
    private int previousGeneration2Collections = GC.CollectionCount(2);
    private long snapshotVisibilityGroups;
    private long snapshotPacketsEncoded;
    private long snapshotPacketsSent;
    private long snapshotEntityUpdatesDeferred;

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
        tickResynchronizationCounter = meter.CreateCounter<long>(
            "simulation_worker.simulation.tick.resynchronizations");
        snapshotVisibilityGroupCounter = meter.CreateCounter<long>(
            "simulation_worker.simulation.snapshot.visibility_groups");
        snapshotPacketEncodingCounter = meter.CreateCounter<long>(
            "simulation_worker.simulation.snapshot.packets_encoded");
        snapshotPacketSendCounter = meter.CreateCounter<long>(
            "simulation_worker.simulation.snapshot.packets_sent");
        snapshotEntityUpdateDeferredCounter = meter.CreateCounter<long>(
            "simulation_worker.simulation.snapshot.entity_updates_deferred");
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

    public void RecordSimulationTick(TimeSpan duration) =>
        Record(simulationTick, simulationTickHistogram, duration);

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

    public void RecordTickResynchronization()
    {
        Interlocked.Increment(ref tickResynchronizations);
        tickResynchronizationCounter.Add(1);
    }

    public void RecordSnapshotPacketReuse(
        int visibilityGroups,
        int packetsEncoded,
        int packetsSent,
        long entityUpdatesDeferred)
    {
        if (visibilityGroups < 0
            || packetsEncoded < 0
            || packetsSent < 0
            || entityUpdatesDeferred < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(visibilityGroups));
        }

        Interlocked.Add(ref snapshotVisibilityGroups, visibilityGroups);
        Interlocked.Add(ref snapshotPacketsEncoded, packetsEncoded);
        Interlocked.Add(ref snapshotPacketsSent, packetsSent);
        Interlocked.Add(ref snapshotEntityUpdatesDeferred, entityUpdatesDeferred);
        snapshotVisibilityGroupCounter.Add(visibilityGroups);
        snapshotPacketEncodingCounter.Add(packetsEncoded);
        snapshotPacketSendCounter.Add(packetsSent);
        snapshotEntityUpdateDeferredCounter.Add(entityUpdatesDeferred);
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
        return new RealtimePerformanceMetricsSnapshot(
            networkPoll.CaptureAndReset(),
            completedOperations.CaptureAndReset(),
            joinQueueDelay.CaptureAndReset(),
            joinFinalization.CaptureAndReset(),
            simulationTick.CaptureAndReset(),
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
            Interlocked.Exchange(ref snapshotVisibilityGroups, 0),
            Interlocked.Exchange(ref snapshotPacketsEncoded, 0),
            Interlocked.Exchange(ref snapshotPacketsSent, 0),
            Interlocked.Exchange(ref snapshotEntityUpdatesDeferred, 0));
    }

    public void Dispose()
    {
        meter.Dispose();
    }

    private Histogram<double> CreateHistogram(string name)
    {
        return meter.CreateHistogram<double>(name, "ms");
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
            var bucketIndex = Array.FindIndex(
                UpperBoundsMilliseconds,
                upperBound => milliseconds <= upperBound);
            lock (gate)
            {
                samples++;
                totalMicroseconds += microseconds;
                maximumMicroseconds = Math.Max(maximumMicroseconds, microseconds);
                buckets[bucketIndex]++;
            }
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
