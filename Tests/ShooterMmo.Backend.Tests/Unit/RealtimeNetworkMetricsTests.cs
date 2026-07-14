using SimulationWorker.Realtime;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class RealtimeNetworkMetricsTests
{
    [Fact]
    public void CaptureIncludesTransportPopulationAndTrafficCounters()
    {
        using var metrics = new RealtimeNetworkMetrics();
        metrics.SetActivePeers(8);
        metrics.SetActiveEntities(7);
        metrics.SetPeerPopulation(2, 5, 1);
        metrics.RecordReceived(120);
        metrics.RecordSent(80);
        metrics.RecordSnapshotDropped(3);
        metrics.RecordQuotaRejected();

        var snapshot = metrics.Capture();

        Assert.Equal(8, snapshot.ActivePeers);
        Assert.Equal(7, snapshot.ActiveEntities);
        Assert.Equal(2, snapshot.ActiveRealPlayers);
        Assert.Equal(5, snapshot.ActiveSyntheticBots);
        Assert.Equal(1, snapshot.UnauthenticatedPeers);
        Assert.Equal(1, snapshot.ReceivedPackets);
        Assert.Equal(120, snapshot.ReceivedBytes);
        Assert.Equal(1, snapshot.SentPackets);
        Assert.Equal(80, snapshot.SentBytes);
        Assert.Equal(3, snapshot.DroppedSnapshotPackets);
        Assert.Equal(1, snapshot.QuotaRejectedPackets);
    }
}
