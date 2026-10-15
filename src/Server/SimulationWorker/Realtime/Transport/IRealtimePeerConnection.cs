using LiteNetLib;

namespace Mmorpg.SimulationWorker.Realtime;

// One connected UDP peer as the realtime loop sees it. Only the loop may use it.
internal interface IRealtimePeerConnection
{
    int Id { get; }

    // Sends on channel 0, exactly like NetPeer.Send(byte[], DeliveryMethod).
    void Send(byte[] data, DeliveryMethod deliveryMethod);

    int GetPacketsCountInReliableQueue(byte channelNumber, bool ordered);
}
