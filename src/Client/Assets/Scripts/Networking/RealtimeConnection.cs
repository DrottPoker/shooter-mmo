using LiteNetLib;

namespace Mmorpg.Client.Networking
{
    // The hub's transport and the one connection it runs: whether the socket runs, whether the
    // server connected, and the endpoint. The session starts and stops it; the session, the
    // send path, and the diagnostics read it.
    internal sealed class RealtimeConnection
    {
        private readonly IRealtimeTransport transport;

        public RealtimeConnection(IRealtimeTransport transport)
        {
            this.transport = transport;
        }

        public bool IsStarted { get; private set; }

        // Whether the transport reported the connection while the session was connecting.
        public bool IsServerConnected { get; private set; }

        public string Host { get; private set; } = string.Empty;

        public int Port { get; private set; }

        // The transport's round trip time, or 0 without a connected server.
        public int RoundTripTimeMilliseconds
        {
            get { return IsServerConnected ? transport.RoundTripTimeMilliseconds : 0; }
        }

        public void SetEndpoint(string host, int port)
        {
            Host = host;
            Port = port;
        }

        // Opens the socket; false when the transport refuses, as while it still runs.
        public bool Start(int disconnectTimeoutMilliseconds)
        {
            if (!transport.Start(disconnectTimeoutMilliseconds))
            {
                return false;
            }

            IsStarted = true;
            return true;
        }

        public void Connect(string connectionKey)
        {
            transport.Connect(Host, Port, connectionKey);
        }

        public void MarkServerConnected()
        {
            IsServerConnected = true;
        }

        public void MarkServerDisconnected()
        {
            IsServerConnected = false;
        }

        // Raises the transport's events of this frame.
        public void Poll()
        {
            if (IsStarted)
            {
                transport.PollEvents();
            }
        }

        public void Send(byte[] packet, byte channel, DeliveryMethod deliveryMethod)
        {
            transport.Send(packet, channel, deliveryMethod);
        }

        // Wakes the transport's update thread, so what was queued leaves at once.
        public void Flush()
        {
            transport.TriggerUpdate();
        }

        // Closes the socket when it runs and forgets the connection and its endpoint.
        public void Stop()
        {
            if (IsStarted)
            {
                transport.Stop();
                IsStarted = false;
            }

            IsServerConnected = false;
            Host = string.Empty;
            Port = 0;
        }
    }
}
