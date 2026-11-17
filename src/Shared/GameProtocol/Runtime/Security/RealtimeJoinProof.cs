using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Mmorpg.GameProtocol
{
    // The proof in a join request that the client holds the transport key of its ticket, without
    // sending the key: HMAC-SHA256 with the proof key over a fixed label, the protocol version,
    // the ticket id, the Shard id, and a fresh client nonce. GameService checks it in constant
    // time before it consumes the ticket, so the ticket id alone joins nobody.
    public static class RealtimeJoinProof
    {
        public const int NonceSize = RealtimeProtocol.JoinNonceSize;
        public const int ProofSize = RealtimeProtocol.JoinProofSize;

        private const int MaximumShardIdBytes = ushort.MaxValue;

        private static readonly byte[] Label = Encoding.ASCII.GetBytes("Mmorpg.Realtime.v1 join request");

        // A fresh nonce from the system's cryptographic random source.
        public static byte[] CreateNonce()
        {
            var nonce = new byte[NonceSize];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(nonce);
            }

            return nonce;
        }

        // The label, then the version as a u16, the ticket id as 16 bytes in the order of
        // Guid.ToByteArray, the Shard id as a u16 UTF-8 length and its bytes, and the nonce.
        public static byte[] Compute(byte[] proofKey, ushort protocolVersion, Guid ticketId, string shardId, byte[] clientNonce)
        {
            if (proofKey == null || proofKey.Length != RealtimeAead.KeySize)
            {
                throw new ArgumentException($"The proof key must have {RealtimeAead.KeySize} bytes.", nameof(proofKey));
            }

            if (clientNonce == null || clientNonce.Length != NonceSize)
            {
                throw new ArgumentException($"The client nonce must have {NonceSize} bytes.", nameof(clientNonce));
            }

            var shardBytes = Encoding.UTF8.GetBytes(shardId ?? throw new ArgumentNullException(nameof(shardId)));
            if (shardBytes.Length > MaximumShardIdBytes)
            {
                throw new ArgumentException("The Shard id is too long.", nameof(shardId));
            }

            var message = new byte[Label.Length + 2 + 16 + 2 + shardBytes.Length + NonceSize];
            var span = new Span<byte>(message);
            Label.CopyTo(span);
            var offset = Label.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset), protocolVersion);
            offset += 2;
            ticketId.ToByteArray().CopyTo(span.Slice(offset));
            offset += 16;
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset), (ushort)shardBytes.Length);
            offset += 2;
            shardBytes.CopyTo(span.Slice(offset));
            offset += shardBytes.Length;
            clientNonce.CopyTo(span.Slice(offset));

            using (var hmac = new HMACSHA256(proofKey))
            {
                return hmac.ComputeHash(message);
            }
        }

        // The join request of a ticket: its id, a fresh nonce, and the proof of the nonce.
        public static RealtimeJoinRequest CreateRequest(byte[] proofKey, Guid ticketId, string shardId)
        {
            var nonce = CreateNonce();
            return new RealtimeJoinRequest(
                ticketId,
                nonce,
                Compute(proofKey, RealtimeProtocol.Version, ticketId, shardId, nonce));
        }

        // Whether the proof is the one the proof key makes, compared in constant time. False for a
        // proof or nonce of the wrong size.
        public static bool Verify(byte[] proofKey, ushort protocolVersion, Guid ticketId, string shardId, byte[] clientNonce, byte[] proof)
        {
            if (proof == null || proof.Length != ProofSize || clientNonce == null || clientNonce.Length != NonceSize || shardId == null)
            {
                return false;
            }

            var expected = Compute(proofKey, protocolVersion, ticketId, shardId, clientNonce);
            try
            {
                return RealtimeConstantTime.AreEqual(expected, proof);
            }
            finally
            {
                Array.Clear(expected, 0, expected.Length);
            }
        }
    }
}
