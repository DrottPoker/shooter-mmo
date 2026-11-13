using System;
using System.Collections.Generic;
using System.Net.Sockets;
using LiteNetLib;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Tests.PlayMode
{
    // The hub's real LiteNetLib transport with two additions for the live stack test: it keeps
    // the BodySeconds of every LeaveAccepted it delivers, which the hub does not expose, and it
    // can drop the line without a disconnect message, as a lost connection looks to the worker.
    internal sealed class LiveStackTransport : IRealtimeTransport
    {
        private readonly LiteNetLibRealtimeTransport inner = new LiteNetLibRealtimeTransport();

        public LiveStackTransport()
        {
            inner.Received += OnReceived;
        }

        public event Action Connected
        {
            add { inner.Connected += value; }
            remove { inner.Connected -= value; }
        }

        public event Action<DisconnectReason> Disconnected
        {
            add { inner.Disconnected += value; }
            remove { inner.Disconnected -= value; }
        }

        public event Action<byte[], byte, DeliveryMethod> Received;

        public event Action<SocketError> NetworkError
        {
            add { inner.NetworkError += value; }
            remove { inner.NetworkError -= value; }
        }

        public List<ushort> LeaveBodySeconds { get; } = new List<ushort>();

        public int RoundTripTimeMilliseconds
        {
            get { return inner.RoundTripTimeMilliseconds; }
        }

        public bool Start(int disconnectTimeoutMilliseconds)
        {
            return inner.Start(disconnectTimeoutMilliseconds);
        }

        public void Connect(string host, int port, string connectionKey)
        {
            inner.Connect(host, port, connectionKey);
        }

        public void Send(byte[] packet, byte channel, DeliveryMethod deliveryMethod)
        {
            inner.Send(packet, channel, deliveryMethod);
        }

        public void PollEvents()
        {
            inner.PollEvents();
        }

        public void TriggerUpdate()
        {
            inner.TriggerUpdate();
        }

        public void Stop()
        {
            inner.Stop();
        }

        // The worker keeps the connection until its disconnect timeout, as after a lost line.
        public void DropLine()
        {
            inner.StopWithoutDisconnectMessage();
        }

        private void OnReceived(byte[] packet, byte channel, DeliveryMethod deliveryMethod)
        {
            if (RealtimeProtocol.TryReadMessageType(packet, out var type)
                && type == RealtimeMessageType.LeaveAccepted
                && RealtimeProtocol.TryDecodeLeaveAccepted(packet, out var bodySeconds, out _))
            {
                LeaveBodySeconds.Add(bodySeconds);
            }

            Received?.Invoke(packet, channel, deliveryMethod);
        }
    }
}
