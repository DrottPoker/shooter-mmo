using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorldServer.Config;

namespace WorldServer.Realtime;

public sealed class RealtimeMetricsReporterService(
    RealtimeNetworkMetrics metrics,
    WorldServerConfig config,
    ILogger<RealtimeMetricsReporterService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(config.NetworkMetricsLogInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var snapshot = metrics.Capture();
            logger.LogInformation(
                "[WORLDSERVER] Network metrics: peers {ActivePeers}, entities {ActiveEntities}, received {ReceivedPackets} packets and {ReceivedBytes} bytes, sent {SentPackets} packets and {SentBytes} bytes, quota rejects {QuotaRejects}, snapshot drops {SnapshotDrops}, joins {AcceptedJoins} accepted and {RejectedJoins} rejected, lifecycle {SpawnPackets} spawns and {DespawnPackets} despawns, snapshot entity records {SnapshotEntityRecords}.",
                snapshot.ActivePeers,
                snapshot.ActiveEntities,
                snapshot.ReceivedPackets,
                snapshot.ReceivedBytes,
                snapshot.SentPackets,
                snapshot.SentBytes,
                snapshot.QuotaRejectedPackets,
                snapshot.DroppedSnapshotPackets,
                snapshot.AcceptedJoins,
                snapshot.RejectedJoins,
                snapshot.SpawnPackets,
                snapshot.DespawnPackets,
                snapshot.SnapshotEntityRecords);
        }
    }
}
