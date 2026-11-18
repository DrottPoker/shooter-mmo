using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// The reliable ordered channels of the protocol, as its generated descriptors name them, so the
// worker sends every reliable message on its own channel and counts every reliable channel.
internal static class RealtimeReliableChannels
{
    private static readonly byte[] Channels = Enum.GetValues<RealtimeMessageType>()
        .Select(type => RealtimeProtocol.TryGetDescriptor(type, out var descriptor) ? descriptor : null)
        .Where(descriptor => descriptor is not null && descriptor.Delivery == RealtimeDelivery.ReliableOrdered)
        .Select(descriptor => descriptor!.Channel)
        .Distinct()
        .Order()
        .ToArray();

    // Every channel that carries a reliable ordered message in either direction.
    public static IReadOnlyList<byte> All => Channels;

    // The channel that the descriptor of the packet's reliable message type names. Throws for a
    // packet that is no reliable message, which only a worker defect sends.
    public static byte Of(byte[] packet)
    {
        if (!RealtimeProtocol.TryReadMessageType(packet, out var type)
            || !RealtimeProtocol.TryGetDescriptor(type, out var descriptor)
            || descriptor.Delivery != RealtimeDelivery.ReliableOrdered)
        {
            throw new ArgumentException("The packet is no reliable realtime message.", nameof(packet));
        }

        return descriptor.Channel;
    }

    // LiteNetLib's outgoing queues of every reliable channel together: the packets that wait
    // behind the send window of their channel. Never an acknowledgement. It allocates nothing,
    // since the supervisor asks it for every peer at every loop iteration and the interest refresh
    // for every connection.
    public static int QueuedPackets(IRealtimePeerConnection peer)
    {
        var queued = 0;
        for (var index = 0; index < Channels.Length; index++)
        {
            queued += peer.GetPacketsCountInReliableQueue(Channels[index], true);
        }

        return queued;
    }
}
