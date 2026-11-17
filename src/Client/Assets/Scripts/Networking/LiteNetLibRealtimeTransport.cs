using System;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // The LiteNetLib transport of RealtimeSimulationClient: one NetManager with the protocol's
    // channels and LiteNetLib's synchronized events, which fire only inside PollEvents. Every
    // datagram passes the sealing layer with the keys of the current join, as Transport Security
    // in docs/REALTIME_PROTOCOL.md describes.
    internal sealed class LiteNetLibRealtimeTransport : IRealtimeTransport
    {
        private readonly NetManager manager;
        private readonly RealtimeClientTransportSecurity security = new RealtimeClientTransportSecurity();
        private NetPeer serverPeer;

        public LiteNetLibRealtimeTransport()
        {
            var listener = new EventBasedNetListener();
            manager = new NetManager(listener, new RealtimeSealingPacketLayer(security));
            listener.PeerConnectedEvent += OnPeerConnected;
            listener.PeerDisconnectedEvent += OnPeerDisconnected;
            listener.NetworkReceiveEvent += OnNetworkReceive;
            listener.NetworkErrorEvent += OnNetworkError;
            manager.ChannelsCount = RealtimeProtocol.ChannelCount;

            // The layer's bytes come on top of LiteNetLib's packets, which keep their limits.
            manager.MtuOverride = RealtimeDatagram.LiteNetLibMtu;
        }

        // The datagrams of the connection and the ones the sealing layer dropped, and whether the
        // worker's sealed datagrams arrived.
        internal RealtimeClientTransportSecurity Security
        {
            get { return security; }
        }

        public event Action Connected;

        public event Action<DisconnectReason, byte[]> Disconnected;

        public event Action<byte[], byte, DeliveryMethod> Received;

        public event Action<SocketError> NetworkError;

        // LiteNetLib's Ping is half the round trip, so the round trip is read directly.
        public int RoundTripTimeMilliseconds
        {
            get { return serverPeer == null ? 0 : serverPeer.RoundTripTime; }
        }

        public bool Start(int disconnectTimeoutMilliseconds)
        {
            manager.DisconnectTimeout = disconnectTimeoutMilliseconds;
            return manager.Start();
        }

        public void Connect(string host, int port, string connectionKey, RealtimeTransportKeys keys)
        {
            security.Install(keys);
            manager.Connect(host, port, connectionKey);
        }

        public void Send(byte[] packet, byte channel, DeliveryMethod deliveryMethod)
        {
            serverPeer.Send(packet, channel, deliveryMethod);
        }

        public void PollEvents()
        {
            manager.PollEvents();
        }

        public void TriggerUpdate()
        {
            manager.TriggerUpdate();
        }

        // The keys of the connection are zeroed once the socket closed.
        public void Stop()
        {
            manager.Stop();
            serverPeer = null;
            security.Reset();
        }

        // Closes the socket without the disconnect message, so the worker sees a lost line and
        // times the connection out. Only the live stack test uses it.
        internal void StopWithoutDisconnectMessage()
        {
            manager.Stop(false);
            serverPeer = null;
            security.Reset();
        }

        private void OnPeerConnected(NetPeer peer)
        {
            serverPeer = peer;
            Connected?.Invoke();
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            serverPeer = null;
            Disconnected?.Invoke(disconnectInfo.Reason, ReadDisconnectData(disconnectInfo.AdditionalData));
        }

        // LiteNetLib returns the event of an empty reader, as of a local disconnect, to its pool by
        // itself; recycling that reader as well would hand the same event out twice. Only a reader
        // that holds the server's disconnect packet goes back here.
        private static byte[] ReadDisconnectData(NetPacketReader reader)
        {
            if (reader == null || reader.IsNull)
            {
                return Array.Empty<byte>();
            }

            try
            {
                return reader.GetRemainingBytes();
            }
            finally
            {
                reader.Recycle();
            }
        }

        // The reader returns to LiteNetLib's packet pool once the packet was handled.
        private void OnNetworkReceive(
            NetPeer peer,
            NetPacketReader reader,
            byte channel,
            DeliveryMethod deliveryMethod)
        {
            try
            {
                var packet = reader.GetRemainingBytes();
                Received?.Invoke(packet, channel, deliveryMethod);
            }
            finally
            {
                reader.Recycle();
            }
        }

        private void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            NetworkError?.Invoke(socketError);
        }
    }
}
