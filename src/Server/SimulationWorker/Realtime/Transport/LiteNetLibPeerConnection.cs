using LiteNetLib;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// Stateless adapter over one NetPeer. The adapter is cached in NetPeer.Tag, so resolving
// a peer by id keeps its current semantics and allocates at most once per connection.
internal sealed class LiteNetLibPeerConnection : IRealtimePeerConnection
{
    private LiteNetLibPeerConnection(NetPeer peer)
    {
        Peer = peer;
    }

    public NetPeer Peer { get; }

    public int Id => Peer.Id;

    public static LiteNetLibPeerConnection For(NetPeer peer)
    {
        return (LiteNetLibPeerConnection)(peer.Tag ??= new LiteNetLibPeerConnection(peer));
    }

    public void Send(byte[] data, DeliveryMethod deliveryMethod)
    {
        Peer.Send(data, deliveryMethod);
    }

    // LiteNetLib copies the bytes into a pooled packet before it returns.
    public void Send(ReadOnlySpan<byte> data, DeliveryMethod deliveryMethod)
    {
        Peer.Send(data, deliveryMethod);
    }

    // LiteNetLib raises its delivery event with the disconnect as the token once the client
    // acknowledged the packet; the transport's handler marks it.
    public void SendDisconnectReason(byte[] data, RealtimePendingDisconnect disconnect)
    {
        Peer.SendWithDeliveryEvent(data, RealtimeProtocol.ControlChannel, DeliveryMethod.ReliableOrdered, disconnect);
    }

    public int GetPacketsCountInReliableQueue(byte channelNumber, bool ordered)
    {
        return Peer.GetPacketsCountInReliableQueue(channelNumber, ordered);
    }
}
