using System;
using System.Net.Sockets;
using LiteNetLib;

namespace Mmorpg.Client.Networking
{
    // What RealtimeSimulationClient uses of its UDP transport: one connection to one
    // SimulationWorker. The events fire only inside PollEvents, on the thread that polls, so the
    // hub handles them within its Update. EditMode tests replace it with a recording fake.
    internal interface IRealtimeTransport
    {
        event Action Connected;

        // The connection closed, with LiteNetLib's reason.
        event Action<DisconnectReason> Disconnected;

        // A packet from the server with the channel and delivery it arrived on.
        event Action<byte[], byte, DeliveryMethod> Received;

        event Action<SocketError> NetworkError;

        // The round trip time to the connected server in milliseconds.
        int RoundTripTimeMilliseconds { get; }

        // Opens the socket; false when it cannot, as while it still runs.
        bool Start(int disconnectTimeoutMilliseconds);

        void Connect(string host, int port, string connectionKey);

        // Sends to the connected server.
        void Send(byte[] packet, byte channel, DeliveryMethod deliveryMethod);

        void PollEvents();

        // Wakes the transport's update thread, so queued packets leave at once.
        void TriggerUpdate();

        // Closes the connection and the socket and drops the events not polled yet.
        void Stop();
    }
}
