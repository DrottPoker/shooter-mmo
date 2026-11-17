using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;

namespace Mmorpg.GameProtocol
{
    // The worker's side of the sealed transport, which its LiteNetLib packet layer calls for every
    // datagram, with the keys of every joined peer by its endpoint. The worker installs a peer's
    // keys when GameService consumed its ticket, and from then on seals every datagram to that
    // endpoint and drops every clear one from it. A peer without keys may only connect and send
    // its join in clear. A datagram that does not authenticate is dropped and counted, never
    // answered. Keys go when the peer disconnects. Thread-safe.
    public sealed class RealtimeWorkerTransportSecurity : IDisposable
    {
        private readonly ConcurrentDictionary<EndpointKey, RealtimeSealedSession> sessions =
            new ConcurrentDictionary<EndpointKey, RealtimeSealedSession>(EndpointKeyComparer.Instance);

        private readonly Func<byte[], IRealtimeAead> createAead;

        public RealtimeWorkerTransportSecurity(Func<byte[], IRealtimeAead> createAead = null)
        {
            this.createAead = createAead ?? RealtimeAead.Create;
        }

        public RealtimeTransportSecurityCounters Counters { get; } = new RealtimeTransportSecurityCounters();

        // The peers whose datagrams are sealed now.
        public int SealedPeerCount => sessions.Count;

        // Seals the peer from now on; keys it held before are zeroed.
        public void Install(IPEndPoint endPoint, RealtimeTransportKeys keys)
        {
            if (endPoint == null)
            {
                throw new ArgumentNullException(nameof(endPoint));
            }

            var next = RealtimeSealedSession.ForWorker(keys, createAead);
            RealtimeSealedSession previous = null;
            sessions.AddOrUpdate(
                new EndpointKey(endPoint),
                next,
                (key, existing) =>
                {
                    previous = existing;
                    return next;
                });
            previous?.Dispose();
        }

        public bool IsSealed(IPEndPoint endPoint)
        {
            return endPoint != null && sessions.ContainsKey(new EndpointKey(endPoint));
        }

        // Forgets and zeroes the keys of a peer that went.
        public void Remove(IPEndPoint endPoint)
        {
            if (endPoint != null && sessions.TryRemove(new EndpointKey(endPoint), out var session))
            {
                session.Dispose();
            }
        }

        public void Clear()
        {
            foreach (var pair in sessions)
            {
                if (sessions.TryRemove(pair.Key, out var session))
                {
                    session.Dispose();
                }
            }
        }

        public void Dispose()
        {
            Clear();
        }

        public void ProcessInbound(IPEndPoint endPoint, byte[] data, ref int length)
        {
            Counters.RecordReceived(length);
            if (length < 1 || endPoint == null)
            {
                Drop(RealtimeDatagramDrop.Malformed, ref length);
                return;
            }

            var marker = data[0];
            if (sessions.TryGetValue(new EndpointKey(endPoint), out var session))
            {
                if (marker == RealtimeDatagram.SealedMarker)
                {
                    var result = session.TryOpen(data, ref length);
                    if (result == RealtimeOpenResult.Opened)
                    {
                        Counters.RecordAccepted(isSealed: true);
                        return;
                    }

                    Drop(RealtimeClientTransportSecurity.ToDrop(result), ref length);
                    return;
                }

                Drop(
                    marker == RealtimeDatagram.ClearMarker ? RealtimeDatagramDrop.ClearAfterSealing : RealtimeDatagramDrop.Malformed,
                    ref length);
                return;
            }

            switch (marker)
            {
                case RealtimeDatagram.ClearMarker:
                    if (!RealtimeClearDatagramRules.IsAllowedFromClient(new ReadOnlySpan<byte>(data, 1, length - 1)))
                    {
                        Drop(RealtimeDatagramDrop.ClearNotAllowed, ref length);
                        return;
                    }

                    RealtimeDatagram.UnwrapClear(data, ref length);
                    Counters.RecordAccepted(isSealed: false);
                    return;
                case RealtimeDatagram.SealedMarker:
                    // No keys for this endpoint, so nothing can open it.
                    Drop(RealtimeDatagramDrop.Unauthenticated, ref length);
                    return;
                default:
                    Drop(RealtimeDatagramDrop.Malformed, ref length);
                    return;
            }
        }

        public void ProcessOutbound(IPEndPoint endPoint, byte[] data, ref int offset, ref int length)
        {
            if (endPoint != null && sessions.TryGetValue(new EndpointKey(endPoint), out var session))
            {
                // A session whose keys went this moment sends an empty datagram, never a clear one.
                if (!session.TrySeal(data, offset, ref length))
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

        // An endpoint by its address and port, so a LiteNetLib peer and the endpoint that a
        // datagram arrived from find the same keys whatever hash code their types use.
        private readonly struct EndpointKey
        {
            public EndpointKey(IPEndPoint endPoint)
            {
                Address = endPoint.Address;
                Port = endPoint.Port;
            }

            public IPAddress Address { get; }

            public int Port { get; }
        }

        private sealed class EndpointKeyComparer : IEqualityComparer<EndpointKey>
        {
            public static readonly EndpointKeyComparer Instance = new EndpointKeyComparer();

            public bool Equals(EndpointKey left, EndpointKey right)
            {
                return left.Port == right.Port && left.Address.Equals(right.Address);
            }

            public int GetHashCode(EndpointKey key)
            {
                return (key.Address.GetHashCode() * 397) ^ key.Port;
            }
        }
    }
}
