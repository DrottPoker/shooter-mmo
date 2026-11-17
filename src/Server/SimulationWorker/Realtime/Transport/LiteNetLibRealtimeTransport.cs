using System.Diagnostics.CodeAnalysis;
using LiteNetLib;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;

namespace Mmorpg.SimulationWorker.Realtime;

// Owns the LiteNetLib NetManager. UnsyncedEvents stays false, so every LiteNetLib
// callback runs inside PollEvents on the realtime loop. A freed peer id can therefore be
// reused only after its disconnect event was handled, which makes resolving peers by id
// safe. A connection request, connect, or disconnect callback that throws is logged and costs
// only its own peer, so PollEvents goes on with the other events. Every datagram passes the
// sealing layer, which holds the keys of the joined peers until they disconnect. Loop-confined.
internal sealed class LiteNetLibRealtimeTransport(
    SimulationWorkerConfig config,
    RealtimeConnectionAdmission admission,
    RealtimeLoopGuard loopGuard,
    ILogger logger,
    RealtimeNetworkMetrics? networkMetrics = null) : IRealtimePeerDirectory
{
    private readonly RealtimeWorkerTransportSecurity security = new();

    // Peers that the worker disconnected and whose disconnect LiteNetLib still sends again until
    // the client confirms it; they keep their keys until then, so the disconnect stays sealed.
    private readonly List<NetPeer> retiringPeers = [];
    private NetManager? server;

    // The keys of the sealed peers and the counts of every datagram.
    public RealtimeWorkerTransportSecurity Security => security;

    public void Start(IRealtimePeerLifecycle lifecycle, RealtimeMessageDispatcher dispatcher)
    {
        if (!RealtimeAead.IsPlatformSupported)
        {
            logger.LogWarning(
                "[SIMULATION] The platform offers no ChaCha20-Poly1305, so the realtime transport seals its datagrams with the managed implementation.");
        }

        networkMetrics?.AttachTransport(security.Counters);
        var listener = new EventBasedNetListener();
        server = new NetManager(listener, new RealtimeSealingPacketLayer(security))
        {
            DisconnectTimeout = (int)config.JoinHandshakeTimeout.TotalMilliseconds,
            ChannelsCount = RealtimeProtocol.ChannelCount,
            // The layer's bytes come on top of LiteNetLib's packets, which keep their limits.
            MtuOverride = RealtimeDatagram.LiteNetLibMtu,
            PacketPoolSize = PacketPoolSizeFor(
                config.MaxConnections,
                config.UdpQuotas.AggregateSnapshotBytesPerSecond / config.SnapshotRateHz)
        };

        listener.ConnectionRequestEvent += request =>
        {
            bool admitted;
            try
            {
                admitted = admission.TryAdmit(
                    request.RemoteEndPoint.Address,
                    server.ConnectedPeersCount);
            }
            catch (Exception exception)
            {
                loopGuard.ConnectionRequestFailed(request.RemoteEndPoint, exception);
                admitted = false;
            }

            if (!admitted)
            {
                request.Reject();
                return;
            }

            request.AcceptIfKey(RealtimeProtocol.ConnectionKey);
        };
        listener.PeerConnectedEvent += peer =>
        {
            try
            {
                lifecycle.OnPeerConnected(peer.Id, peer.Address);
            }
            catch (Exception exception)
            {
                loopGuard.PeerFailed("peer connect", peer.Id, exception);
                server.DisconnectPeer(peer);
            }
        };
        listener.PeerDisconnectedEvent += (peer, disconnectInfo) =>
        {
            try
            {
                lifecycle.OnPeerDisconnected(peer.Id, disconnectInfo.Reason);
            }
            catch (Exception exception)
            {
                // The peer is gone already; what its cleanup did not reach stays to the orphan sweep.
                loopGuard.PeerFailed("peer disconnect", peer.Id, exception);
            }
            finally
            {
                // The keys go with the connection; a reconnect brings a new ticket and new keys.
                RetireKeys(peer);
            }
        };
        listener.NetworkReceiveEvent += (peer, reader, channel, deliveryMethod) =>
        {
            try
            {
                dispatcher.Dispatch(
                    LiteNetLibPeerConnection.For(peer, security),
                    reader.GetRemainingBytes(),
                    channel,
                    deliveryMethod);
            }
            finally
            {
                reader.Recycle();
            }
        };
        // UnsyncedDeliveryEvent stays false too, so the acknowledgement of a disconnect reason
        // arrives inside PollEvents on the loop.
        listener.DeliveryEvent += (_, userData) => (userData as RealtimePendingDisconnect)?.MarkAcknowledged();
        listener.NetworkErrorEvent += (endPoint, socketError) =>
        {
            logger.LogWarning(
                "[SIMULATION] UDP transport error from {EndPoint}: {SocketError}.",
                endPoint,
                socketError);
        };

        if (!server.Start(config.UdpPort))
        {
            throw new InvalidOperationException(
                $"SimulationWorker could not bind UDP port {config.UdpPort}.");
        }
    }

    public void PollEvents()
    {
        server!.PollEvents();
        RemoveKeysOfRetiredPeers();
    }

    // Wakes the transport's update thread, so the packets queued since the last call leave
    // now instead of on its next periodic update.
    public void FlushSends()
    {
        server!.TriggerUpdate();
    }

    // A snapshot broadcast queues an owner packet for every connection plus the remote chunks
    // that the aggregate snapshot budget allows before the update thread sends and recycles
    // them. The pool covers that at half the 1 KB chunk size. The default pool of 1000 packets
    // made every packet beyond it a new allocation that survived into the older GC generations.
    internal static int PacketPoolSizeFor(int maxConnections, int aggregateSnapshotBytesPerBroadcast)
    {
        var packets = (long)maxConnections + (aggregateSnapshotBytesPerBroadcast / 512L);
        return (int)Math.Clamp(packets, 1_000L, 65_536L);
    }

    // Undelivered events are dropped; nothing resolves peers after this. Every key is zeroed.
    public void Stop()
    {
        server!.Stop();
        server = null;
        retiringPeers.Clear();
        security.Clear();
    }

    // A peer that the worker disconnected waits for the client to confirm its disconnect, or for
    // the disconnect timeout, while LiteNetLib sends the disconnect with its reason again every
    // 300 ms; those datagrams must stay sealed for a client that seals.
    private void RetireKeys(NetPeer peer)
    {
        if (peer.ConnectionState == ConnectionState.ShutdownRequested)
        {
            retiringPeers.Add(peer);
            return;
        }

        security.Remove(peer);
    }

    private void RemoveKeysOfRetiredPeers()
    {
        for (var index = retiringPeers.Count - 1; index >= 0; index--)
        {
            var peer = retiringPeers[index];
            if (peer.ConnectionState != ConnectionState.ShutdownRequested)
            {
                retiringPeers.RemoveAt(index);
                security.Remove(peer);
            }
        }
    }

    public bool TryGetConnectedPeer(
        int peerId,
        [NotNullWhen(true)] out IRealtimePeerConnection? peer)
    {
        peer = null;
        if (server is null)
        {
            return false;
        }

        var candidate = server.GetPeerById(peerId) as NetPeer;
        if (candidate is null || candidate.ConnectionState != ConnectionState.Connected)
        {
            return false;
        }

        peer = LiteNetLibPeerConnection.For(candidate, security);
        return true;
    }

    public void DisconnectPeer(IRealtimePeerConnection peer, byte[]? data)
    {
        var netPeer = ((LiteNetLibPeerConnection)peer).Peer;
        if (data is null)
        {
            server!.DisconnectPeer(netPeer);
            return;
        }

        // LiteNetLib would send zeros of that length instead of longer data.
        if (data.Length > RealtimeDisconnectData.MaximumBytes)
        {
            throw new ArgumentException(
                $"Disconnect data of {data.Length} bytes exceeds the {RealtimeDisconnectData.MaximumBytes} bytes that LiteNetLib delivers.",
                nameof(data));
        }

        server!.DisconnectPeer(netPeer, data);
    }

    // Whether a disconnected peer still waits for its client to confirm the disconnect, while
    // LiteNetLib sends the disconnect again every 300 ms.
    public bool AwaitsDisconnectConfirmation(IRealtimePeerConnection peer)
    {
        return ((LiteNetLibPeerConnection)peer).Peer.ConnectionState == ConnectionState.ShutdownRequested;
    }
}
