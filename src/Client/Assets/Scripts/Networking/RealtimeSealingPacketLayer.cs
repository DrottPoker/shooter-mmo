using System;
using System.Net;
using LiteNetLib.Layers;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // LiteNetLib's hook for every datagram of the client, LiteNetLib's own headers,
    // acknowledgements, and pings included, which RealtimeClientTransportSecurity seals and opens
    // with the keys of the current join. It runs on LiteNetLib's receive and update threads. A
    // layer that throws drops the datagram instead of LiteNetLib's thread.
    internal sealed class RealtimeSealingPacketLayer : PacketLayerBase
    {
        private readonly RealtimeClientTransportSecurity security;

        public RealtimeSealingPacketLayer(RealtimeClientTransportSecurity security)
            : base(RealtimeDatagram.MaximumOverhead)
        {
            this.security = security;
        }

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
}
