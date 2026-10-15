using System.Diagnostics.CodeAnalysis;
using LiteNetLib;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;

namespace Mmorpg.SimulationWorker.Realtime;

// Owns the LiteNetLib NetManager. UnsyncedEvents stays false, so every LiteNetLib
// callback runs inside PollEvents on the realtime loop. A freed peer id can therefore be
// reused only after its disconnect event was handled, which makes resolving peers by id
// safe. Loop-confined.
internal sealed class LiteNetLibRealtimeTransport(
    SimulationWorkerConfig config,
    RealtimeConnectionAdmission admission,
    ILogger logger) : IRealtimePeerDirectory
{
    private NetManager? server;

    public void Start(RealtimePeerLifecycle lifecycle, RealtimeMessageDispatcher dispatcher)
    {
        var listener = new EventBasedNetListener();
        server = new NetManager(listener)
        {
            DisconnectTimeout = (int)config.JoinHandshakeTimeout.TotalMilliseconds,
            ChannelsCount = RealtimeProtocol.ChannelCount
        };

        listener.ConnectionRequestEvent += request =>
        {
            if (!admission.TryAdmit(
                    request.RemoteEndPoint.Address,
                    server.ConnectedPeersCount))
            {
                request.Reject();
                return;
            }

            request.AcceptIfKey(RealtimeProtocol.ConnectionKey);
        };
        listener.PeerConnectedEvent += peer => lifecycle.OnPeerConnected(peer.Id, peer.Address);
        listener.PeerDisconnectedEvent += (peer, disconnectInfo) =>
            lifecycle.OnPeerDisconnected(peer.Id, disconnectInfo.Reason);
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
