using System.Net;
using LiteNetLib;
using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Registers connected peers and cleans up disconnected ones. The admission guard is
// released first, even for a peer the registry never knew. Loop-confined.
internal sealed class RealtimePeerLifecycle(
    SimulationWorkerConfig config,
    RealtimePeerRegistry peers,
    PeerAdmissionGuard admissionGuard,
    EntityPresenceReplicator presence,
    CorpseViewerRegistry corpseViewers,
    WorldInteractionLeaseRegistry? worldInteractionLeases,
    ActiveSimulationSessionStore sessionStore,
    RealtimeSessionTeardown teardown,
    RealtimeNetworkMetrics networkMetrics,
    ILogger logger)
{
    public void OnPeerConnected(int peerId, IPAddress address)
    {
        peers.Register(new RealtimePeerContext(peerId, DateTime.UtcNow, config.UdpQuotas));
        admissionGuard.OnPeerConnected(peerId, address);
        networkMetrics.SetActivePeers(peers.Count);
        peers.RefreshPopulationMetrics();
        logger.LogDebug(
            "[SIMULATION] UDP peer {PeerId} connected from {EndPoint} and must authenticate before joining.",
            peerId,
            address);
    }

    // A lost, timed out, or kicked connection leaves the character in the world as a lingering
    // body while its session goes on, also when a reconnect from another client takes the
    // session over. A session that ended already takes its entity with it.
    public void OnPeerDisconnected(int peerId, DisconnectReason reason)
    {
        admissionGuard.OnPeerDisconnected(peerId);
        if (!peers.Remove(peerId, out var context))
        {
            return;
        }

        presence.ForgetConnection(peerId);
        corpseViewers.RemovePeer(peerId);
        worldInteractionLeases?.RemovePeer(peerId);
        networkMetrics.SetActivePeers(peers.Count);
        peers.RefreshPopulationMetrics();

        if (context.Session is { } session)
        {
            if (sessionStore.IsCurrent(session, DateTime.UtcNow)
                || sessionStore.IsSupersededByReconnect(session))
            {
                teardown.LeaveBody(context, session);
            }
            else
            {
                teardown.RemoveBoundEntity(context, session, "connection_closed");
                context.Session = null;
            }
        }

        logger.LogDebug(
            "[SIMULATION] UDP peer {PeerId} disconnected: {Reason}.",
            peerId,
            reason);
    }
}
