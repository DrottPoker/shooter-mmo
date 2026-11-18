using System;
using System.Buffers.Binary;

namespace Mmorpg.GameProtocol
{
    // What a clear LiteNetLib packet may carry before its session seals. Nothing secret travels
    // in clear, but a forged clear packet must not put data into a channel that the session later
    // trusts, because LiteNetLib holds a reliable packet that arrives early and delivers it after
    // the packets before it. So a clear packet carries only LiteNetLib's connection control, and
    // from the client also the session channel with its join request. A worker that refuses a
    // peer before its keys says why only in the data of the disconnect.
    //
    // The values are LiteNetLib 2.1's packet properties, the low five bits of the first byte, and
    // its channel ids, the channel number times four plus the delivery method.
    public static class RealtimeClearDatagramRules
    {
        private const int PropertyMask = 0x1F;
        private const int ChanneledHeaderSize = 4;
        private const int MergedLengthSize = 2;

        private const byte Channeled = 1;
        private const byte ReliableMerged = 2;
        private const byte Ack = 3;
        private const byte Ping = 4;
        private const byte Pong = 5;
        private const byte ConnectRequest = 6;
        private const byte ConnectAccept = 7;
        private const byte Disconnect = 8;
        private const byte MtuCheck = 10;
        private const byte MtuOk = 11;
        private const byte Merged = 13;
        private const byte ShutdownOk = 14;
        private const byte PeerNotFound = 15;
        private const byte InvalidProtocol = 16;

        // LiteNetLib's DeliveryMethod.ReliableOrdered.
        private const int ReliableOrderedDelivery = 2;
        private const int DeliveryMethodCount = 4;

        // The channel id of the reliable ordered session channel that carries the join request.
        public const int SessionChannelId = (RealtimeProtocol.SessionChannel * DeliveryMethodCount) + ReliableOrderedDelivery;

        // A clear packet from the client to a worker that holds no keys for it.
        public static bool IsAllowedFromClient(ReadOnlySpan<byte> packet)
        {
            return IsAllowed(packet, fromClient: true, allowMerged: true);
        }

        // A clear packet from the worker to a client whose session has not sealed yet.
        public static bool IsAllowedFromWorker(ReadOnlySpan<byte> packet)
        {
            return IsAllowed(packet, fromClient: false, allowMerged: true);
        }

        private static bool IsAllowed(ReadOnlySpan<byte> packet, bool fromClient, bool allowMerged)
        {
            if (packet.Length < 1)
            {
                return false;
            }

            switch (packet[0] & PropertyMask)
            {
                case Ack:
                case Ping:
                case Pong:
                case Disconnect:
                case ShutdownOk:
                case MtuCheck:
                case MtuOk:
                    return true;
                case ConnectRequest:
                    return fromClient;
                case ConnectAccept:
                case PeerNotFound:
                case InvalidProtocol:
                    return !fromClient;
                case Channeled:
                case ReliableMerged:
                    return fromClient
                        && packet.Length >= ChanneledHeaderSize
                        && packet[ChanneledHeaderSize - 1] == SessionChannelId;
                case Merged:
                    return allowMerged && AreMergedPacketsAllowed(packet, fromClient);
                default:
                    return false;
            }
        }

        // A merged packet is a series of u16 lengths and packets, as LiteNetLib reads it; every
        // packet in it must be allowed on its own, and none may be merged again.
        private static bool AreMergedPacketsAllowed(ReadOnlySpan<byte> packet, bool fromClient)
        {
            var offset = 1;
            while (offset < packet.Length)
            {
                if (packet.Length - offset < MergedLengthSize)
                {
                    return false;
                }

                var size = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(offset));
                if (size == 0)
                {
                    break;
                }

                offset += MergedLengthSize;
                if (packet.Length - offset < size
                    || !IsAllowed(packet.Slice(offset, size), fromClient, allowMerged: false))
                {
                    return false;
                }

                offset += size;
            }

            return true;
        }
    }
}
