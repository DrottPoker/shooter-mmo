using System;
using System.Collections.Generic;
using System.Net.Sockets;
using LiteNetLib;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Tests.EditMode
{
    // A recording stand-in for the hub's LiteNetLib transport. Like NetManager with synchronized
    // events, it raises what a test queued only inside PollEvents, in queue order; Stop drops what
    // was not polled, and Start fails while the transport still runs.
    internal sealed class FakeRealtimeTransport : IRealtimeTransport
    {
        private readonly List<Action> pending = new List<Action>();
        private readonly Action<string> record;

        public FakeRealtimeTransport(Action<string> record)
        {
            this.record = record;
        }

        public event Action Connected;

        public event Action<DisconnectReason> Disconnected;

        public event Action<byte[], byte, DeliveryMethod> Received;

        public event Action<SocketError> NetworkError;

        public bool IsStarted { get; private set; }

        public bool StartSucceeds { get; set; } = true;

        public Exception StartException { get; set; }

        public Exception ConnectException { get; set; }

        public int ReportedRoundTripTime { get; set; }

        public int PollCount { get; private set; }

        public int DisconnectTimeoutMilliseconds { get; private set; }

        public string ConnectedEndpoint { get; private set; }

        public List<SentPacket> Sent { get; } = new List<SentPacket>();

        public int RoundTripTimeMilliseconds
        {
            get { return ReportedRoundTripTime; }
        }

        public bool Start(int disconnectTimeoutMilliseconds)
        {
            record("Start");
            DisconnectTimeoutMilliseconds = disconnectTimeoutMilliseconds;
            if (StartException != null)
            {
                throw StartException;
            }

            if (IsStarted || !StartSucceeds)
            {
                return false;
            }

            IsStarted = true;
            return true;
        }

        public void Connect(string host, int port, string connectionKey)
        {
            record("Connect");
            ConnectedEndpoint = host + ":" + port + " " + connectionKey;
            if (ConnectException != null)
            {
                throw ConnectException;
            }
        }

        public void Send(byte[] packet, byte channel, DeliveryMethod deliveryMethod)
        {
            var sent = new SentPacket(packet, channel, deliveryMethod);
            Sent.Add(sent);
            record("Send " + sent);
        }

        public void PollEvents()
        {
            PollCount++;
            var events = pending.ToArray();
            pending.Clear();
            foreach (var raise in events)
            {
                raise();
            }
        }

        public void TriggerUpdate()
        {
            record("TriggerUpdate");
        }

        public void Stop()
        {
            record("Stop");
            IsStarted = false;
            pending.Clear();
        }

        public void QueueConnected()
        {
            pending.Add(() => Connected?.Invoke());
        }

        public void QueueDisconnected(DisconnectReason reason)
        {
            pending.Add(() => Disconnected?.Invoke(reason));
        }

        public void QueueNetworkError(SocketError error)
        {
            pending.Add(() => NetworkError?.Invoke(error));
        }

        public void QueueReceived(byte[] packet, byte channel, DeliveryMethod deliveryMethod)
        {
            pending.Add(() => Received?.Invoke(packet, channel, deliveryMethod));
        }

        public readonly struct SentPacket
        {
            public SentPacket(byte[] packet, byte channel, DeliveryMethod deliveryMethod)
            {
                Packet = packet;
                Channel = channel;
                DeliveryMethod = deliveryMethod;
            }

            public byte[] Packet { get; }

            public byte Channel { get; }

            public DeliveryMethod DeliveryMethod { get; }

            public override string ToString()
            {
                var type = RealtimeProtocol.TryReadMessageType(Packet, out var messageType)
                    ? messageType.ToString()
                    : "invalid";
                return type + " on " + Channel + " " + DeliveryMethod;
            }
        }
    }
}
