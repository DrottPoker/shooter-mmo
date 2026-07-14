using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimulationWorker.Config;

namespace SimulationWorker.Realtime;

public sealed class RealtimeMetricsReporterService(
    RealtimeNetworkMetrics metrics,
    RealtimePerformanceMetrics performanceMetrics,
    SimulationWorkerConfig config,
    ILogger<RealtimeMetricsReporterService> logger) : BackgroundService
{
    private readonly Stopwatch workerUptime = Stopwatch.StartNew();
    private TimeSpan previousProcessorTime = CaptureProcessorTime();
    private TimeSpan previousSampleTime;
    private RealtimeNetworkMetricsSnapshot previousNetwork = metrics.Capture();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(config.NetworkMetricsLogInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var snapshot = metrics.Capture();
            var sampleTime = workerUptime.Elapsed;
            var sampleSeconds = Math.Max(
                double.Epsilon,
                (sampleTime - previousSampleTime).TotalSeconds);
            var processorTime = CaptureProcessorTime();
            var processorSeconds = Math.Max(
                0d,
                (processorTime - previousProcessorTime).TotalSeconds);
            var logicalProcessorCount = Math.Max(1, Environment.ProcessorCount);
            var singleCoreCpuPercent = Math.Min(
                logicalProcessorCount * 100d,
                processorSeconds / sampleSeconds * 100d);
            var machineCpuPercent = Math.Min(
                100d,
                singleCoreCpuPercent / logicalProcessorCount);
            using var process = Process.GetCurrentProcess();

            logger.LogInformation(
                "[SIMULATION] Worker status: players {ActiveRealPlayers}, bots {ActiveBots}, unauthenticated peers {UnauthenticatedPeers}, connected peers {ConnectedPeers}, entities {ActiveEntities}; process CPU {MachineCpuPercent:F1}% of total logical-core capacity and {SingleCoreCpuPercent:F1}% single-core equivalent; working set {WorkingSetMiB:F1} MiB; traffic {ReceivedPacketsPerSecond:F1} received packets/s at {ReceivedKiBPerSecond:F1} KiB/s and {SentPacketsPerSecond:F1} sent packets/s at {SentKiBPerSecond:F1} KiB/s; snapshot drops {SnapshotDropsPerSecond:F1}/s and quota rejects {QuotaRejectsPerSecond:F1}/s over {SampleSeconds:F1} seconds.",
                snapshot.ActiveRealPlayers,
                snapshot.ActiveSyntheticBots,
                snapshot.UnauthenticatedPeers,
                snapshot.ActivePeers,
                snapshot.ActiveEntities,
                machineCpuPercent,
                singleCoreCpuPercent,
                process.WorkingSet64 / 1024d / 1024d,
                PerSecond(snapshot.ReceivedPackets - previousNetwork.ReceivedPackets, sampleSeconds),
                PerSecond(snapshot.ReceivedBytes - previousNetwork.ReceivedBytes, sampleSeconds) / 1024d,
                PerSecond(snapshot.SentPackets - previousNetwork.SentPackets, sampleSeconds),
                PerSecond(snapshot.SentBytes - previousNetwork.SentBytes, sampleSeconds) / 1024d,
                PerSecond(
                    snapshot.DroppedSnapshotPackets - previousNetwork.DroppedSnapshotPackets,
                    sampleSeconds),
                PerSecond(
                    snapshot.QuotaRejectedPackets - previousNetwork.QuotaRejectedPackets,
                    sampleSeconds),
                sampleSeconds);

            logger.LogInformation(
                "[SIMULATION] Network metrics: peers {ActivePeers}, entities {ActiveEntities}, received {ReceivedPackets} packets and {ReceivedBytes} bytes, sent {SentPackets} packets and {SentBytes} bytes, quota rejects {QuotaRejects}, snapshot drops {SnapshotDrops}, including {SnapshotBackpressureDrops} from aggregate backpressure, joins {AcceptedJoins} accepted and {RejectedJoins} rejected, lifecycle {SpawnPackets} spawns and {DespawnPackets} despawns, snapshot entity records {SnapshotEntityRecords}.",
                snapshot.ActivePeers,
                snapshot.ActiveEntities,
                snapshot.ReceivedPackets,
                snapshot.ReceivedBytes,
                snapshot.SentPackets,
                snapshot.SentBytes,
                snapshot.QuotaRejectedPackets,
                snapshot.DroppedSnapshotPackets,
                snapshot.BackpressureDroppedSnapshotPackets,
                snapshot.AcceptedJoins,
                snapshot.RejectedJoins,
                snapshot.SpawnPackets,
                snapshot.DespawnPackets,
                snapshot.SnapshotEntityRecords);

            var performance = performanceMetrics.CaptureAndReset();
            logger.LogInformation(
                "[SIMULATION] Performance metrics for the interval: network poll {NetworkPoll}; completed operations {CompletedOperations}; simulation tick {SimulationTick}; tick lag {TickLag}; collision streaming {CollisionStreaming}; movement {Movement}; interest {Interest}; snapshot broadcast {SnapshotBroadcast}; tick resynchronizations {TickResynchronizations}; process allocated {ProcessAllocatedMiB:F3} MiB; GC collections gen0 {Generation0Collections}, gen1 {Generation1Collections}, gen2 {Generation2Collections}; managed heap {ManagedHeapMiB:F3} MiB, fragmented {ManagedHeapFragmentedMiB:F3} MiB, live managed memory {TotalManagedMemoryMiB:F3} MiB; snapshot visibility groups {SnapshotVisibilityGroups}, packets encoded {SnapshotPacketsEncoded}, packets sent {SnapshotPacketsSent}.",
                Format(performance.NetworkPoll),
                Format(performance.CompletedOperations),
                Format(performance.SimulationTick),
                Format(performance.SimulationTickLag),
                Format(performance.CollisionStreaming),
                Format(performance.MovementSimulation),
                Format(performance.InterestRefresh),
                Format(performance.SnapshotBroadcast),
                performance.TickResynchronizations,
                performance.ProcessAllocatedBytes / 1024d / 1024d,
                performance.Generation0Collections,
                performance.Generation1Collections,
                performance.Generation2Collections,
                performance.ManagedHeapSizeBytes / 1024d / 1024d,
                performance.ManagedHeapFragmentedBytes / 1024d / 1024d,
                performance.TotalManagedMemoryBytes / 1024d / 1024d,
                performance.SnapshotVisibilityGroups,
                performance.SnapshotPacketsEncoded,
                performance.SnapshotPacketsSent);

            previousSampleTime = sampleTime;
            previousProcessorTime = processorTime;
            previousNetwork = snapshot;
        }
    }

    private static TimeSpan CaptureProcessorTime()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    private static double PerSecond(long delta, double seconds)
    {
        return Math.Max(0, delta) / seconds;
    }

    private static string Format(RealtimeDurationSummary? summary)
    {
        return summary is null
            ? "no samples"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{summary.Samples} samples, avg {summary.AverageMilliseconds:F3} ms, p95 <= {summary.P95UpperBoundMilliseconds:F3} ms, p99 <= {summary.P99UpperBoundMilliseconds:F3} ms, max {summary.MaximumMilliseconds:F3} ms");
    }
}
