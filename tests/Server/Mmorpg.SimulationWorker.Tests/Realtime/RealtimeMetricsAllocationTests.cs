using Mmorpg.Server.Telemetry;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The realtime loop records these metrics many times per fixed tick. With the production
// OpenTelemetry views and an exporter attached, recording must still not allocate, or
// enabling export would add GC pressure to the loop.
public sealed class RealtimeMetricsAllocationTests
{
    private static readonly TimeSpan TickBudget = TimeSpan.FromMilliseconds(1000d / 30d);

    [Fact]
    public void HotPathRecordingDoesNotAllocateWithExportAttached()
    {
        var exported = new List<Metric>();
        using var performance = new RealtimePerformanceMetrics();
        using var network = new RealtimeNetworkMetrics();
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddServerMetrics(SimulationWorkerTelemetry.MeterNames)
            .AddInMemoryExporter(exported)
            .Build();

        // The first measurement of each stream sets up its metric point.
        RecordHotPath(performance, network, 100);
        provider.ForceFlush();
        RecordHotPath(performance, network, 100);

        // A listener that another test left subscribed creates its metric points on this
        // thread once, so one quiet pass proves steady recording; an allocation in the
        // recording itself repeats in every pass.
        var allocated = new List<long>();
        for (var pass = 0; pass < 3 && !allocated.Contains(0); pass++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            RecordHotPath(performance, network, 10_000);
            allocated.Add(GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Contains(0L, allocated);
        provider.ForceFlush();
        Assert.Contains(exported, metric => metric.Name == "simulation_worker.simulation.tick.duration");
        Assert.Contains(exported, metric => metric.Name == "simulation_worker.simulation.tick.overruns");
        Assert.Contains(exported, metric => metric.Name == "simulation_worker.realtime.snapshot.bytes");
    }

    private static void RecordHotPath(
        RealtimePerformanceMetrics performance,
        RealtimeNetworkMetrics network,
        int iterations)
    {
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            // 0 to 49 ms, so every bucket and the overrun counter see samples.
            var duration = TimeSpan.FromMilliseconds(iteration % 50);
            performance.RecordNetworkPoll(duration);
            performance.RecordCompletedOperations(duration);
            performance.ObserveCompletedOperationBacklog(iteration % 4);
            performance.RecordSimulationTickLag(duration);
            performance.RecordCollisionStreaming(duration);
            performance.RecordMovementSimulation(duration);
            performance.RecordInterestRefresh(duration);
            performance.RecordSnapshotBroadcast(duration);
            performance.RecordSnapshotPacketReuse(2, 4, 8, 0);
            performance.RecordSimulationTick(duration, TickBudget);
            network.RecordSent(120);
            network.RecordReceived(64);
            network.RecordSnapshotEntities(3);
            network.RecordSnapshotBytes(240);
        }
    }
}
