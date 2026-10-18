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
    SessionAuthorityOperations sessionOperations,
    RealtimeNetworkMetrics networkMetrics,
    ILogger logger,
    PlayerCombatAuthority? combatAuthority = null)
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

        if (context.Session is not null)
        {
            if (sessionStore.IsSupersededByReconnect(context.Session))
            {
                teardown.DetachSupersededPeer(context);
            }
            else
            {
                // Spent arrows and a death that did not commit yet keep the session until
                // they committed; combat then releases it.
                var session = context.Session;
                var deferred = combatAuthority?.TryDeferRelease(context, session) == true;
                teardown.RemoveBoundEntity(context, session, "connection_closed");
                if (!deferred)
                {
                    sessionOperations.ReleaseDetachedInBackground(session);
                }
            }
        }

        logger.LogDebug(
            "[SIMULATION] UDP peer {PeerId} disconnected: {Reason}.",
            peerId,
            reason);
    }
}
