using System.Threading;

namespace Mmorpg.GameProtocol
{
    // Why the transport layer dropped a datagram before LiteNetLib saw it.
    public enum RealtimeDatagramDrop
    {
        // Empty, an unknown first byte, or too short for its kind.
        Malformed,

        // A sealed datagram whose tag does not authenticate, or one without keys to open it.
        Unauthenticated,

        // A sealed datagram whose counter was accepted before or lies below the replay window.
        Replayed,

        // A clear datagram after the session sealed.
        ClearAfterSealing,

        // A clear datagram that carries what only a sealed one may carry.
        ClearNotAllowed
    }

    // The datagrams one side's transport layer sent and received, the bytes it added, and the
    // datagrams it dropped by reason. Every count only grows; readers take differences.
    // Thread-safe.
    public sealed class RealtimeTransportSecurityCounters
    {
        private long datagramsSent;
        private long datagramsReceived;
        private long sealedSent;
        private long sealedReceived;
        private long bytesSent;
        private long bytesReceived;
        private long overheadBytesSent;
        private long overheadBytesReceived;
        private long droppedMalformed;
        private long droppedUnauthenticated;
        private long droppedReplayed;
        private long droppedClearAfterSealing;
        private long droppedClearNotAllowed;

        public long DatagramsSent => Interlocked.Read(ref datagramsSent);

        public long DatagramsReceived => Interlocked.Read(ref datagramsReceived);

        public long SealedSent => Interlocked.Read(ref sealedSent);

        public long SealedReceived => Interlocked.Read(ref sealedReceived);

        // Every byte of the datagrams, the layer's own bytes included.
        public long BytesSent => Interlocked.Read(ref bytesSent);

        public long BytesReceived => Interlocked.Read(ref bytesReceived);

        // The bytes that the layer added: the marker of a clear datagram, and the marker, counter,
        // and tag of a sealed one.
        public long OverheadBytesSent => Interlocked.Read(ref overheadBytesSent);

        public long OverheadBytesReceived => Interlocked.Read(ref overheadBytesReceived);

        public long DroppedMalformed => Interlocked.Read(ref droppedMalformed);

        public long DroppedUnauthenticated => Interlocked.Read(ref droppedUnauthenticated);

        public long DroppedReplayed => Interlocked.Read(ref droppedReplayed);

        public long DroppedClearAfterSealing => Interlocked.Read(ref droppedClearAfterSealing);

        public long DroppedClearNotAllowed => Interlocked.Read(ref droppedClearNotAllowed);

        public long Dropped =>
            DroppedMalformed + DroppedUnauthenticated + DroppedReplayed + DroppedClearAfterSealing + DroppedClearNotAllowed;

        public long DroppedFor(RealtimeDatagramDrop reason)
        {
            switch (reason)
            {
                case RealtimeDatagramDrop.Malformed:
                    return DroppedMalformed;
                case RealtimeDatagramDrop.Unauthenticated:
                    return DroppedUnauthenticated;
                case RealtimeDatagramDrop.Replayed:
                    return DroppedReplayed;
                case RealtimeDatagramDrop.ClearAfterSealing:
                    return DroppedClearAfterSealing;
                default:
                    return DroppedClearNotAllowed;
            }
        }

        internal void RecordSent(int datagramBytes, bool isSealed)
        {
            Interlocked.Increment(ref datagramsSent);
            Interlocked.Add(ref bytesSent, datagramBytes);
            if (isSealed)
            {
                Interlocked.Increment(ref sealedSent);
                Interlocked.Add(ref overheadBytesSent, RealtimeDatagram.SealedOverhead);
            }
            else
            {
                Interlocked.Add(ref overheadBytesSent, RealtimeDatagram.ClearOverhead);
            }
        }

        internal void RecordReceived(int datagramBytes)
        {
            Interlocked.Increment(ref datagramsReceived);
            Interlocked.Add(ref bytesReceived, datagramBytes);
        }

        internal void RecordAccepted(bool isSealed)
        {
            if (isSealed)
            {
                Interlocked.Increment(ref sealedReceived);
                Interlocked.Add(ref overheadBytesReceived, RealtimeDatagram.SealedOverhead);
            }
            else
            {
                Interlocked.Add(ref overheadBytesReceived, RealtimeDatagram.ClearOverhead);
            }
        }

        // Also for a datagram that a packet layer could not process at all.
        public void RecordDropped(RealtimeDatagramDrop reason)
        {
            switch (reason)
            {
                case RealtimeDatagramDrop.Malformed:
                    Interlocked.Increment(ref droppedMalformed);
                    break;
                case RealtimeDatagramDrop.Unauthenticated:
                    Interlocked.Increment(ref droppedUnauthenticated);
                    break;
                case RealtimeDatagramDrop.Replayed:
                    Interlocked.Increment(ref droppedReplayed);
                    break;
                case RealtimeDatagramDrop.ClearAfterSealing:
                    Interlocked.Increment(ref droppedClearAfterSealing);
                    break;
                default:
                    Interlocked.Increment(ref droppedClearNotAllowed);
                    break;
            }
        }
    }
}
