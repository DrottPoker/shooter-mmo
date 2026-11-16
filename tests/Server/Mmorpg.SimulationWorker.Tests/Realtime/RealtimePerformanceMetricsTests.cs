using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

public sealed class RealtimePerformanceMetricsTests
{
    [Fact]
    public void CapturesIntervalDurationDistributionAndResets()
    {
        using var metrics = new RealtimePerformanceMetrics();
        var tickBudget = TimeSpan.FromMilliseconds(1000d / 30d);
        metrics.RecordSimulationTick(TimeSpan.FromMilliseconds(1), tickBudget);
        metrics.RecordSimulationTick(TimeSpan.FromMilliseconds(5), tickBudget);
        metrics.RecordSimulationTick(TimeSpan.FromMilliseconds(40), tickBudget);
        metrics.RecordInterestRefresh(TimeSpan.FromMilliseconds(2));
        metrics.RecordJoinQueueDelay(TimeSpan.FromMilliseconds(4));
        metrics.RecordJoinFinalization(TimeSpan.FromMilliseconds(3));
        metrics.ObserveCompletedOperationBacklog(2);
        metrics.ObserveCompletedOperationBacklog(7);
        metrics.ObserveCompletedOperationBacklog(3);
        metrics.ObserveCorpseMutationCoordinator(4, 2);
        metrics.ObserveCorpseMutationCoordinator(9, 3);
        metrics.ObserveCorpseMutationCoordinator(5, 2);
        metrics.RecordTickResynchronization();
        metrics.RecordSnapshotSends(2, 4, 40, 80);

        var snapshot = metrics.CaptureAndReset();

        Assert.Equal(3, snapshot.SimulationTick!.Samples);
        Assert.Equal(40, snapshot.SimulationTick.MaximumMilliseconds, 3);
        Assert.Equal(50, snapshot.SimulationTick.P95UpperBoundMilliseconds, 3);
        Assert.Equal(1, snapshot.InterestRefresh!.Samples);
        Assert.Equal(1, snapshot.JoinQueueDelay!.Samples);
        Assert.Equal(1, snapshot.JoinFinalization!.Samples);
        Assert.Equal(3, snapshot.CompletedOperationBacklog);
        Assert.Equal(7, snapshot.MaximumCompletedOperationBacklog);
        Assert.Equal(5, snapshot.CorpseMutationBacklog);
        Assert.Equal(9, snapshot.MaximumCorpseMutationBacklog);
        Assert.Equal(2, snapshot.ActiveCorpseMutations);
        Assert.Equal(1, snapshot.TickResynchronizations);
        Assert.Equal(1, snapshot.TickOverruns);
        Assert.True(snapshot.ProcessAllocatedBytes >= 0);
        Assert.True(snapshot.Generation0Collections >= 0);
        Assert.True(snapshot.Generation1Collections >= 0);
        Assert.True(snapshot.Generation2Collections >= 0);
        Assert.True(snapshot.ManagedHeapSizeBytes >= 0);
        Assert.True(snapshot.ManagedHeapFragmentedBytes >= 0);
        Assert.True(snapshot.TotalManagedMemoryBytes >= 0);
        Assert.Equal(2, snapshot.SnapshotRemoteRecipients);
        Assert.Equal(4, snapshot.SnapshotPacketsEncoded);
        Assert.Equal(40, snapshot.SnapshotPacketsSent);
        Assert.Equal(80, snapshot.SnapshotEntityUpdatesDeferred);

        var reset = metrics.CaptureAndReset();
        Assert.Null(reset.SimulationTick);
        Assert.Null(reset.InterestRefresh);
        Assert.Null(reset.JoinQueueDelay);
        Assert.Null(reset.JoinFinalization);
        Assert.Equal(3, reset.CompletedOperationBacklog);
        Assert.Equal(3, reset.MaximumCompletedOperationBacklog);
        Assert.Equal(5, reset.CorpseMutationBacklog);
        Assert.Equal(5, reset.MaximumCorpseMutationBacklog);
        Assert.Equal(2, reset.ActiveCorpseMutations);
        Assert.Equal(0, reset.TickResynchronizations);
        Assert.Equal(0, reset.TickOverruns);
        Assert.Equal(0, reset.SnapshotRemoteRecipients);
        Assert.Equal(0, reset.SnapshotPacketsEncoded);
        Assert.Equal(0, reset.SnapshotPacketsSent);
        Assert.Equal(0, reset.SnapshotEntityUpdatesDeferred);
    }

    // A GC pause inside a tick or phase makes it look slow, so both say how much of them was
    // pause and how they look without it.
    [Fact]
    public void TicksAndPhasesCountTheGcPausesInsideThem()
    {
        using var metrics = new RealtimePerformanceMetrics();
        var tickBudget = TimeSpan.FromMilliseconds(1000d / 30d);
        var snapshots = metrics.TickPhaseTimer("snapshots");
        var sendFlush = metrics.LoopStepTimer("send flush");
        snapshots.Record(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(15));
        snapshots.Record(TimeSpan.FromMilliseconds(6), TimeSpan.Zero);
        sendFlush.Record(TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(2));
        // Over the budget only with its pause, and over it either way.
        metrics.RecordSimulationTick(TimeSpan.FromMilliseconds(40), tickBudget, TimeSpan.FromMilliseconds(15));
        metrics.RecordSimulationTick(TimeSpan.FromMilliseconds(45), tickBudget, TimeSpan.FromMilliseconds(5));
        metrics.RecordSimulationTick(TimeSpan.FromMilliseconds(4), tickBudget);

        var snapshot = metrics.CaptureAndReset();

        var tick = snapshot.SimulationTick!.GcPauses!;
        Assert.Equal(2, tick.PausedSamples);
        Assert.Equal(20, tick.PauseMilliseconds, 3);
        Assert.Equal(40, tick.MaximumWithoutPausesMilliseconds, 3);
        Assert.Equal(50, tick.P99UpperBoundWithoutPausesMilliseconds);
        Assert.Equal(2, snapshot.TickOverruns);
        Assert.Equal(1, snapshot.TickOverrunsWithoutGcPauses);
        var phase = Assert.Single(snapshot.TickPhases).Summary;
        Assert.Equal(20, phase.MaximumMilliseconds, 3);
        Assert.Equal(1, phase.GcPauses!.PausedSamples);
        Assert.Equal(15, phase.GcPauses.PauseMilliseconds, 3);
        Assert.Equal(6, phase.GcPauses.MaximumWithoutPausesMilliseconds, 3);
        Assert.Equal(8, phase.GcPauses.P99UpperBoundWithoutPausesMilliseconds);
        // A loop step is not watched for pauses.
        Assert.Null(Assert.Single(snapshot.LoopSteps).Summary.GcPauses);

        var reset = metrics.CaptureAndReset();
        Assert.Null(reset.SimulationTick);
        Assert.Equal(0, reset.TickOverrunsWithoutGcPauses);
    }

    // The slowest tick of each interval keeps how long each phase and phase part took in it; a
    // loop step outside the tick is no part of it.
    [Fact]
    public void TheSlowestTickOfAnIntervalKeepsItsPhasesAndParts()
    {
        using var metrics = new RealtimePerformanceMetrics();
        var tickBudget = TimeSpan.FromMilliseconds(1000d / 30d);
        var movement = metrics.TickPhaseTimer("movement");
        var snapshots = metrics.TickPhaseTimer("snapshots");
        var plans = metrics.TickPhasePartTimer("snapshots", "remote plans");
        var networkPoll = metrics.LoopStepTimer("network poll");
        movement.Record(TimeSpan.FromMilliseconds(2));
        metrics.RecordSimulationTick(TimeSpan.FromMilliseconds(3), tickBudget);
        movement.Record(TimeSpan.FromMilliseconds(3));
        snapshots.Record(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(4));
        plans.Record(TimeSpan.FromMilliseconds(12));
        networkPoll.Record(TimeSpan.FromMilliseconds(9));
        metrics.RecordSimulationTick(TimeSpan.FromMilliseconds(25), tickBudget, TimeSpan.FromMilliseconds(4));
        movement.Record(TimeSpan.FromMilliseconds(1));
        metrics.RecordSimulationTick(TimeSpan.FromMilliseconds(2), tickBudget);

        var snapshot = metrics.CaptureAndReset();

        var slowest = snapshot.SlowestTick!;
        Assert.Equal(25, slowest.Milliseconds, 3);
        Assert.Equal(4, slowest.GcPauseMilliseconds, 3);
        Assert.Equal([("movement", 3d), ("snapshots", 20d)], slowest.Phases.Select(phase => (phase.Name, phase.Milliseconds)));
        Assert.Equal([("snapshots: remote plans", 12d)], slowest.PhaseParts.Select(part => (part.Name, part.Milliseconds)));
        Assert.Equal("snapshots: remote plans", Assert.Single(snapshot.TickPhaseParts).Name);

        // An interval without a tick has none, and the next tick starts the next interval's own.
        Assert.Null(metrics.CaptureAndReset().SlowestTick);
        movement.Record(TimeSpan.FromMilliseconds(1));
        metrics.RecordSimulationTick(TimeSpan.FromMilliseconds(1.5), tickBudget);
        var next = metrics.CaptureAndReset().SlowestTick!;
        Assert.Equal(1.5, next.Milliseconds, 3);
        Assert.Equal([("movement", 1d)], next.Phases.Select(phase => (phase.Name, phase.Milliseconds)));
        Assert.Empty(next.PhaseParts);
    }

    // The monitor reads the runtime's own GC events, which arrive on the runtime's event thread
    // a little after each collection.
    [Fact]
    public async Task TheGcPauseMonitorCountsThePausesOfTheProcess()
    {
        using var metrics = new RealtimePerformanceMetrics(new GcPauseMonitor());
        long generation0Pauses = 0;
        long pauses = 0;
        double longestPause = 0;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (generation0Pauses == 0 && waited.Elapsed < TimeSpan.FromSeconds(20))
        {
            GC.Collect(0, GCCollectionMode.Forced, blocking: true);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            var counts = metrics.CaptureAndReset().GcPauses!;
            generation0Pauses += counts.Generation0Pauses;
            pauses += counts.Pauses;
            longestPause = Math.Max(longestPause, counts.LongestPauseMilliseconds);
            Assert.Equal(counts.Generation0Pauses + counts.Generation1Pauses + counts.Generation2Pauses, counts.Pauses);
        }

        Assert.True(generation0Pauses > 0, "No generation 0 pause arrived within 20 seconds.");
        Assert.True(pauses >= generation0Pauses);
        Assert.True(longestPause > 0d);
        Assert.Contains("latency mode", GcPauseMonitor.DescribeConfiguration(), StringComparison.Ordinal);
    }

    // Each tick is slower than every tick before it, and the loop ends the next one only after
    // the reporter took two more intervals, so every tick is the slowest of the interval it
    // falls into and must come back from exactly one of them.
    [Fact]
    public async Task NoIntervalLosesATickThatEndsWhileItIsTaken()
    {
        var recorder = new RealtimePerformanceMetrics.SlowestTickRecorder();
        var phases = new List<RealtimePerformanceMetrics.DurationTimer>();
        var parts = new List<RealtimePerformanceMetrics.DurationTimer>();
        const int ticks = 20_000;
        long taken = 0;
        var stop = 0;
        var seen = new bool[ticks + 1];
        var cancellationToken = TestContext.Current.CancellationToken;
        var reporter = Task.Run(
            () =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    if (recorder.TakeInterval(phases, parts) is { } slowest)
                    {
                        var index = (int)slowest.Milliseconds;
                        Assert.False(seen[index], $"Tick {index} came back twice.");
                        seen[index] = true;
                    }

                    Interlocked.Increment(ref taken);
                }
            },
            cancellationToken);

        try
        {
            for (var tick = 1; tick <= ticks; tick++)
            {
                recorder.EndTick(tick, 0d, 0, 0);
                var after = Volatile.Read(ref taken) + 2;
                var wait = default(SpinWait);
                while (Volatile.Read(ref taken) < after)
                {
                    Assert.False(reporter.IsCompleted, "The reporter stopped early.");
                    wait.SpinOnce();
                }
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            await reporter;
        }

        for (var tick = 1; tick <= ticks; tick++)
        {
            Assert.True(seen[tick], $"Tick {tick} was lost.");
        }
    }

    // Every sample is 0.25 ms, so a capture that held a sample's duration without its count, or
    // its count without its duration, would show another average or maximum. The producers go on
    // until three captures ran, so captures run while they record also on a busy machine, where
    // the producers could otherwise finish before the capturing loop ran twice.
    [Fact]
    public async Task ConcurrentCaptureHandsOutWholeDurationSamples()
    {
        using var metrics = new RealtimePerformanceMetrics();
        const int producers = 4;
        const int samplesPerProducer = 200_000;
        const int capturesWhileRecording = 3;
        var cancellationToken = TestContext.Current.CancellationToken;
        var produced = new long[producers];
        var captures = 0;
        var producing = Enumerable.Range(0, producers)
            .Select(producer => Task.Run(
                () =>
                {
                    long count = 0;
                    while (count < samplesPerProducer || Volatile.Read(ref captures) < capturesWhileRecording)
                    {
                        metrics.RecordNetworkPoll(TimeSpan.FromMilliseconds(0.25));
                        count++;
                    }

                    produced[producer] = count;
                },
                cancellationToken))
            .ToArray();
        var all = Task.WhenAll(producing);

        long capturedSamples = 0;
        while (!all.IsCompleted)
        {
            capturedSamples += AssertWholeSamples(metrics.CaptureAndReset().NetworkPoll);
            Interlocked.Increment(ref captures);
            await Task.Yield();
        }

        await all;
        capturedSamples += AssertWholeSamples(metrics.CaptureAndReset().NetworkPoll);
        Assert.Equal(produced.Sum(), capturedSamples);
        Assert.True(captures >= capturesWhileRecording, "Too few captures ran while the samples were recorded.");
    }

    private static long AssertWholeSamples(RealtimeDurationSummary? summary)
    {
        if (summary is null)
        {
            return 0;
        }

        Assert.Equal(0.25, summary.AverageMilliseconds, 9);
        Assert.Equal(0.25, summary.MaximumMilliseconds, 9);
        Assert.Equal(0.25, summary.P95UpperBoundMilliseconds, 9);
        Assert.Equal(0.25, summary.P99UpperBoundMilliseconds, 9);
        return summary.Samples;
    }
}
