using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// Runs once per loop iteration: completes delayed disconnects, drops peers that stop
// acknowledging, times out joins, detaches invalidated or superseded sessions, ends the
// connection of a session whose account session ended, and releases sessions that neither a
// peer nor a lingering body owns. Loop-confined.
internal sealed class RealtimePeerSupervisor(
    SimulationWorkerConfig config,
    RealtimePeerRegistry peers,
    IRealtimePeerDirectory directory,
    RealtimePeerSender sender,
    ActiveSimulationSessionStore sessionStore,
    RealtimeSessionTeardown teardown,
    LingeringBodies bodies,
    SessionAuthorityOperations sessionOperations,
    RealtimeNetworkMetrics networkMetrics,
    ILogger logger)
{
    private readonly Dictionary<Guid, DateTime> unboundSessionsSinceUtc = [];
    private DateTime nextOrphanSweepUtc = DateTime.MinValue;

    public void ClearUnboundSessions()
    {
        unboundSessionsSinceUtc.Clear();
    }

    public void EnforcePeerState()
    {
        var now = DateTime.UtcNow;
        foreach (var context in peers.Contexts.ToArray())
        {
            if (!peers.TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            if (context.DisconnectAfterUtc is not null && context.DisconnectAfterUtc <= now)
            {
                directory.DisconnectPeer(peer);
                continue;
            }

            // A client that never acknowledges reliable packets would grow this queue
            // without bound, so it is disconnected without another reliable message.
            var queuedReliablePackets = peer.GetPacketsCountInReliableQueue(
                RealtimeProtocol.ControlChannel,
                true);
            if (queuedReliablePackets > config.Admission.MaxReliableQueuedPackets)
            {
                logger.LogWarning(
                    "[SIMULATION] Disconnecting UDP peer {PeerId} with {QueuedPackets} unacknowledged reliable packets.",
                    context.PeerId,
                    queuedReliablePackets);
                networkMetrics.RecordConnectionRejected();
                directory.DisconnectPeer(peer);
                continue;
            }

            if (context.DisconnectAfterUtc is not null)
            {
                continue;
            }

            var activeSession = context.Session;
            if (activeSession is null)
            {
                if (now - context.ConnectedAtUtc > config.JoinHandshakeTimeout)
                {
                    sender.RejectProtocol(peer, context, "join_timeout", "The join handshake timed out.");
                }

                continue;
            }

            if (!IsCurrentSession(activeSession))
            {
                if (sessionStore.IsSupersededByReconnect(activeSession))
                {
                    // The reconnecting client takes the entity and carry state over; until its
                    // join binds them, the entity stands as a lingering body.
                    sender.SendControl(
                        peer,
                        RealtimeProtocol.EncodeServerDisconnect(
                            "session_reconnected",
                            "This character connected from another client."));
                    teardown.LeaveBody(context, activeSession);
                    context.ScheduleDisconnect(now);
                    continue;
                }

                var reason = sessionStore.TryTakeInvalidation(
                    activeSession.SimulationSessionId,
                    out var invalidation)
                    ? invalidation!
                    : new ActiveSimulationSessionInvalidation(
                        "session_revoked",
                        "The simulation session is no longer active.");
                sender.SendControl(
                    peer,
                    RealtimeProtocol.EncodeServerDisconnect(
                        reason.Code,
                        reason.Message));
                teardown.RemoveBoundEntity(context, activeSession, reason.Code);
                context.Session = null;
                peers.RefreshPopulationMetrics();
                context.ScheduleDisconnect(now);
                continue;
            }

            // The account session ended while the session goes on: the client goes with the code
            // it got before, and the character stays in the world as a lingering body.
            if (sessionStore.TryGetAccountSessionEnd(activeSession, out var accountSessionEnd))
            {
                var (code, message) = GameServiceOutcome.AccountSessionEndDisconnect(accountSessionEnd);
                sender.SendControl(peer, RealtimeProtocol.EncodeServerDisconnect(code, message));
                teardown.LeaveBody(context, activeSession);
                context.ScheduleDisconnect(now);
            }
        }
    }

    // Safety net: a local session that no peer has owned for a while is released, so a
    // missed cleanup path can never keep renewing a lease for a player who is gone. A lingering
    // body owns its session without a peer on purpose, and its own time ends it.
    public void ReleaseOrphanedSessions()
    {
        var now = DateTime.UtcNow;
        if (now < nextOrphanSweepUtc)
        {
            return;
        }

        nextOrphanSweepUtc = now + TimeSpan.FromSeconds(1);
        var boundSessionIds = peers.Contexts
            .Select(context => context.Session?.SimulationSessionId)
            .OfType<Guid>()
            .ToHashSet();
        var orphanThreshold = config.JoinHandshakeTimeout * 2;
        var activeSessionIds = new HashSet<Guid>();
        foreach (var session in sessionStore.ListActiveSessions())
        {
            activeSessionIds.Add(session.SimulationSessionId);
            if (boundSessionIds.Contains(session.SimulationSessionId) || bodies.Holds(session))
            {
                unboundSessionsSinceUtc.Remove(session.SimulationSessionId);
                continue;
            }

            if (!unboundSessionsSinceUtc.TryGetValue(session.SimulationSessionId, out var since))
            {
                unboundSessionsSinceUtc[session.SimulationSessionId] = now;
                continue;
            }

            if (now - since < orphanThreshold)
            {
                continue;
            }

            unboundSessionsSinceUtc.Remove(session.SimulationSessionId);
            logger.LogWarning(
                "[SIMULATION] Releasing simulation session {SimulationSessionId} because no peer has owned it for {Seconds:F0} seconds.",
                session.SimulationSessionId,
                (now - since).TotalSeconds);
            sessionOperations.ReleaseDetachedInBackground(session);
        }

        foreach (var sessionId in unboundSessionsSinceUtc.Keys.ToArray())
        {
            if (!activeSessionIds.Contains(sessionId))
            {
                unboundSessionsSinceUtc.Remove(sessionId);
            }
        }
    }

    private bool IsCurrentSession(ActiveSimulationSession session)
    {
        return sessionStore.IsCurrent(session, DateTime.UtcNow);
    }
}
