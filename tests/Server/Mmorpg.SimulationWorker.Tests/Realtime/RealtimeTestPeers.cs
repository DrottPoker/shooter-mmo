using System.Diagnostics.CodeAnalysis;
using LiteNetLib;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

internal sealed record RecordedPeerSend(byte[] Data, DeliveryMethod Delivery)
{
    public RealtimeMessageType Type
    {
        get
        {
            Assert.True(RealtimeProtocol.TryReadMessageType(Data, out var type));
            return type;
        }
    }
}

// An in-memory peer that records every packet the realtime loop sends to it.
internal sealed class RecordingPeerConnection(int id) : IRealtimePeerConnection
{
    public int Id { get; } = id;

    public List<RecordedPeerSend> Sent { get; } = [];

    public int QueuedReliablePackets { get; set; }

    public void Send(byte[] data, DeliveryMethod deliveryMethod)
    {
        Sent.Add(new RecordedPeerSend(data, deliveryMethod));
    }

    public int GetPacketsCountInReliableQueue(byte channelNumber, bool ordered)
    {
        return QueuedReliablePackets;
    }
}

// Resolves recording peers by id. A test reuses an id by replacing the entry, which is
// how LiteNetLib hands a freed id to a new connection.
internal sealed class RecordingPeerDirectory : IRealtimePeerDirectory
{
    public Dictionary<int, RecordingPeerConnection> Peers { get; } = [];

    public List<int> Disconnected { get; } = [];

    public bool TryGetConnectedPeer(
        int peerId,
        [NotNullWhen(true)] out IRealtimePeerConnection? peer)
    {
        peer = Peers.TryGetValue(peerId, out var connection) ? connection : null;
        return peer is not null;
    }

    public void DisconnectPeer(IRealtimePeerConnection peer)
    {
        Disconnected.Add(peer.Id);
        Peers.Remove(peer.Id);
    }
}

internal static class RealtimeTestSessions
{
    public static ActiveSimulationSession Create(string name = "Seam Hero")
    {
        return new ActiveSimulationSession(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            name,
            RealtimeServerHarness.ShardId,
            RealtimeServerHarness.WorldId,
            RealtimeServerHarness.WorkerId,
            RealtimeServerHarness.RuntimeId,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(5),
            new PlayerCarryState(0, 0, 200),
            false);
    }
}
