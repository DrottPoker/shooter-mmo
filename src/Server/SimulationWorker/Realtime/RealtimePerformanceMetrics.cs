using System.Diagnostics.Metrics;

namespace Mmorpg.SimulationWorker.Realtime;

public sealed record RealtimeDurationSummary(
    long Samples,
    double AverageMilliseconds,
    double P95UpperBoundMilliseconds,
    double P99UpperBoundMilliseconds,
    double MaximumMilliseconds)
{
    // What garbage collection pauses took of the samples; null for a duration that is not
    // watched for them.
    public RealtimeGcPauseShare? GcPauses { get; init; }
}

// The samples of a duration during which a garbage collection paused the process, the pause time
// inside them, and the p99 bucket bound and the longest sample once each sample's pauses are
// taken out of it.
public sealed record RealtimeGcPauseShare(
    long PausedSamples,
    double PauseMilliseconds,
    double P99UpperBoundWithoutPausesMilliseconds,
    double MaximumWithoutPausesMilliseconds);

// The slowest tick of an interval: its duration, the garbage collection pause inside it, and how
// long each tick phase and phase part that ran in it took, in the order they were timed first.
public sealed record RealtimeSlowestTick(
    double Milliseconds,
    double GcPauseMilliseconds,
    IReadOnlyList<RealtimeNamedMilliseconds> Phases,
    IReadOnlyList<RealtimeNamedMilliseconds> PhaseParts)
{
    // The spawn and despawn packets the tick sent, such as those of an interest refresh after a
    // player came into a new place.
    public long SpawnPackets { get; init; }

    public long DespawnPackets { get; init; }

    // The loop thread's CPU time inside the tick; the rest of the tick it did not run. Null
    // without a CPU clock.
    public double? CpuMilliseconds { get; init; }
}

public sealed record RealtimeNamedMilliseconds(string Name, double Milliseconds);

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

    // The parts of tick phases that time themselves, such as the planning inside the snapshots,
    // named "<phase>: <part>", in the order they were timed first.
    public IReadOnlyList<RealtimeNamedDurationSummary> TickPhaseParts { get; init; } = [];

    // The time the garbage collector paused the process in the interval.
    public double GcPauseMilliseconds { get; init; }

    // The pauses one by one, from the runtime's GC events; null when the worker does not watch
    // them.
    public RealtimeGcPauseCounts? GcPauses { get; init; }

    // Overruns that stay longer than the tick interval once their garbage collection pauses are
    // taken out of them.
    public long TickOverrunsWithoutGcPauses { get; init; }

    // The slowest tick of the interval with its phases and parts; null without a tick.
    public RealtimeSlowestTick? SlowestTick { get; init; }

    // The time the loop thread spent in its timed work: every tick and every loop step.
    public double LoopBusyMilliseconds { get; init; }

    // Per tick, the loop thread's CPU time and the time it did not run, wall time minus CPU
    // time; null without a CPU clock.
    public RealtimeDurationSummary? SimulationTickCpu { get; init; }

    public RealtimeDurationSummary? SimulationTickNotRunning { get; init; }

    // Steps that players and Mobs skipped because collision chunks around them still loaded.
    public long CollisionPendingSteps { get; init; }
}

public sealed record RealtimeNamedDurationSummary(string Name, RealtimeDurationSummary Summary);

public sealed class RealtimePerformanceMetrics : IDisposable
{
    public const string MeterName = "Mmorpg.SimulationWorker.Performance";
    public const string TickPhaseTag = "phase";
    public const string TickPhasePartTag = "part";
    public const string LoopStepTag = "step";

    private readonly Meter meter = new(MeterName);
    private readonly Histogram<double> tickPhaseHistogram;
    private readonly Histogram<double> tickPhasePartHistogram;
    private readonly Histogram<double> loopStepHistogram;
    private readonly object timerGate = new();
    private readonly List<DurationTimer> tickPhaseTimers = [];
    private readonly List<DurationTimer> tickPhasePartTimers = [];
    private readonly List<DurationTimer> loopStepTimers = [];
    private readonly SlowestTickRecorder slowestTick = new();
    private readonly GcPauseMonitor? gcPauseMonitor;
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
    private readonly Counter<long> collisionPendingStepCounter;
    private readonly ObservableGauge<int> corpseMutationBacklogGauge;
    private readonly ObservableGauge<int> activeCorpseMutationGauge;
    private readonly DurationAccumulator networkPoll = new();
    private readonly DurationAccumulator completedOperations = new();
    private readonly DurationAccumulator joinQueueDelay = new();
    private readonly DurationAccumulator joinFinalization = new();
    private readonly DurationAccumulator simulationTick = new();
    private readonly GcPauseAccumulator simulationTickGcPauses = new();
    private readonly DurationAccumulator simulationTickCpu = new();
    private readonly DurationAccumulator simulationTickNotRunning = new();
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
    private long tickOverrunsWithoutGcPauses;
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
    private long collisionPendingSteps;

    public RealtimePerformanceMetrics()
        : this(null)
    {
    }

    // With a GC pause monitor, which it then owns, every interval also counts the pauses one by
    // one.
    internal RealtimePerformanceMetrics(GcPauseMonitor? gcPauseMonitor)
    {
        this.gcPauseMonitor = gcPauseMonitor;
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
        tickPhasePartHistogram = CreateHistogram("simulation_worker.simulation.tick_phase_part.duration");
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
        collisionPendingStepCounter = meter.CreateCounter<long>(
            "simulation_worker.simulation.collision_streaming.pending_steps");
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
    // catch-up budget later absorbs the delay without a resynchronization. gcPause is the
    // garbage collection pause time that fell inside the tick, and the packets are the spawns
    // and despawns it sent. cpuMilliseconds is the loop thread's CPU time inside the tick, null
    // without a CPU clock; the rest of the tick the thread did not run. The tick ends the
    // breakdown of the phases and parts timed since the previous one.
    public void RecordSimulationTick(
        TimeSpan duration,
        TimeSpan budget,
        TimeSpan gcPause = default,
        long spawnPackets = 0,
        long despawnPackets = 0,
        double? cpuMilliseconds = null)
    {
        var milliseconds = Record(simulationTick, simulationTickHistogram, duration);
        var pauseMilliseconds = simulationTickGcPauses.Record(milliseconds, gcPause);
        var cpu = double.NaN;
        if (cpuMilliseconds is { } measured)
        {
            cpu = Math.Clamp(measured, 0d, milliseconds);
            simulationTickCpu.Record(cpu);
            simulationTickNotRunning.Record(milliseconds - cpu);
        }

        slowestTick.EndTick(milliseconds, pauseMilliseconds, spawnPackets, despawnPackets, cpu);
        if (duration > budget)
        {
            Interlocked.Increment(ref tickOverruns);
            tickOverrunCounter.Add(1);
            if (duration - gcPause > budget)
            {
                Interlocked.Increment(ref tickOverrunsWithoutGcPauses);
            }
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
    // composition, so the loop records without allocating. It takes the garbage collection pause
    // time of each sample and has a place in the slowest tick's breakdown.
    internal DurationTimer TickPhaseTimer(string name) =>
        GetOrAddTimer(tickPhaseTimers, tickPhaseHistogram, TickPhaseTag, name, TimerScope.TickPhase);

    // The timer of one part of a tick phase, labeled "<phase>: <part>", for a phase that times
    // where its own time goes; it has a place in the slowest tick's breakdown.
    internal DurationTimer TickPhasePartTimer(string phase, string part) =>
        GetOrAddTimer(tickPhasePartTimers, tickPhasePartHistogram, TickPhasePartTag, $"{phase}: {part}", TimerScope.TickPhasePart);

    // The timer of one step of a loop iteration, labeled with its name.
    internal DurationTimer LoopStepTimer(string name) =>
        GetOrAddTimer(loopStepTimers, loopStepHistogram, LoopStepTag, name, TimerScope.LoopStep);

    // A player or Mob that did not step because collision chunks around it still loaded.
    public void RecordCollisionPendingStep()
    {
        Interlocked.Increment(ref collisionPendingSteps);
        collisionPendingStepCounter.Add(1);
    }

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

    // Each fact is captured whole: a duration with its count, total, maximum, and buckets, and the
    // slowest tick with its phases. The facts of one tick are captured one after another, though,
    // so a tick that ends while this runs may count in this interval for its duration and in the
    // next for its overrun or as the slowest tick.
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
        var tick = simulationTickGcPauses.Attach(simulationTick.CaptureAndReset());
        IReadOnlyList<RealtimeNamedDurationSummary> tickPhases;
        IReadOnlyList<RealtimeNamedDurationSummary> tickPhaseParts;
        IReadOnlyList<RealtimeNamedDurationSummary> loopSteps;
        RealtimeSlowestTick? slowest;
        lock (timerGate)
        {
            tickPhases = CaptureAndReset(tickPhaseTimers);
            tickPhaseParts = CaptureAndReset(tickPhasePartTimers);
            loopSteps = CaptureAndReset(loopStepTimers);
            slowest = slowestTick.TakeInterval(tickPhaseTimers, tickPhasePartTimers);
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
            TickPhaseParts = tickPhaseParts,
            LoopSteps = loopSteps,
            GcPauseMilliseconds = gcPauseMilliseconds,
            GcPauses = gcPauseMonitor?.CaptureAndReset(),
            TickOverrunsWithoutGcPauses = Interlocked.Exchange(ref tickOverrunsWithoutGcPauses, 0),
            SlowestTick = slowest,
            LoopBusyMilliseconds = loopBusyMilliseconds,
            SimulationTickCpu = simulationTickCpu.CaptureAndReset(),
            SimulationTickNotRunning = simulationTickNotRunning.CaptureAndReset(),
            CollisionPendingSteps = Interlocked.Exchange(ref collisionPendingSteps, 0)
        };
    }

    public void Dispose()
    {
        gcPauseMonitor?.Dispose();
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
        string name,
        TimerScope scope)
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

            var inTick = scope != TimerScope.LoopStep;
            var created = new DurationTimer(
                name,
                histogram,
                new KeyValuePair<string, object?>(tagName, name),
                watchesGcPauses: scope == TimerScope.TickPhase,
                inTick ? slowestTick : null,
                inTick ? slowestTick.AddSlot() : -1);
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

    private enum TimerScope
    {
        TickPhase,
        TickPhasePart,
        LoopStep
    }

    // Times one named tick phase, phase part, or loop step into its own accumulator and into the
    // shared histogram under its name tag. A tick phase also counts the garbage collection pauses
    // inside its samples, and a phase or part adds its sample to the running tick's breakdown.
    // Recording takes no lock and allocates nothing.
    internal sealed class DurationTimer
    {
        private readonly DurationAccumulator accumulator = new();
        private readonly GcPauseAccumulator? gcPauses;
        private readonly Histogram<double> histogram;
        private readonly KeyValuePair<string, object?> tag;
        private readonly SlowestTickRecorder? slowestTick;

        internal DurationTimer(
            string name,
            Histogram<double> histogram,
            KeyValuePair<string, object?> tag,
            bool watchesGcPauses,
            SlowestTickRecorder? slowestTick,
            int tickSlot)
        {
            Name = name;
            this.histogram = histogram;
            this.tag = tag;
            this.slowestTick = slowestTick;
            TickSlot = tickSlot;
            gcPauses = watchesGcPauses ? new GcPauseAccumulator() : null;
        }

        public string Name { get; }

        // The timer's place in the slowest tick's breakdown, -1 for none.
        internal int TickSlot { get; }

        public void Record(TimeSpan duration) => Record(duration, TimeSpan.Zero);

        // A sample with the garbage collection pause time that fell inside it, which a timer that
        // does not watch the pauses leaves aside.
        public void Record(TimeSpan duration, TimeSpan gcPause)
        {
            var milliseconds = Math.Max(0d, duration.TotalMilliseconds);
            accumulator.Record(milliseconds);
            histogram.Record(milliseconds, tag);
            gcPauses?.Record(milliseconds, gcPause);
            slowestTick?.Add(TickSlot, milliseconds);
        }

        internal RealtimeDurationSummary? CaptureAndReset()
        {
            var summary = accumulator.CaptureAndReset();
            return gcPauses is null ? summary : gcPauses.Attach(summary);
        }
    }

    private static double Record(
        DurationAccumulator accumulator,
        Histogram<double> histogram,
        TimeSpan duration)
    {
        var milliseconds = Math.Max(0d, duration.TotalMilliseconds);
        accumulator.Record(milliseconds);
        histogram.Record(milliseconds);
        return milliseconds;
    }

    // Counts samples into buckets with Interlocked operations, so the loop never waits for the
    // reporter, and still hands the reporter whole samples: a sample goes into the slot of the
    // interval it was recorded in, and a capture starts the next interval and then waits until
    // every sample that was being recorded has landed, so the count, the total, the maximum,
    // the buckets, and the tagged samples of one capture describe the same samples. Any thread
    // may record; one thread captures at a time.
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

        private readonly Slot[] slots = [new Slot(), new Slot()];
        private readonly Lock captureGate = new();
        private long interval;
        private int recording;

        public void Record(double milliseconds)
        {
            Record(milliseconds, 0);
        }

        // taggedMicroseconds above zero counts the sample as tagged and adds to the tagged total,
        // such as the garbage collection pause inside a sample.
        public void Record(double milliseconds, long taggedMicroseconds)
        {
            var microseconds = (long)Math.Ceiling(milliseconds * 1000d);
            Interlocked.Increment(ref recording);
            var slot = slots[Volatile.Read(ref interval) & 1];
            Interlocked.Increment(ref slot.Buckets[FindBucketIndex(milliseconds)]);
            Interlocked.Add(ref slot.TotalMicroseconds, microseconds);
            RaiseMaximum(ref slot.MaximumMicroseconds, microseconds);
            Interlocked.Increment(ref slot.Samples);
            if (taggedMicroseconds > 0)
            {
                Interlocked.Add(ref slot.TaggedMicroseconds, taggedMicroseconds);
                Interlocked.Increment(ref slot.TaggedSamples);
            }

            Interlocked.Decrement(ref recording);
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
            return CaptureAndReset(out _, out _);
        }

        public RealtimeDurationSummary? CaptureAndReset(out long taggedSamples, out long taggedMicroseconds)
        {
            lock (captureGate)
            {
                // From here on new samples go into the other slot. A sample that read the old
                // interval first is still counted as recording, so the wait covers it.
                var slot = slots[(Interlocked.Increment(ref interval) - 1) & 1];
                var wait = default(SpinWait);
                while (Volatile.Read(ref recording) != 0)
                {
                    wait.SpinOnce();
                }

                var capturedSamples = Interlocked.Exchange(ref slot.Samples, 0);
                var capturedBuckets = new long[slot.Buckets.Length];
                for (var index = 0; index < slot.Buckets.Length; index++)
                {
                    capturedBuckets[index] = Interlocked.Exchange(ref slot.Buckets[index], 0);
                }

                var capturedTotalMicroseconds = Interlocked.Exchange(ref slot.TotalMicroseconds, 0);
                var capturedMaximumMicroseconds = Interlocked.Exchange(ref slot.MaximumMicroseconds, 0);
                taggedSamples = Interlocked.Exchange(ref slot.TaggedSamples, 0);
                taggedMicroseconds = Interlocked.Exchange(ref slot.TaggedMicroseconds, 0);
                if (capturedSamples == 0)
                {
                    return null;
                }

                return new RealtimeDurationSummary(
                    capturedSamples,
                    capturedTotalMicroseconds / 1000d / capturedSamples,
                    FindPercentileUpperBound(capturedBuckets, capturedSamples, 0.95, capturedMaximumMicroseconds),
                    FindPercentileUpperBound(capturedBuckets, capturedSamples, 0.99, capturedMaximumMicroseconds),
                    capturedMaximumMicroseconds / 1000d);
            }
        }

        private static void RaiseMaximum(ref long maximum, long value)
        {
            var observed = Volatile.Read(ref maximum);
            while (value > observed)
            {
                var previous = Interlocked.CompareExchange(ref maximum, value, observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
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

        // The samples of one interval.
        private sealed class Slot
        {
            public readonly long[] Buckets = new long[UpperBoundsMilliseconds.Length];
            public long Samples;
            public long TotalMicroseconds;
            public long MaximumMicroseconds;
            public long TaggedSamples;
            public long TaggedMicroseconds;
        }
    }

    // The garbage collection pauses inside the samples of one duration: the samples a pause fell
    // into, their pause time, and the samples once their own pauses are taken out of them.
    private sealed class GcPauseAccumulator
    {
        private readonly DurationAccumulator withoutPauses = new();

        // Returns the pause time counted inside the sample, at most the sample itself.
        public double Record(double milliseconds, TimeSpan gcPause)
        {
            var pause = Math.Clamp(gcPause.TotalMilliseconds, 0d, milliseconds);
            withoutPauses.Record(milliseconds - pause, pause > 0d ? (long)Math.Ceiling(pause * 1000d) : 0);
            return pause;
        }

        // The summary with the pauses of its interval, which starts the next interval. The
        // samples without their pauses and the paused samples are captured together; the
        // summary itself comes from another accumulator, so a sample recorded between the two
        // captures counts in this interval for one and in the next for the other.
        public RealtimeDurationSummary? Attach(RealtimeDurationSummary? summary)
        {
            var withoutPausesSummary = withoutPauses.CaptureAndReset(
                out var capturedPausedSamples,
                out var capturedPauseMicroseconds);
            return summary is null
                ? null
                : summary with
                {
                    GcPauses = new RealtimeGcPauseShare(
                        capturedPausedSamples,
                        capturedPauseMicroseconds / 1000d,
                        withoutPausesSummary?.P99UpperBoundMilliseconds ?? 0d,
                        withoutPausesSummary?.MaximumMilliseconds ?? 0d)
                };
        }
    }

    // The slowest tick of each metrics interval, with how long each tick phase and phase part took
    // in it. The loop sums what the timers record into the running tick and, at the tick's end,
    // keeps the tick when it is the slowest of the interval so far. Each interval has its own slot,
    // and the loop says that it is ending a tick before it reads which interval runs, so the
    // reporter, which starts the next interval first, waits for a tick that still belongs to the
    // interval it takes and then reads a slot that the loop no longer writes. The loop never
    // waits.
    internal sealed class SlowestTickRecorder
    {
        // Timers beyond it stay out of the breakdown.
        public const int Capacity = 64;

        private readonly double[] running = CreateEmptyTimings();
        private readonly IntervalSlot[] slots = [new(), new()];
        private int assignedSlots;
        private long interval;
        private int endingTick;

        // A place for one more timer, during composition and under the metrics' timer gate.
        public int AddSlot()
        {
            return assignedSlots < Capacity ? assignedSlots++ : -1;
        }

        public void Add(int slot, double milliseconds)
        {
            if ((uint)slot >= Capacity)
            {
                return;
            }

            var value = running[slot];
            running[slot] = double.IsNaN(value) ? milliseconds : value + milliseconds;
        }

        // cpuMilliseconds is NaN without a CPU clock.
        public void EndTick(
            double milliseconds,
            double gcPauseMilliseconds,
            long spawnPackets,
            long despawnPackets,
            double cpuMilliseconds = double.NaN)
        {
            // Said with a full fence before the interval is read: the reporter either sees it and
            // waits, or has started the next interval already, which this tick then reads.
            Interlocked.Exchange(ref endingTick, 1);

            // Slot intervals count from 1, so a slot that was never written matches none.
            var stamp = Volatile.Read(ref interval) + 1;
            var slot = slots[stamp & 1];
            if (slot.Interval != stamp || milliseconds > slot.Milliseconds)
            {
                Interlocked.Increment(ref slot.Version);
                slot.Interval = stamp;
                slot.Milliseconds = milliseconds;
                slot.GcPauseMilliseconds = gcPauseMilliseconds;
                slot.CpuMilliseconds = cpuMilliseconds;
                slot.SpawnPackets = spawnPackets;
                slot.DespawnPackets = despawnPackets;
                Array.Copy(running, slot.Timings, Capacity);
                Interlocked.Increment(ref slot.Version);
            }

            Volatile.Write(ref endingTick, 0);
            Array.Fill(running, double.NaN);
        }

        // Ends the interval and returns its slowest tick with the timers' names, or null when it had
        // no tick. Runs under the metrics' timer gate, so the timer lists hold still.
        public RealtimeSlowestTick? TakeInterval(List<DurationTimer> phases, List<DurationTimer> parts)
        {
            var stamp = Interlocked.Increment(ref interval);
            var slot = slots[stamp & 1];

            // A tick that read the interval before it advanced is still being kept in this slot.
            var ending = default(SpinWait);
            while (Volatile.Read(ref endingTick) != 0)
            {
                ending.SpinOnce();
            }

            var timings = new double[Capacity];
            long slotInterval;
            double milliseconds;
            double gcPauseMilliseconds;
            double cpuMilliseconds;
            long spawnPackets;
            long despawnPackets;
            var spinner = new SpinWait();
            while (true)
            {
                var version = Volatile.Read(ref slot.Version);
                if ((version & 1) == 0)
                {
                    slotInterval = slot.Interval;
                    milliseconds = slot.Milliseconds;
                    gcPauseMilliseconds = slot.GcPauseMilliseconds;
                    cpuMilliseconds = slot.CpuMilliseconds;
                    spawnPackets = slot.SpawnPackets;
                    despawnPackets = slot.DespawnPackets;
                    Array.Copy(slot.Timings, timings, Capacity);
                    Interlocked.MemoryBarrier();
                    if (Volatile.Read(ref slot.Version) == version)
                    {
                        break;
                    }
                }

                spinner.SpinOnce();
            }

            return slotInterval != stamp
                ? null
                : new RealtimeSlowestTick(milliseconds, gcPauseMilliseconds, Named(phases, timings), Named(parts, timings))
                {
                    SpawnPackets = spawnPackets,
                    DespawnPackets = despawnPackets,
                    CpuMilliseconds = double.IsNaN(cpuMilliseconds) ? null : cpuMilliseconds
                };
        }

        private static List<RealtimeNamedMilliseconds> Named(List<DurationTimer> timers, double[] timings)
        {
            var named = new List<RealtimeNamedMilliseconds>();
            foreach (var timer in timers)
            {
                if ((uint)timer.TickSlot < Capacity && !double.IsNaN(timings[timer.TickSlot]))
                {
                    named.Add(new RealtimeNamedMilliseconds(timer.Name, timings[timer.TickSlot]));
                }
            }

            return named;
        }

        private static double[] CreateEmptyTimings()
        {
            var timings = new double[Capacity];
            Array.Fill(timings, double.NaN);
            return timings;
        }

        private sealed class IntervalSlot
        {
            public readonly double[] Timings = CreateEmptyTimings();
            public long Version;
            public long Interval;
            public double Milliseconds;
            public double GcPauseMilliseconds;
            public double CpuMilliseconds = double.NaN;
            public long SpawnPackets;
            public long DespawnPackets;
        }
    }
}
