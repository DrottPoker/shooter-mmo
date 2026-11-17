using System;
using System.Buffers.Binary;

namespace Mmorpg.GameProtocol
{
    // The format of every UDP datagram between client and worker, LiteNetLib's own packets
    // included. The first byte says what follows: 0 a clear LiteNetLib packet, 1 a sealed one.
    // A sealed datagram is the byte 1, an 8-byte little-endian counter, the ciphertext of the
    // LiteNetLib packet, and the 16-byte tag. The nonce is four zero bytes and the counter; the
    // marker and the counter are the associated data.
    public static class RealtimeDatagram
    {
        public const byte ClearMarker = 0;
        public const byte SealedMarker = 1;
        public const int CounterSize = 8;
        public const int SealedHeaderSize = 1 + CounterSize;
        public const int ClearOverhead = 1;
        public const int SealedOverhead = SealedHeaderSize + RealtimeAead.TagSize;

        // The most bytes the layer adds to a LiteNetLib packet, which LiteNetLib reserves.
        public const int MaximumOverhead = SealedOverhead;

        // The MTU that every NetManager sets as its MtuOverride: the largest LiteNetLib packet,
        // an unreliable packet of the protocol and LiteNetLib's one-byte header, as before the
        // layer. Without it LiteNetLib would take the layer's bytes off its packets. A datagram is
        // therefore at most LiteNetLibMtu + MaximumOverhead bytes long.
        public const int LiteNetLibMtu = RealtimeProtocol.MaximumUnreliablePacketSize + 1;

        // Prefixes the LiteNetLib packet at data[offset..offset + length) with the clear marker,
        // in place. The buffer must hold one byte more.
        public static void WrapClear(byte[] data, int offset, ref int length)
        {
            Buffer.BlockCopy(data, offset, data, offset + ClearOverhead, length);
            data[offset] = ClearMarker;
            length += ClearOverhead;
        }

        // Moves the LiteNetLib packet of a clear datagram to the start of the buffer.
        public static void UnwrapClear(byte[] data, ref int length)
        {
            length -= ClearOverhead;
            Buffer.BlockCopy(data, ClearOverhead, data, 0, length);
        }

        // Seals the LiteNetLib packet at data[offset..offset + length) in place: the datagram then
        // starts at offset and is SealedOverhead bytes longer, which the buffer must hold.
        public static void Seal(IRealtimeAead aead, ulong counter, byte[] data, int offset, ref int length)
        {
            if (data.Length - offset < length + SealedOverhead)
            {
                throw new ArgumentException("The buffer has no room for the sealed datagram.", nameof(data));
            }

            Buffer.BlockCopy(data, offset, data, offset + SealedHeaderSize, length);
            data[offset] = SealedMarker;
            BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(data, offset + 1, CounterSize), counter);
            Span<byte> nonce = stackalloc byte[RealtimeAead.NonceSize];
            WriteNonce(counter, nonce);
            var body = new Span<byte>(data, offset + SealedHeaderSize, length);
            aead.Seal(
                nonce,
                body,
                body,
                new Span<byte>(data, offset + SealedHeaderSize + length, RealtimeAead.TagSize),
                new ReadOnlySpan<byte>(data, offset, SealedHeaderSize));
            length += SealedOverhead;
        }

        // Reads the counter of a sealed datagram at the start of the buffer; false when it is too
        // short to be one.
        public static bool TryReadCounter(byte[] data, int length, out ulong counter)
        {
            counter = 0;
            if (length < SealedOverhead || data[0] != SealedMarker)
            {
                return false;
            }

            counter = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(data, 1, CounterSize));
            return true;
        }

        // Opens a sealed datagram at the start of the buffer in place and moves the LiteNetLib
        // packet to the start. False when it does not authenticate.
        public static bool TryOpen(IRealtimeAead aead, ulong counter, byte[] data, ref int length)
        {
            var bodyLength = length - SealedOverhead;
            Span<byte> nonce = stackalloc byte[RealtimeAead.NonceSize];
            WriteNonce(counter, nonce);
            var body = new Span<byte>(data, SealedHeaderSize, bodyLength);
            if (!aead.TryOpen(
                    nonce,
                    body,
                    new ReadOnlySpan<byte>(data, SealedHeaderSize + bodyLength, RealtimeAead.TagSize),
                    body,
                    new ReadOnlySpan<byte>(data, 0, SealedHeaderSize)))
            {
                return false;
            }

            Buffer.BlockCopy(data, SealedHeaderSize, data, 0, bodyLength);
            length = bodyLength;
            return true;
        }

        private static void WriteNonce(ulong counter, Span<byte> nonce)
        {
            nonce.Slice(0, 4).Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(4), counter);
        }
    }
}
