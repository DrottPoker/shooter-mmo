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

    [Fact]
    public async Task ConcurrentCaptureDoesNotLoseDurationSamples()
    {
        using var metrics = new RealtimePerformanceMetrics();
        const int expectedSamples = 10_000;
        var producer = Task.Run(() =>
        {
            for (var index = 0; index < expectedSamples; index++)
            {
                metrics.RecordNetworkPoll(TimeSpan.FromMilliseconds(0.25));
            }
        }, TestContext.Current.CancellationToken);

        long capturedSamples = 0;
        while (!producer.IsCompleted)
        {
            capturedSamples += metrics.CaptureAndReset().NetworkPoll?.Samples ?? 0;
            await Task.Yield();
        }

        await producer;
        capturedSamples += metrics.CaptureAndReset().NetworkPoll?.Samples ?? 0;
        Assert.Equal(expectedSamples, capturedSamples);
    }
}
