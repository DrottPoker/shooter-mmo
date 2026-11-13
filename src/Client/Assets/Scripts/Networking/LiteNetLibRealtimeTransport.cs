using System;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // The LiteNetLib transport of RealtimeSimulationClient: one NetManager with the protocol's
    // channels and LiteNetLib's synchronized events, which fire only inside PollEvents.
    internal sealed class LiteNetLibRealtimeTransport : IRealtimeTransport
    {
        private readonly NetManager manager;
        private NetPeer serverPeer;

        public LiteNetLibRealtimeTransport()
        {
            var listener = new EventBasedNetListener();
            manager = new NetManager(listener);
            listener.PeerConnectedEvent += OnPeerConnected;
            listener.PeerDisconnectedEvent += OnPeerDisconnected;
            listener.NetworkReceiveEvent += OnNetworkReceive;
            listener.NetworkErrorEvent += OnNetworkError;
            manager.ChannelsCount = RealtimeProtocol.ChannelCount;
        }

        public event Action Connected;

        public event Action<DisconnectReason> Disconnected;

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

        public void Connect(string host, int port, string connectionKey)
        {
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

        public void Stop()
        {
            manager.Stop();
            serverPeer = null;
        }

        // Closes the socket without the disconnect message, so the worker sees a lost line and
        // times the connection out. Only the live stack test uses it.
        internal void StopWithoutDisconnectMessage()
        {
            manager.Stop(false);
            serverPeer = null;
        }

        private void OnPeerConnected(NetPeer peer)
        {
            serverPeer = peer;
            Connected?.Invoke();
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            serverPeer = null;
            Disconnected?.Invoke(disconnectInfo.Reason);
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
