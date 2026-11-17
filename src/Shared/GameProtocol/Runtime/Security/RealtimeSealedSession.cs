using System;

namespace Mmorpg.GameProtocol
{
    public enum RealtimeOpenResult
    {
        Opened,

        // Too short to be a sealed datagram.
        Malformed,

        // The tag does not authenticate: another key, or altered bytes.
        Unauthenticated,

        // A counter that was accepted before or lies below the replay window.
        Replayed,

        // The session's keys are gone.
        Closed
    }

    // The sealing state of one session on one side: the key and counter it seals with, and the
    // key and replay window it opens with. The counter of each direction starts at 0 and is never
    // used twice. Thread-safe: LiteNetLib seals from several threads and opens on its receive
    // thread, so sealing and opening each hold their own lock. Dispose zeroes the keys.
    public sealed class RealtimeSealedSession : IDisposable
    {
        private readonly object sealGate = new object();
        private readonly object openGate = new object();
        private readonly RealtimeReplayWindow window = new RealtimeReplayWindow();
        private readonly IRealtimeAead sealer;
        private readonly IRealtimeAead opener;
        private ulong nextCounter;
        private bool counterExhausted;
        private bool disposed;

        private RealtimeSealedSession(IRealtimeAead sealer, IRealtimeAead opener)
        {
            this.sealer = sealer;
            this.opener = opener;
            ImplementationName = sealer.ImplementationName;
        }

        public string ImplementationName { get; }

        // The client seals with the client-to-worker key and opens with the worker-to-client key.
        public static RealtimeSealedSession ForClient(RealtimeTransportKeys keys, Func<byte[], IRealtimeAead> createAead = null)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            var create = createAead ?? RealtimeAead.Create;
            return new RealtimeSealedSession(create(keys.ClientToWorkerKey), create(keys.WorkerToClientKey));
        }

        // The worker seals with the worker-to-client key and opens with the client-to-worker key.
        public static RealtimeSealedSession ForWorker(RealtimeTransportKeys keys, Func<byte[], IRealtimeAead> createAead = null)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            var create = createAead ?? RealtimeAead.Create;
            return new RealtimeSealedSession(create(keys.WorkerToClientKey), create(keys.ClientToWorkerKey));
        }

        // Seals the LiteNetLib packet at data[offset..offset + length) in place with the next
        // counter. False, with nothing written, when the session is closed or its counters ran out.
        public bool TrySeal(byte[] data, int offset, ref int length)
        {
            lock (sealGate)
            {
                if (disposed || counterExhausted)
                {
                    return false;
                }

                var counter = nextCounter;
                if (counter == ulong.MaxValue)
                {
                    counterExhausted = true;
                }
                else
                {
                    nextCounter = counter + 1;
                }

                RealtimeDatagram.Seal(sealer, counter, data, offset, ref length);
                return true;
            }
        }

        // Opens the sealed datagram at the start of the buffer in place. Only a datagram that
        // authenticates moves the replay window.
        public RealtimeOpenResult TryOpen(byte[] data, ref int length)
        {
            if (!RealtimeDatagram.TryReadCounter(data, length, out var counter))
            {
                return RealtimeOpenResult.Malformed;
            }

            lock (openGate)
            {
                if (disposed)
                {
                    return RealtimeOpenResult.Closed;
                }

                if (!window.Check(counter))
                {
                    return RealtimeOpenResult.Replayed;
                }

                if (!RealtimeDatagram.TryOpen(opener, counter, data, ref length))
                {
                    return RealtimeOpenResult.Unauthenticated;
                }

                window.Accept(counter);
                return RealtimeOpenResult.Opened;
            }
        }

        public void Dispose()
        {
            lock (sealGate)
            {
                lock (openGate)
                {
                    if (disposed)
                    {
                        return;
                    }

                    disposed = true;
                    sealer.Dispose();
                    opener.Dispose();
                }
            }
        }
    }
}
