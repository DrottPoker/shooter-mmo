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

    int GetPacketsCountInReliableQueue(byte channelNumber, bool ordered);
}
