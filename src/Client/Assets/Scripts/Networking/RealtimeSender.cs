using System;
using LiteNetLib;
using Mmorpg.Client.Core;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // The one send path of the channels. A send needs a joined session with a connected server;
    // an intent goes reliable ordered on the control channel, and one that its encoder refuses
    // is logged and not sent. Movement input has its own channel and wakes the transport.
    internal sealed class RealtimeSender
    {
        private readonly RealtimeConnection connection;
        private readonly RealtimeSession session;
        private readonly RealtimeDiagnostics diagnostics;

        public RealtimeSender(
            RealtimeConnection connection,
            RealtimeSession session,
            RealtimeDiagnostics diagnostics)
        {
            this.connection = connection;
            this.session = session;
            this.diagnostics = diagnostics;
        }

        // Leaving counts as not joined.
        public bool CanSend
        {
            get { return session.State == RealtimeConnectionState.Joined && connection.IsServerConnected; }
        }

        // The encoder is a cached delegate of its channel, so a send allocates only its packet.
        // The kind and the operation id time the intent to its result while a measurement asks
        // for it.
        public bool TrySendIntent<TIntent>(
            TIntent intent,
            Func<TIntent, byte[]> encode,
            string description,
            string kind,
            Guid operationId)
            where TIntent : class
        {
            if (!CanSend || intent == null)
            {
                return false;
            }

            try
            {
                var packet = encode(intent);
                connection.Send(packet, RealtimeProtocol.ControlChannel, DeliveryMethod.ReliableOrdered);
                diagnostics.RecordSentPacket(packet.Length);
                diagnostics.RecordIntentSent(operationId, kind);
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException || exception is InvalidOperationException)
            {
                ClientLog.Error(
                    ClientLogCategory.Client,
                    description + " could not be encoded: " + exception.Message);
                return false;
            }
        }

        // A channel received the result of the intent with this operation id.
        public void RecordIntentAnswered(Guid operationId)
        {
            diagnostics.RecordIntentAnswered(operationId);
        }

        // Sends an encoded movement input batch sequenced on its channel. The transport otherwise
        // sends on its own 15 ms update, which made inputs reach the worker unevenly.
        public void SendMovementInput(byte[] packet)
        {
            connection.Send(packet, RealtimeProtocol.MovementInputChannel, DeliveryMethod.Sequenced);
            connection.Flush();
            diagnostics.RecordSentPacket(packet.Length);
        }
    }
}
