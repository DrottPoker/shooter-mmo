using System.Text;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// What a disconnect carries: the encoded LeaveAccepted, ServerDisconnect, or JoinRejected that
// says why the connection ends, as docs/REALTIME_PROTOCOL.md describes under The Reason In The
// Disconnect.
internal static class RealtimeDisconnectData
{
    // LiteNetLib 2.1.4 writes the data into one disconnect packet behind a 9-byte header (the
    // property byte and the connect time) and attaches it only while that packet is strictly
    // smaller than the peer's MTU. The MTU starts at 1024 and stays there, because MTU discovery
    // is off. Above this LiteNetLib sends zero bytes of the same length instead of the data.
    public const int MaximumBytes = 1024 - 9 - 1;

    // The packet itself when it fits, else the same message with its code whole and its log
    // message cut to fit. Throws for a packet that is no such message.
    public static byte[] Fit(byte[] packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (!RealtimeProtocol.TryReadMessageType(packet, out var type)
            || type is not (RealtimeMessageType.LeaveAccepted
                or RealtimeMessageType.ServerDisconnect
                or RealtimeMessageType.JoinRejected))
        {
            throw new ArgumentException(
                "Only a LeaveAccepted, ServerDisconnect, or JoinRejected says why a connection ends.",
                nameof(packet));
        }

        if (packet.Length <= MaximumBytes)
        {
            return packet;
        }

        if (type == RealtimeMessageType.ServerDisconnect
            && RealtimeProtocol.TryDecodeServerDisconnect(packet, out var reason, out _))
        {
            return Cut(reason, RealtimeProtocol.EncodeServerDisconnect);
        }

        if (type == RealtimeMessageType.JoinRejected
            && RealtimeProtocol.TryDecodeJoinRejected(packet, out var rejection, out _))
        {
            return Cut(rejection, RealtimeProtocol.EncodeJoinRejected);
        }

        throw new ArgumentException("The packet does not decode as the message it names.", nameof(packet));
    }

    // Keeps the longest start of the message whose UTF-8 bytes fit beside the code, without
    // splitting a surrogate pair. A start that is blank gives way to the code, which always fits.
    private static byte[] Cut(RealtimeError error, Func<string, string, byte[]> encode)
    {
        var budget = MaximumBytes - (encode(error.Code, "-").Length - 1);
        var message = error.Message;
        var used = 0;
        var end = 0;
        while (end < message.Length)
        {
            var width = char.IsSurrogatePair(message, end) ? 2 : 1;
            var bytes = Encoding.UTF8.GetByteCount(message.AsSpan(end, width));
            if (used + bytes > budget)
            {
                break;
            }

            used += bytes;
            end += width;
        }

        var cut = message[..end];
        return encode(error.Code, string.IsNullOrWhiteSpace(cut) ? error.Code : cut);
    }
}
