using LiteNetLib;

namespace Mmorpg.SimulationWorker.Realtime;

// One connected UDP peer as the realtime loop sees it. Only the loop may use it.
internal interface IRealtimePeerConnection
{
    int Id { get; }

    // Sends on channel 0, exactly like NetPeer.Send(byte[], DeliveryMethod).
    void Send(byte[] data, DeliveryMethod deliveryMethod);

    // Sends a copy of the bytes on channel 0, so the caller may reuse its buffer at once.
    void Send(ReadOnlySpan<byte> data, DeliveryMethod deliveryMethod);

    // Sends the message that says why the connection ends, reliable ordered on channel 0, and
    // marks the pending disconnect acknowledged once the client acknowledged the packet.
    void SendDisconnectReason(byte[] data, RealtimePendingDisconnect disconnect);

    // LiteNetLib's outgoing queue of the channel: the reliable packets that wait behind the 64
    // unacknowledged ones of its send window. An empty queue means only that every packet was
    // sent at least once, never that one was acknowledged.
    int GetPacketsCountInReliableQueue(byte channelNumber, bool ordered);
}
