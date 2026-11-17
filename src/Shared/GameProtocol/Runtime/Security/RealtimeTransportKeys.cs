using System;
using System.Security.Cryptography;
using System.Text;

namespace Mmorpg.GameProtocol
{
    // The keys of one join, derived with HKDF-SHA256 from the transport key that GameService made
    // with the join ticket: the proof key, with which the client proves that it holds the
    // transport key, and one key for each direction of the sealed datagrams. Nothing uses the
    // transport key itself. Dispose zeroes them.
    public sealed class RealtimeTransportKeys : IDisposable
    {
        public const int TransportKeySize = 32;

        private static readonly byte[] ProofLabel = Encoding.ASCII.GetBytes("Mmorpg.Realtime.v1 join proof");
        private static readonly byte[] ClientToWorkerLabel = Encoding.ASCII.GetBytes("Mmorpg.Realtime.v1 client to worker");
        private static readonly byte[] WorkerToClientLabel = Encoding.ASCII.GetBytes("Mmorpg.Realtime.v1 worker to client");

        private RealtimeTransportKeys(byte[] proofKey, byte[] clientToWorkerKey, byte[] workerToClientKey)
        {
            ProofKey = proofKey;
            ClientToWorkerKey = clientToWorkerKey;
            WorkerToClientKey = workerToClientKey;
        }

        public byte[] ProofKey { get; }

        public byte[] ClientToWorkerKey { get; }

        public byte[] WorkerToClientKey { get; }

        // 32 bytes from the system's cryptographic random source.
        public static byte[] CreateTransportKey()
        {
            var key = new byte[TransportKeySize];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(key);
            }

            return key;
        }

        public static RealtimeTransportKeys Derive(byte[] transportKey)
        {
            if (transportKey == null || transportKey.Length != TransportKeySize)
            {
                throw new ArgumentException($"The transport key must have {TransportKeySize} bytes.", nameof(transportKey));
            }

            var pseudoRandomKey = RealtimeHkdf.Extract(null, transportKey);
            try
            {
                return new RealtimeTransportKeys(
                    RealtimeHkdf.Expand(pseudoRandomKey, RealtimeAead.KeySize, ProofLabel),
                    RealtimeHkdf.Expand(pseudoRandomKey, RealtimeAead.KeySize, ClientToWorkerLabel),
                    RealtimeHkdf.Expand(pseudoRandomKey, RealtimeAead.KeySize, WorkerToClientLabel));
            }
            finally
            {
                Array.Clear(pseudoRandomKey, 0, pseudoRandomKey.Length);
            }
        }

        // The proof key alone, for GameService, which never needs the datagram keys.
        public static byte[] DeriveProofKey(byte[] transportKey)
        {
            if (transportKey == null || transportKey.Length != TransportKeySize)
            {
                throw new ArgumentException($"The transport key must have {TransportKeySize} bytes.", nameof(transportKey));
            }

            return RealtimeHkdf.DeriveKey(transportKey, RealtimeAead.KeySize, null, ProofLabel);
        }

        public void Dispose()
        {
            Array.Clear(ProofKey, 0, ProofKey.Length);
            Array.Clear(ClientToWorkerKey, 0, ClientToWorkerKey.Length);
            Array.Clear(WorkerToClientKey, 0, WorkerToClientKey.Length);
        }
    }
}
