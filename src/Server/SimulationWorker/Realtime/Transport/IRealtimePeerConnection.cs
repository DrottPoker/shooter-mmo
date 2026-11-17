using LiteNetLib;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// One connected UDP peer as the realtime loop sees it. Only the loop may use it.
internal interface IRealtimePeerConnection
{
    int Id { get; }

    // Whether the peer's datagrams are sealed, which they are from the moment its join's keys
    // were installed until it disconnects.
    bool IsSealed { get; }

    // Installs the keys of the peer's join: every datagram to it is sealed from now on, the one
    // that accepts the join included, and every clear one from it is dropped. The caller still
    // owns the keys and zeroes them.
    void Seal(RealtimeTransportKeys keys);

    // Sends on the channel, exactly like NetPeer.Send(byte[], byte, DeliveryMethod).
    void Send(byte[] data, byte channel, DeliveryMethod deliveryMethod);

    // Sends a copy of the bytes unreliably on channel 0, so the caller may reuse its buffer at
    // once.
    void Send(ReadOnlySpan<byte> data, DeliveryMethod deliveryMethod);

    // Sends the message that says why the connection ends, reliable ordered on its channel, and
    // marks the pending disconnect acknowledged once the client acknowledged the packet.
    void SendDisconnectReason(byte[] data, byte channel, RealtimePendingDisconnect disconnect);

    // LiteNetLib's outgoing queue of one reliable channel: the packets that wait behind the 64
    // unacknowledged ones of its send window. An empty queue means only that every packet was
    // sent at least once, never that one was acknowledged.
    int GetPacketsCountInReliableQueue(byte channelNumber, bool ordered);
}
