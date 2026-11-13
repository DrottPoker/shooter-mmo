using System;
using System.Collections.Generic;
using LiteNetLib;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // A set of connection states.
    [Flags]
    internal enum RealtimeStates
    {
        None = 0,
        Disconnected = 1 << (int)RealtimeConnectionState.Disconnected,
        Connecting = 1 << (int)RealtimeConnectionState.Connecting,
        Joining = 1 << (int)RealtimeConnectionState.Joining,
        Joined = 1 << (int)RealtimeConnectionState.Joined,
        Leaving = 1 << (int)RealtimeConnectionState.Leaving,

        // The states in which the hub reads messages; a disconnected hub drops every message.
        Receiving = Connecting | Joining | Joined | Leaving,

        // Every receiving state but Joined.
        NotJoined = Connecting | Joining | Leaving
    }

    // What the message table does with one server message type in each state.
    internal sealed class RealtimeMessageRoute
    {
        public RealtimeMessageRoute(
            Action<byte[]> handler,
            RealtimeStates handledIn,
            RealtimeStates droppedIn,
            string unexpectedCode,
            string unexpectedMessage)
        {
            Handler = handler;
            HandledIn = handledIn;
            DroppedIn = droppedIn;
            UnexpectedCode = unexpectedCode;
            UnexpectedMessage = unexpectedMessage;
        }

        public Action<byte[]> Handler { get; }

        // The states the handler runs in.
        public RealtimeStates HandledIn { get; }

        // The states the message is dropped in without a failure, such as the messages that the
        // worker sent before it took a leave.
        public RealtimeStates DroppedIn { get; }

        // In every other receiving state the message fails the session with this code.
        public string UnexpectedCode { get; }

        public string UnexpectedMessage { get; }
    }

    // Routes every server message to the handler that its part registered. The channel and
    // delivery that the generated descriptor names are checked first; then the entry decides by
    // the state whether the handler runs, the message is dropped, or it fails the session. A
    // client message, or a type without an entry, is unexpected. The hub seals the table once
    // every part registered, and a server message type without an entry fails that composition.
    internal sealed class RealtimeMessageTable
    {
        private readonly RealtimeSession session;
        private readonly RealtimeMessageRoute[] routes = new RealtimeMessageRoute[byte.MaxValue + 1];
        private bool isSealed;

        public RealtimeMessageTable(RealtimeSession session)
        {
            this.session = session;
        }

        // The states of handledIn and droppedIn never overlap. A message that arrives in a
        // receiving state that neither names fails the session with the unexpected code.
        public void Register(
            RealtimeMessageType type,
            Action<byte[]> handler,
            RealtimeStates handledIn,
            RealtimeStates droppedIn,
            string unexpectedCode,
            string unexpectedMessage)
        {
            if (isSealed)
            {
                throw new InvalidOperationException(
                    "The realtime message table is sealed; every part registers while the hub composes.");
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (!RealtimeProtocol.TryGetDescriptor(type, out var descriptor)
                || descriptor.Direction != RealtimeMessageDirection.ServerToClient)
            {
                throw new ArgumentException("Only a server message type has an entry: " + type + ".", nameof(type));
            }

            if (routes[(int)type] != null)
            {
                throw new InvalidOperationException("The realtime message type " + type + " already has an entry.");
            }

            if (handledIn == RealtimeStates.None
                || (handledIn & ~RealtimeStates.Receiving) != 0
                || (droppedIn & ~RealtimeStates.Receiving) != 0
                || (handledIn & droppedIn) != 0)
            {
                throw new ArgumentException(
                    "The realtime message type " + type + " needs states to handle it in, apart from those that drop it.",
                    nameof(handledIn));
            }

            if ((handledIn | droppedIn) != RealtimeStates.Receiving && string.IsNullOrWhiteSpace(unexpectedCode))
            {
                throw new ArgumentException(
                    "The realtime message type " + type + " fails in some states and needs a code for them.",
                    nameof(unexpectedCode));
            }

            routes[(int)type] = new RealtimeMessageRoute(
                handler,
                handledIn,
                droppedIn,
                unexpectedCode,
                unexpectedMessage);
        }

        // Fails when a server message type of the protocol has no entry.
        public void Seal()
        {
            var missing = new List<string>();
            foreach (RealtimeMessageType type in Enum.GetValues(typeof(RealtimeMessageType)))
            {
                if (RealtimeProtocol.TryGetDescriptor(type, out var descriptor)
                    && descriptor.Direction == RealtimeMessageDirection.ServerToClient
                    && routes[(int)type] == null)
                {
                    missing.Add(type.ToString());
                }
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    "These server message types have no entry in the realtime message table: "
                    + string.Join(", ", missing) + ".");
            }

            isSealed = true;
        }

        // Whether a message of the type that arrives in the state is dropped without a failure.
        public bool IsDropped(RealtimeMessageType type, RealtimeConnectionState state)
        {
            if (state == RealtimeConnectionState.Disconnected)
            {
                return true;
            }

            var route = routes[(int)type];
            return route != null && (route.DroppedIn & StatesOf(state)) != 0;
        }

        public void Dispatch(byte[] packet, byte channel, DeliveryMethod deliveryMethod)
        {
            // The session already ended in this poll; later packets must not replace the first
            // failure reason.
            if (session.State == RealtimeConnectionState.Disconnected)
            {
                return;
            }

            if (!RealtimeProtocol.TryReadMessageType(packet, out var messageType)
                || !RealtimeProtocol.TryGetDescriptor(messageType, out var descriptor))
            {
                session.FailProtocol("invalid_packet", "SimulationWorker returned an invalid realtime packet.");
                return;
            }

            // The protocol schema declares which messages travel unreliably: the snapshots.
            if (descriptor.Delivery == RealtimeDelivery.Unreliable)
            {
                if (channel != descriptor.Channel
                    || deliveryMethod != DeliveryMethod.Unreliable)
                {
                    session.FailProtocol(
                        "invalid_snapshot_delivery",
                        "Simulation snapshots require unreliable delivery.");
                    return;
                }
            }
            else if (channel != RealtimeProtocol.ControlChannel
                || deliveryMethod != DeliveryMethod.ReliableOrdered)
            {
                session.FailProtocol(
                    "invalid_control_delivery",
                    "SimulationWorker control messages require the reliable ordered control channel.");
                return;
            }

            var route = routes[(int)messageType];
            if (route == null)
            {
                session.FailProtocol(
                    "unexpected_message",
                    "SimulationWorker returned a message that is invalid for clients.");
                return;
            }

            var state = StatesOf(session.State);
            if ((route.DroppedIn & state) != 0)
            {
                return;
            }

            if ((route.HandledIn & state) == 0)
            {
                session.FailProtocol(route.UnexpectedCode, route.UnexpectedMessage);
                return;
            }

            route.Handler(packet);
        }

        private static RealtimeStates StatesOf(RealtimeConnectionState state)
        {
            return (RealtimeStates)(1 << (int)state);
        }
    }
}
