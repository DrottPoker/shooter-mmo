using LiteNetLib;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

internal delegate bool RealtimeTestDecoder<T>(byte[] data, out T value, out string error);

internal sealed record RecordedRealtimePacket(
    int Index,
    RealtimeMessageType Type,
    byte Channel,
    DeliveryMethod Delivery,
    byte[] Data,
    DateTime ReceivedAtUtc)
{
    public bool IsReliable => Delivery is DeliveryMethod.ReliableOrdered
        or DeliveryMethod.ReliableUnordered
        or DeliveryMethod.ReliableSequenced;
}

// A LiteNetLib client that records every received realtime packet in arrival order, so
// tests can assert ordered message families instead of reacting inside callbacks.
internal sealed class RealtimeRecordingClient : IDisposable
{
    private readonly object sync = new();
    private readonly EventBasedNetListener listener = new();
    private readonly NetManager client;
    private readonly List<RecordedRealtimePacket> packets = [];
    private readonly string? joinTicket;
    private NetPeer? peer;
    private Exception? failure;

    public RealtimeRecordingClient(string name, string? joinTicket)
    {
        Name = name;
        this.joinTicket = joinTicket;
        client = new NetManager(listener)
        {
            ChannelsCount = RealtimeProtocol.ChannelCount,
            // Keeps the server-side LiteNetLib timeout from racing short handshake timeouts.
            PingInterval = 100
        };
        listener.PeerConnectedEvent += OnPeerConnected;
        listener.PeerDisconnectedEvent += OnPeerDisconnected;
        listener.NetworkReceiveEvent += OnNetworkReceive;
    }

    public string Name { get; }

    public bool IsConnected { get; private set; }

    public bool IsDisconnected { get; private set; }

    public DateTime? DisconnectedAtUtc { get; private set; }

    public DisconnectReason? DisconnectReason { get; private set; }

    public IReadOnlyList<RecordedRealtimePacket> Packets
    {
        get
        {
            lock (sync)
            {
                return packets.ToArray();
            }
        }
    }

    public void Connect(int port)
    {
        Assert.True(client.Start());
        client.Connect("127.0.0.1", port, RealtimeProtocol.ConnectionKey);
    }

    public void PollEvents()
    {
        client.PollEvents();
        if (failure is not null)
        {
            throw new InvalidOperationException(
                $"Realtime recording client {Name} failed.",
                failure);
        }
    }

    public void Send(
        byte[] packet,
        DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered,
        byte channel = RealtimeProtocol.ControlChannel)
    {
        var connectedPeer = peer
            ?? throw new InvalidOperationException($"Client {Name} is not connected.");
        connectedPeer.Send(packet, channel, deliveryMethod);
    }

    public void SendJoin(string ticket)
    {
        Send(RealtimeProtocol.EncodeJoinRequest(ticket));
    }

    public void SendLeave(string simulationSessionId)
    {
        Send(RealtimeProtocol.EncodeLeaveRequest(simulationSessionId));
    }

    // Every call names path 1, so the worker keeps the first destination it received.
    public void SendMoveTo(uint inputSequence, float x, float z)
    {
        Send(
            RealtimeProtocol.EncodeMovementInputBatch(new RealtimeMovementInputBatch(
                [new RealtimeMovementInput(inputSequence, inputSequence, 1)],
                new RealtimeMovementPath(1, [new RealtimeMovementPathWaypoint(x, 0f, z)]))),
            DeliveryMethod.Sequenced,
            RealtimeProtocol.MovementInputChannel);
    }

    public void SendItemIntent(RealtimeItemOperationIntent intent)
    {
        Send(RealtimeProtocol.EncodeItemOperationIntent(intent));
    }

    public void SendCorpseIntent(RealtimeCorpseInteractionIntent intent)
    {
        Send(RealtimeProtocol.EncodeCorpseInteractionIntent(intent));
    }

    public void SendWorldIntent(RealtimeWorldInteractionIntent intent)
    {
        Send(RealtimeProtocol.EncodeWorldInteractionIntent(intent));
    }

    public bool Has(RealtimeMessageType type)
    {
        lock (sync)
        {
            return packets.Any(packet => packet.Type == type);
        }
    }

    public int Count(RealtimeMessageType type)
    {
        lock (sync)
        {
            return packets.Count(packet => packet.Type == type);
        }
    }

    public int IndexOfFirst(RealtimeMessageType type)
    {
        lock (sync)
        {
            return packets.FindIndex(packet => packet.Type == type);
        }
    }

    public IReadOnlyList<T> Decode<T>(
        RealtimeMessageType type,
        RealtimeTestDecoder<T> decoder)
    {
        return Packets
            .Where(packet => packet.Type == type)
            .Select(packet =>
            {
                Assert.True(decoder(packet.Data, out var value, out var error), error);
                return value;
            })
            .ToArray();
    }

    public IReadOnlyList<(RecordedRealtimePacket Packet, T Value)> DecodeWithPackets<T>(
        RealtimeMessageType type,
        RealtimeTestDecoder<T> decoder)
    {
        return Packets
            .Where(packet => packet.Type == type)
            .Select(packet =>
            {
                Assert.True(decoder(packet.Data, out var value, out var error), error);
                return (packet, value);
            })
            .ToArray();
    }

    public RealtimeJoinAccepted? JoinAccepted =>
        Decode<RealtimeJoinAccepted>(
                RealtimeMessageType.JoinAccepted,
                RealtimeProtocol.TryDecodeJoinAccepted)
            .FirstOrDefault();

    public RealtimeError? ServerDisconnect =>
        Decode<RealtimeError>(
                RealtimeMessageType.ServerDisconnect,
                RealtimeProtocol.TryDecodeServerDisconnect)
            .LastOrDefault();

    public RealtimeError? JoinRejected =>
        Decode<RealtimeError>(
                RealtimeMessageType.JoinRejected,
                RealtimeProtocol.TryDecodeJoinRejected)
            .LastOrDefault();

    public IReadOnlyList<RealtimeError> LeaveRejections =>
        Decode<RealtimeError>(
            RealtimeMessageType.LeaveRejected,
            RealtimeProtocol.TryDecodeLeaveRejected);

    public IReadOnlyList<RealtimeEntitySpawn> Spawns =>
        Decode<RealtimeEntitySpawn>(
            RealtimeMessageType.EntitySpawn,
            RealtimeProtocol.TryDecodeEntitySpawn);

    public IReadOnlyList<RealtimeEntityDespawn> Despawns =>
        Decode<RealtimeEntityDespawn>(
            RealtimeMessageType.EntityDespawn,
            RealtimeProtocol.TryDecodeEntityDespawn);

    public IReadOnlyList<RealtimeItemOperationResult> ItemResults =>
        Decode<RealtimeItemOperationResult>(
            RealtimeMessageType.ItemOperationResult,
            RealtimeProtocol.TryDecodeItemOperationResult);

    public IReadOnlyList<RealtimeCorpseInteractionResult> CorpseResults =>
        Decode<RealtimeCorpseInteractionResult>(
            RealtimeMessageType.CorpseInteractionResult,
            RealtimeProtocol.TryDecodeCorpseInteractionResult);

    public IReadOnlyList<RealtimeCorpseViewStateChunk> CorpseViewChunks =>
        Decode<RealtimeCorpseViewStateChunk>(
            RealtimeMessageType.CorpseViewStateChunk,
            RealtimeProtocol.TryDecodeCorpseViewStateChunk);

    public IReadOnlyList<RealtimeCorpseViewClosed> CorpseViewClosures =>
        Decode<RealtimeCorpseViewClosed>(
            RealtimeMessageType.CorpseViewClosed,
            RealtimeProtocol.TryDecodeCorpseViewClosed);

    public IReadOnlyList<RealtimeCorpsePresenceSnapshotChunk> CorpsePresence =>
        Decode<RealtimeCorpsePresenceSnapshotChunk>(
            RealtimeMessageType.CorpsePresenceSnapshotChunk,
            RealtimeProtocol.TryDecodeCorpsePresenceSnapshotChunk);

    public IReadOnlyList<RealtimeWorldActorSpawn> WorldActorSpawns =>
        Decode<RealtimeWorldActorSpawn>(
            RealtimeMessageType.WorldActorSpawn,
            RealtimeProtocol.TryDecodeWorldActorSpawn);

    public IReadOnlyList<RealtimeWorldActorDespawn> WorldActorDespawns =>
        Decode<RealtimeWorldActorDespawn>(
            RealtimeMessageType.WorldActorDespawn,
            RealtimeProtocol.TryDecodeWorldActorDespawn);

    public IReadOnlyList<RealtimeWorldInteractionOpened> WorldOpened =>
        Decode<RealtimeWorldInteractionOpened>(
            RealtimeMessageType.WorldInteractionOpened,
            RealtimeProtocol.TryDecodeWorldInteractionOpened);

    public IReadOnlyList<RealtimeWorldInteractionResult> WorldResults =>
        Decode<RealtimeWorldInteractionResult>(
            RealtimeMessageType.WorldInteractionResult,
            RealtimeProtocol.TryDecodeWorldInteractionResult);

    public IReadOnlyList<RealtimeWorldInteractionClosed> WorldClosures =>
        Decode<RealtimeWorldInteractionClosed>(
            RealtimeMessageType.WorldInteractionClosed,
            RealtimeProtocol.TryDecodeWorldInteractionClosed);

    public IReadOnlyList<RealtimeSimulationSnapshot> Snapshots =>
        Decode<RealtimeSimulationSnapshot>(
            RealtimeMessageType.SimulationSnapshot,
            RealtimeProtocol.TryDecodeSimulationSnapshot);

    public IReadOnlyList<RealtimeOwnerSimulationSnapshot> OwnerSnapshots =>
        Decode<RealtimeOwnerSimulationSnapshot>(
            RealtimeMessageType.OwnerSimulationSnapshot,
            RealtimeProtocol.TryDecodeOwnerSimulationSnapshot);

    public IReadOnlyList<RecordedRealtimePacket> ReliablePackets =>
        Packets.Where(packet => packet.IsReliable).ToArray();

    public string Describe()
    {
        var types = string.Join(
            ", ",
            Packets.Select(packet => packet.Type.ToString()));
        return $"{Name}: connected={IsConnected}, disconnected={IsDisconnected}, packets=[{types}]";
    }

    public void Stop()
    {
        client.Stop();
    }

    public void Dispose()
    {
        client.Stop();
    }

    private void OnPeerConnected(NetPeer connectedPeer)
    {
        peer = connectedPeer;
        IsConnected = true;
        if (joinTicket is not null)
        {
            SendJoin(joinTicket);
        }
    }

    private void OnPeerDisconnected(NetPeer disconnectedPeer, DisconnectInfo disconnectInfo)
    {
        IsConnected = false;
        IsDisconnected = true;
        DisconnectedAtUtc = DateTime.UtcNow;
        DisconnectReason = disconnectInfo.Reason;
    }

    private void OnNetworkReceive(
        NetPeer connectedPeer,
        NetPacketReader reader,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        try
        {
            var data = reader.GetRemainingBytes();
            if (!RealtimeProtocol.TryReadMessageType(data, out var messageType))
            {
                throw new InvalidOperationException("The server sent an invalid realtime header.");
            }

            lock (sync)
            {
                packets.Add(new RecordedRealtimePacket(
                    packets.Count,
                    messageType,
                    channel,
                    deliveryMethod,
                    data,
                    DateTime.UtcNow));
            }
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        finally
        {
            reader.Recycle();
        }
    }
}
