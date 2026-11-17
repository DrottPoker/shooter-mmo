using System.Net;
using LiteNetLib.Layers;
using Mmorpg.GameProtocol;

namespace Mmorpg.Tools.SimulationBotClient;

// The client side of the sealed transport as a LiteNetLib packet layer, as the Unity client has
// it: every datagram of the bot passes RealtimeClientTransportSecurity. A layer that throws drops
// the datagram instead of LiteNetLib's thread.
public sealed class SimulationBotSealingLayer(RealtimeClientTransportSecurity security)
    : PacketLayerBase(RealtimeDatagram.MaximumOverhead)
{
    public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
    {
        try
        {
            security.ProcessInbound(data, ref length);
        }
        catch (Exception)
        {
            security.Counters.RecordDropped(RealtimeDatagramDrop.Malformed);
            length = 0;
        }
    }

    public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
    {
        try
        {
            security.ProcessOutbound(data, ref offset, ref length);
        }
        catch (Exception)
        {
            length = 0;
        }
    }
}
