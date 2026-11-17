using System.Diagnostics.CodeAnalysis;
using LiteNetLib;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

internal sealed record RecordedPeerSend(byte[] Data, byte Channel, DeliveryMethod Delivery)
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

// An in-memory peer that records every packet the realtime loop sends to it. A peer that the
// test joins directly is sealed, as every joined peer is; one that joins through the join route
// is sealed when the join installs its keys.
internal sealed class RecordingPeerConnection(int id, bool isSealed = false) : IRealtimePeerConnection
{
    private readonly List<RealtimePendingDisconnect> unacknowledgedReasons = [];

    public int Id { get; } = id;

    public bool IsSealed { get; private set; } = isSealed;

    // How often a join installed the peer's keys.
    public int SealCount { get; private set; }

    public void Seal(RealtimeTransportKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Assert.Equal(RealtimeAead.KeySize, keys.ClientToWorkerKey.Length);
        IsSealed = true;
        SealCount++;
    }

    public List<RecordedPeerSend> Sent { get; } = [];

    // LiteNetLib's outgoing queue of each reliable channel.
    public Dictionary<byte, int> QueuedReliablePackets { get; } = [];

    // Makes every unreliable send throw, as a broken connection might.
    public bool FailUnreliableSends { get; set; }

    // Makes every reliable send throw.
    public bool FailReliableSends { get; set; }

    public void Send(byte[] data, byte channel, DeliveryMethod deliveryMethod)
    {
        if (FailUnreliableSends && deliveryMethod == DeliveryMethod.Unreliable)
        {
            throw new InvalidOperationException("The unreliable send broke.");
        }

        if (FailReliableSends && deliveryMethod == DeliveryMethod.ReliableOrdered)
        {
            throw new InvalidOperationException("The reliable send broke.");
        }

        Sent.Add(new RecordedPeerSend(data, channel, deliveryMethod));
    }

    // Keeps a copy, since the sender reuses its buffer.
    public void Send(ReadOnlySpan<byte> data, DeliveryMethod deliveryMethod)
    {
        Send(data.ToArray(), RealtimeProtocol.UnreliableReceiveChannel, deliveryMethod);
    }

    // Records the reason like any reliable send; AcknowledgeReasons plays the client's
    // acknowledgement.
    public void SendDisconnectReason(byte[] data, byte channel, RealtimePendingDisconnect disconnect)
    {
        Send(data, channel, DeliveryMethod.ReliableOrdered);
        unacknowledgedReasons.Add(disconnect);
    }

    public void AcknowledgeReasons()
    {
        foreach (var disconnect in unacknowledgedReasons)
        {
            disconnect.MarkAcknowledged();
        }

        unacknowledgedReasons.Clear();
    }

    public int GetPacketsCountInReliableQueue(byte channelNumber, bool ordered)
    {
        return QueuedReliablePackets.GetValueOrDefault(channelNumber);
    }
}

internal sealed record RecordedDisconnect(int PeerId, byte[]? Data);

// Resolves recording peers by id. A test reuses an id by replacing the entry, which is
// how LiteNetLib hands a freed id to a new connection.
internal sealed class RecordingPeerDirectory : IRealtimePeerDirectory
{
    public Dictionary<int, RecordingPeerConnection> Peers { get; } = [];

    public List<RecordedDisconnect> Disconnected { get; } = [];

    public bool TryGetConnectedPeer(
        int peerId,
        [NotNullWhen(true)] out IRealtimePeerConnection? peer)
    {
        peer = Peers.TryGetValue(peerId, out var connection) ? connection : null;
        return peer is not null;
    }

    public void DisconnectPeer(IRealtimePeerConnection peer, byte[]? data)
    {
        Disconnected.Add(new RecordedDisconnect(peer.Id, data));
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
