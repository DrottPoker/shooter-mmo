using SimulationWorker.Realtime;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class RealtimePerformanceMetricsTests
{
    [Fact]
    public void CapturesIntervalDurationDistributionAndResets()
    {
        using var metrics = new RealtimePerformanceMetrics();
        metrics.RecordSimulationTick(TimeSpan.FromMilliseconds(1));
        metrics.RecordSimulationTick(TimeSpan.FromMilliseconds(5));
        metrics.RecordSimulationTick(TimeSpan.FromMilliseconds(40));
        metrics.RecordInterestRefresh(TimeSpan.FromMilliseconds(2));
        metrics.RecordJoinQueueDelay(TimeSpan.FromMilliseconds(4));
        metrics.RecordJoinFinalization(TimeSpan.FromMilliseconds(3));
        metrics.ObserveCompletedOperationBacklog(2);
        metrics.ObserveCompletedOperationBacklog(7);
        metrics.ObserveCompletedOperationBacklog(3);
        metrics.RecordTickResynchronization();
        metrics.RecordSnapshotPacketReuse(2, 4, 40);

        var snapshot = metrics.CaptureAndReset();

        Assert.Equal(3, snapshot.SimulationTick!.Samples);
        Assert.Equal(40, snapshot.SimulationTick.MaximumMilliseconds, 3);
        Assert.Equal(50, snapshot.SimulationTick.P95UpperBoundMilliseconds, 3);
        Assert.Equal(1, snapshot.InterestRefresh!.Samples);
        Assert.Equal(1, snapshot.JoinQueueDelay!.Samples);
        Assert.Equal(1, snapshot.JoinFinalization!.Samples);
        Assert.Equal(3, snapshot.CompletedOperationBacklog);
        Assert.Equal(7, snapshot.MaximumCompletedOperationBacklog);
        Assert.Equal(1, snapshot.TickResynchronizations);
        Assert.True(snapshot.ProcessAllocatedBytes >= 0);
        Assert.True(snapshot.Generation0Collections >= 0);
        Assert.True(snapshot.Generation1Collections >= 0);
        Assert.True(snapshot.Generation2Collections >= 0);
        Assert.True(snapshot.ManagedHeapSizeBytes >= 0);
        Assert.True(snapshot.ManagedHeapFragmentedBytes >= 0);
        Assert.True(snapshot.TotalManagedMemoryBytes >= 0);
        Assert.Equal(2, snapshot.SnapshotVisibilityGroups);
        Assert.Equal(4, snapshot.SnapshotPacketsEncoded);
        Assert.Equal(40, snapshot.SnapshotPacketsSent);

        var reset = metrics.CaptureAndReset();
        Assert.Null(reset.SimulationTick);
        Assert.Null(reset.InterestRefresh);
        Assert.Null(reset.JoinQueueDelay);
        Assert.Null(reset.JoinFinalization);
        Assert.Equal(3, reset.CompletedOperationBacklog);
        Assert.Equal(3, reset.MaximumCompletedOperationBacklog);
        Assert.Equal(0, reset.TickResynchronizations);
        Assert.Equal(0, reset.SnapshotVisibilityGroups);
        Assert.Equal(0, reset.SnapshotPacketsEncoded);
        Assert.Equal(0, reset.SnapshotPacketsSent);
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
