using LiteNetLib;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// Maps the delivery that the protocol schema declares for a message to LiteNetLib.
internal static class RealtimeDeliveryMethods
{
    public static DeliveryMethod ToDeliveryMethod(RealtimeDelivery delivery)
    {
        return delivery switch
        {
            RealtimeDelivery.ReliableOrdered => DeliveryMethod.ReliableOrdered,
            RealtimeDelivery.Sequenced => DeliveryMethod.Sequenced,
            RealtimeDelivery.Unreliable => DeliveryMethod.Unreliable,
            _ => throw new ArgumentOutOfRangeException(nameof(delivery), delivery, "The delivery is unknown.")
        };
    }
}
