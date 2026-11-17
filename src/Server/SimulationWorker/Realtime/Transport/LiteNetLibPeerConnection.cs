using LiteNetLib;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// Stateless adapter over one NetPeer. The adapter is cached in NetPeer.Tag, so resolving
// a peer by id keeps its current semantics and allocates at most once per connection. A NetPeer
// is the peer's endpoint, by which the transport's sealing layer keeps its keys.
internal sealed class LiteNetLibPeerConnection : IRealtimePeerConnection
{
    private readonly RealtimeWorkerTransportSecurity security;

    private LiteNetLibPeerConnection(NetPeer peer, RealtimeWorkerTransportSecurity security)
    {
        Peer = peer;
        this.security = security;
    }

    public NetPeer Peer { get; }

    public int Id => Peer.Id;

    public bool IsSealed => security.IsSealed(Peer);

    public static LiteNetLibPeerConnection For(NetPeer peer, RealtimeWorkerTransportSecurity security)
    {
        return (LiteNetLibPeerConnection)(peer.Tag ??= new LiteNetLibPeerConnection(peer, security));
    }

    public void Seal(RealtimeTransportKeys keys)
    {
        security.Install(Peer, keys);
    }

    public void Send(byte[] data, byte channel, DeliveryMethod deliveryMethod)
    {
        Peer.Send(data, channel, deliveryMethod);
    }

    // LiteNetLib copies the bytes into a pooled packet before it returns.
    public void Send(ReadOnlySpan<byte> data, DeliveryMethod deliveryMethod)
    {
        Peer.Send(data, deliveryMethod);
    }

    // LiteNetLib raises its delivery event with the disconnect as the token once the client
    // acknowledged the packet; the transport's handler marks it.
    public void SendDisconnectReason(byte[] data, byte channel, RealtimePendingDisconnect disconnect)
    {
        Peer.SendWithDeliveryEvent(data, channel, DeliveryMethod.ReliableOrdered, disconnect);
    }

    public int GetPacketsCountInReliableQueue(byte channelNumber, bool ordered)
    {
        return Peer.GetPacketsCountInReliableQueue(channelNumber, ordered);
    }
}
