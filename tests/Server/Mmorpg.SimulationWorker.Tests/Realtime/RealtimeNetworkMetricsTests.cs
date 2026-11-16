using System.Diagnostics.Metrics;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

public sealed class RealtimeNetworkMetricsTests
{
    // The type byte follows the four magic and the two version bytes of the header.
    private const int TypeByteOffset = 6;

    [Fact]
    public void CaptureIncludesTransportPopulationAndTrafficCounters()
    {
        using var metrics = new RealtimeNetworkMetrics();
        metrics.SetActivePeers(8);
        metrics.SetActiveEntities(7);
        metrics.SetPeerPopulation(2, 5, 1);
        metrics.RecordReceived(new byte[120]);
        metrics.RecordSent(new byte[80]);
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

    // Every message counts under the type byte of its header, beside the unchanged totals. An
    // empty packet and one without a readable type count under byte 0, which has no type name.
    [Fact]
    public void MessagesCountByTheKindTheirHeaderNames()
    {
        using var metrics = new RealtimeNetworkMetrics();
        var disconnect = RealtimeProtocol.EncodeServerDisconnect("code", "message");
        var leaveAccepted = RealtimeProtocol.EncodeLeaveAccepted(30);
        var joinRequest = RealtimeProtocol.EncodeJoinRequest("ticket");
        var undefinedType = RealtimeProtocol.EncodeJoinRequest("ticket");
        undefinedType[TypeByteOffset] = 200;

        metrics.RecordSent(disconnect);
        metrics.RecordSent(leaveAccepted);
        metrics.RecordSent(disconnect);
        metrics.RecordSent([]);
        metrics.RecordReceived(joinRequest);
        metrics.RecordReceived(undefinedType);
        metrics.RecordReceived(new byte[3]);

        var snapshot = metrics.Capture();

        Assert.Equal(
            [
                new RealtimeMessageKindTraffic(0, 1, 0),
                new RealtimeMessageKindTraffic((byte)RealtimeMessageType.LeaveAccepted, 1, leaveAccepted.Length),
                new RealtimeMessageKindTraffic((byte)RealtimeMessageType.ServerDisconnect, 2, 2L * disconnect.Length)
            ],
            snapshot.SentByMessage);
        Assert.Equal(
            [
                new RealtimeMessageKindTraffic(0, 2, undefinedType.Length + 3L),
                new RealtimeMessageKindTraffic((byte)RealtimeMessageType.JoinRequest, 1, joinRequest.Length)
            ],
            snapshot.ReceivedByMessage);
        Assert.Equal(["0", "LeaveAccepted", "ServerDisconnect"], snapshot.SentByMessage.Select(kind => kind.Name));
        Assert.Equal("200", new RealtimeMessageKindTraffic(200, 1, 1).Name);
        Assert.Equal(4, snapshot.SentPackets);
        Assert.Equal(leaveAccepted.Length + 2L * disconnect.Length, snapshot.SentBytes);
        Assert.Equal(3, snapshot.ReceivedPackets);
        Assert.Equal(joinRequest.Length + undefinedType.Length + 3L, snapshot.ReceivedBytes);
    }

    // The interval holds only the kinds with a message since the previous capture.
    [Fact]
    public void TheIntervalHoldsTheMessagesOfEveryKindSinceThePreviousCapture()
    {
        using var metrics = new RealtimeNetworkMetrics();
        var disconnect = RealtimeProtocol.EncodeServerDisconnect("code", "message");
        var leaveAccepted = RealtimeProtocol.EncodeLeaveAccepted(30);
        metrics.RecordSent(disconnect);
        metrics.RecordSent(leaveAccepted);
        var previous = metrics.Capture();
        metrics.RecordSent(leaveAccepted);
        metrics.RecordSent(leaveAccepted);
        metrics.RecordReceived(new byte[5]);

        var delta = RealtimeMetricsInterval.Subtract(metrics.Capture(), previous);

        var sent = Assert.Single(delta.SentByMessage);
        Assert.Equal(
            new RealtimeMessageKindTraffic((byte)RealtimeMessageType.LeaveAccepted, 2, 2L * leaveAccepted.Length),
            sent);
        Assert.Equal(new RealtimeMessageKindTraffic(0, 1, 5), Assert.Single(delta.ReceivedByMessage));
        Assert.Equal(2, delta.SentPackets);
    }

    // The tagged counters report every kind with a message, by its type name.
    [Fact]
    public void TheMessageCountersReportEveryKindByItsName()
    {
        // Other tests create metrics under the same meter name at the same time, so only the
        // instruments that this thread creates below are listened to.
        var testThread = Environment.CurrentManagedThreadId;
        var creating = false;
        var measured = new Dictionary<(string Instrument, string? Kind), long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (creating
                    && Environment.CurrentManagedThreadId == testThread
                    && instrument.Name.EndsWith(".messages", StringComparison.Ordinal))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? kind = null;
            foreach (var tag in tags)
            {
                if (tag.Key == RealtimeNetworkMetrics.MessageTagName)
                {
                    kind = (string?)tag.Value;
                }
            }

            measured[(instrument.Name, kind)] = value;
        });
        listener.Start();
        creating = true;
        using var metrics = new RealtimeNetworkMetrics();
        creating = false;
        metrics.RecordSent(RealtimeProtocol.EncodeServerDisconnect("code", "message"));
        metrics.RecordSent(RealtimeProtocol.EncodeServerDisconnect("code", "message"));
        metrics.RecordReceived([]);

        listener.RecordObservableInstruments();

        Assert.Equal(
            new Dictionary<(string Instrument, string? Kind), long>
            {
                [("simulation_worker.realtime.sent.messages", "ServerDisconnect")] = 2,
                [("simulation_worker.realtime.received.messages", "0")] = 1
            },
            measured);
    }
}
