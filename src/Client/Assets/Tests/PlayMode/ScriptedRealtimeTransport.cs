using System;
using System.Collections.Generic;
using System.Net.Sockets;
using LiteNetLib;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Tests.PlayMode
{
    // A stand-in for the hub's LiteNetLib transport that plays a worker which connects at once and
    // answers a join request with the acceptance a test sets. Like NetManager with synchronized
    // events it raises everything inside PollEvents, and it counts the movement input batches the
    // client sends.
    internal sealed class ScriptedRealtimeTransport : IRealtimeTransport
    {
        private readonly List<Action> pending = new List<Action>();

        public event Action Connected;

        public event Action<DisconnectReason> Disconnected;

        public event Action<byte[], byte, DeliveryMethod> Received;

        // The scripted worker has no socket that could fail.
        public event Action<SocketError> NetworkError
        {
            add { }
            remove { }
        }

        // The answer to the next join request, used once.
        public RealtimeJoinAccepted NextJoinAccepted { get; set; }

        public int MovementInputBatches { get; private set; }

        public bool IsStarted { get; private set; }

        public int RoundTripTimeMilliseconds
        {
            get { return 0; }
        }

        public bool Start(int disconnectTimeoutMilliseconds)
        {
            if (IsStarted)
            {
                return false;
            }

            IsStarted = true;
            return true;
        }

        public void Connect(string host, int port, string connectionKey)
        {
            pending.Add(() => Connected?.Invoke());
        }

        public void Send(byte[] packet, byte channel, DeliveryMethod deliveryMethod)
        {
            if (!RealtimeProtocol.TryReadMessageType(packet, out var type))
            {
                return;
            }

            if (type == RealtimeMessageType.MovementInputBatch)
            {
                MovementInputBatches++;
            }

            if (type == RealtimeMessageType.JoinRequest && NextJoinAccepted != null)
            {
                var answer = RealtimeProtocol.EncodeJoinAccepted(NextJoinAccepted);
                NextJoinAccepted = null;
                pending.Add(() => Received?.Invoke(answer, RealtimeProtocol.ControlChannel, DeliveryMethod.ReliableOrdered));
            }
        }

        public void PollEvents()
        {
            var events = pending.ToArray();
            pending.Clear();
            foreach (var raise in events)
            {
                raise();
            }
        }

        public void TriggerUpdate()
        {
        }

        public void Stop()
        {
            IsStarted = false;
            pending.Clear();
        }

        // The line drops: the next poll reports the timeout, as LiteNetLib does.
        public void LoseLine()
        {
            pending.Add(() => Disconnected?.Invoke(DisconnectReason.Timeout));
        }
    }
}
