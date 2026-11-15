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
// only its own peer, so PollEvents goes on with the other events. Loop-confined.
internal sealed class LiteNetLibRealtimeTransport(
    SimulationWorkerConfig config,
    RealtimeConnectionAdmission admission,
    RealtimeLoopGuard loopGuard,
    ILogger logger) : IRealtimePeerDirectory
{
    private NetManager? server;

    public void Start(IRealtimePeerLifecycle lifecycle, RealtimeMessageDispatcher dispatcher)
    {
        var listener = new EventBasedNetListener();
        server = new NetManager(listener)
        {
            DisconnectTimeout = (int)config.JoinHandshakeTimeout.TotalMilliseconds,
            ChannelsCount = RealtimeProtocol.ChannelCount,
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
        };
        listener.NetworkReceiveEvent += (peer, reader, channel, deliveryMethod) =>
        {
            try
            {
                dispatcher.Dispatch(
                    LiteNetLibPeerConnection.For(peer),
                    reader.GetRemainingBytes(),
                    channel,
                    deliveryMethod);
            }
            finally
            {
                reader.Recycle();
            }
        };
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

    // Undelivered events are dropped; nothing resolves peers after this.
    public void Stop()
    {
        server!.Stop();
        server = null;
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

        peer = LiteNetLibPeerConnection.For(candidate);
        return true;
    }

    public void DisconnectPeer(IRealtimePeerConnection peer)
    {
        server!.DisconnectPeer(((LiteNetLibPeerConnection)peer).Peer);
    }
}
