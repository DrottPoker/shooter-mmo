using System;
using System.Threading;

namespace Mmorpg.GameProtocol
{
    // The client's side of the sealed transport, which its LiteNetLib packet layer calls for every
    // datagram. The client holds the keys before it connects. It sends clear datagrams until the
    // first sealed datagram of the worker authenticates; from then on it seals everything it
    // sends and drops every clear datagram. Before that, a clear datagram may carry only
    // LiteNetLib's connection control, so nothing of the session reaches the client in clear.
    // A datagram that does not authenticate is dropped and counted, never answered.
    public sealed class RealtimeClientTransportSecurity : IDisposable
    {
        private RealtimeSealedSession session;
        private int sealing;

        public RealtimeTransportSecurityCounters Counters { get; } = new RealtimeTransportSecurityCounters();

        // Whether the worker's sealed datagrams authenticated, so the client seals what it sends.
        public bool IsSealing => Volatile.Read(ref sealing) != 0;

        // The keys of the next connection; the previous ones are zeroed.
        public void Install(RealtimeTransportKeys keys, Func<byte[], IRealtimeAead> createAead = null)
        {
            var next = RealtimeSealedSession.ForClient(keys, createAead);
            Volatile.Write(ref sealing, 0);
            Interlocked.Exchange(ref session, next)?.Dispose();
        }

        // Forgets and zeroes the keys, as when the connection ends.
        public void Reset()
        {
            Volatile.Write(ref sealing, 0);
            Interlocked.Exchange(ref session, null)?.Dispose();
        }

        public void Dispose()
        {
            Reset();
        }

        public void ProcessInbound(byte[] data, ref int length)
        {
            Counters.RecordReceived(length);
            if (length < 1)
            {
                Drop(RealtimeDatagramDrop.Malformed, ref length);
                return;
            }

            switch (data[0])
            {
                case RealtimeDatagram.SealedMarker:
                    var current = Volatile.Read(ref session);
                    var result = current == null ? RealtimeOpenResult.Closed : current.TryOpen(data, ref length);
                    if (result == RealtimeOpenResult.Opened)
                    {
                        Volatile.Write(ref sealing, 1);
                        Counters.RecordAccepted(isSealed: true);
                        return;
                    }

                    Drop(ToDrop(result), ref length);
                    return;
                case RealtimeDatagram.ClearMarker:
                    if (IsSealing)
                    {
                        Drop(RealtimeDatagramDrop.ClearAfterSealing, ref length);
                        return;
                    }

                    if (!RealtimeClearDatagramRules.IsAllowedFromWorker(new ReadOnlySpan<byte>(data, 1, length - 1)))
                    {
                        Drop(RealtimeDatagramDrop.ClearNotAllowed, ref length);
                        return;
                    }

                    RealtimeDatagram.UnwrapClear(data, ref length);
                    Counters.RecordAccepted(isSealed: false);
                    return;
                default:
                    Drop(RealtimeDatagramDrop.Malformed, ref length);
                    return;
            }
        }

        // Seals once the worker's datagrams are sealed. A session whose keys went sends an empty
        // datagram, which the worker drops, rather than a clear one.
        public void ProcessOutbound(byte[] data, ref int offset, ref int length)
        {
            if (IsSealing)
            {
                var current = Volatile.Read(ref session);
                if (current == null || !current.TrySeal(data, offset, ref length))
                {
                    length = 0;
                    return;
                }

                Counters.RecordSent(length, isSealed: true);
                return;
            }

            RealtimeDatagram.WrapClear(data, offset, ref length);
            Counters.RecordSent(length, isSealed: false);
        }

        private void Drop(RealtimeDatagramDrop reason, ref int length)
        {
            Counters.RecordDropped(reason);
            length = 0;
        }

        internal static RealtimeDatagramDrop ToDrop(RealtimeOpenResult result)
        {
            switch (result)
            {
                case RealtimeOpenResult.Malformed:
                    return RealtimeDatagramDrop.Malformed;
                case RealtimeOpenResult.Replayed:
                    return RealtimeDatagramDrop.Replayed;
                default:
                    return RealtimeDatagramDrop.Unauthenticated;
            }
        }
    }
}
