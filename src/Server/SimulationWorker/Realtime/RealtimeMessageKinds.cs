using System.Globalization;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// The messages and bytes of one realtime message kind, by the type byte of its header.
public readonly record struct RealtimeMessageKindTraffic(byte MessageType, long Messages, long Bytes)
{
    public string Name => RealtimeMessageKinds.NameOf(MessageType);
}

// The message kinds that the realtime network metrics count by: the type byte of a packet's
// header, which RealtimeMessageType names.
internal static class RealtimeMessageKinds
{
    public const int Count = 256;

    // No message type has byte 0, so it holds every packet without a readable type.
    public const byte Unreadable = 0;

    private static readonly string[] Names = CreateNames();

    // The packet's type byte, read as the receive path reads it, or Unreadable for an empty
    // packet and for one whose header is invalid or names no defined type.
    public static byte Of(ReadOnlySpan<byte> packet)
    {
        return RealtimeProtocol.TryReadMessageType(packet, out var type) ? (byte)type : Unreadable;
    }

    // The RealtimeMessageType name, or the number as text for a byte that is no defined type.
    public static string NameOf(byte messageType)
    {
        return Names[messageType];
    }

    private static string[] CreateNames()
    {
        var names = new string[Count];
        for (var value = 0; value < Count; value++)
        {
            var type = (RealtimeMessageType)value;
            names[value] = Enum.IsDefined(type)
                ? type.ToString()
                : value.ToString(CultureInfo.InvariantCulture);
        }

        return names;
    }
}
