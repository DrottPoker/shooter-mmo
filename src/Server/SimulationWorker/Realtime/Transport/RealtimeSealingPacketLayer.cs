using System.Net;
using LiteNetLib.Layers;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// LiteNetLib's hook for every datagram the worker sends and receives, its own headers,
// acknowledgements, and pings included, which RealtimeWorkerTransportSecurity seals and opens by
// the remote endpoint. LiteNetLib reserves the most bytes the layer adds. It runs on LiteNetLib's
// receive thread and on every thread that sends, so it holds no state of its own. A layer that
// throws drops the datagram instead of LiteNetLib's thread.
internal sealed class RealtimeSealingPacketLayer(RealtimeWorkerTransportSecurity security)
    : PacketLayerBase(RealtimeDatagram.MaximumOverhead)
{
    public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
    {
        try
        {
            security.ProcessInbound(endPoint, data, ref length);
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
            security.ProcessOutbound(endPoint, data, ref offset, ref length);
        }
        catch (Exception)
        {
            length = 0;
        }
    }
}
