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
    ILogger logger,
    GameServiceAvailability? availability = null,
    Func<Guid, bool>? holdsDurableWork = null)
{
    private readonly Dictionary<Guid, DateTime> unboundSessionsSinceUtc = [];

    // The sessions without a peer or a body whose durable combat work has not committed, since
    // GameService's available time when the sweep found them so.
    private readonly Dictionary<Guid, TimeSpan> waitingForWorkSinceAvailable = [];
    private DateTime nextOrphanSweepUtc = DateTime.MinValue;

    public void ClearUnboundSessions()
    {
        unboundSessionsSinceUtc.Clear();
        waitingForWorkSinceAvailable.Clear();
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

            var pendingDisconnect = context.PendingDisconnect;
            if (pendingDisconnect is not null
                && pendingDisconnect.IsDue(now, config.GracefulDisconnectMaximum))
            {
                directory.DisconnectPeer(peer, pendingDisconnect.Data);
                continue;
            }

            // A client that never acknowledges reliable packets fills LiteNetLib's send window of
            // 64 packets per channel and then grows the outgoing queues behind them without bound,
            // so it is disconnected without another reliable message. The count is those queues of
            // every reliable channel together, not an acknowledgement.
            var queuedReliablePackets = RealtimeReliableChannels.QueuedPackets(peer);
            if (queuedReliablePackets > config.Admission.MaxReliableQueuedPackets)
            {
                logger.LogWarning(
                    "[SIMULATION] Disconnecting UDP peer {PeerId} with {QueuedPackets} unacknowledged reliable packets.",
                    context.PeerId,
                    queuedReliablePackets);
                networkMetrics.RecordConnectionRejected();
                directory.DisconnectPeer(peer, null);
                continue;
            }

            if (pendingDisconnect is not null)
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
                    sender.SendAndDisconnect(
                        peer,
                        context,
                        RealtimeProtocol.EncodeServerDisconnect(
                            "session_reconnected",
                            "This character connected from another client."));
                    teardown.LeaveBody(context, activeSession);
                    continue;
                }

                var reason = sessionStore.TryTakeInvalidation(
                    activeSession.SimulationSessionId,
                    out var invalidation)
                    ? invalidation!
                    : new ActiveSimulationSessionInvalidation(
                        "session_revoked",
                        "The simulation session is no longer active.");
                sender.SendAndDisconnect(
                    peer,
                    context,
                    RealtimeProtocol.EncodeServerDisconnect(
                        reason.Code,
                        reason.Message));
                teardown.RemoveBoundEntity(context, activeSession, reason.Code);
                context.Session = null;
                peers.RefreshPopulationMetrics();
                continue;
            }

            // The account session ended while the session goes on: the client goes with the code
            // it got before, and the character stays in the world as a lingering body.
            if (sessionStore.TryGetAccountSessionEnd(activeSession, out var accountSessionEnd))
            {
                var (code, message) = GameServiceOutcome.AccountSessionEndDisconnect(accountSessionEnd);
                sender.SendAndDisconnect(peer, context, RealtimeProtocol.EncodeServerDisconnect(code, message));
                teardown.LeaveBody(context, activeSession);
            }
        }
    }

    // Safety net: a local session that no peer has owned for a while is released, so a
    // missed cleanup path can never keep renewing a lease for a player who is gone. A lingering
    // body owns its session without a peer on purpose, and its own time ends it. A session whose
    // body went while its spent arrows or kill credits had not committed belongs to the combat
    // authority, which releases it once they did; that work waits while GameService does not
    // answer, so the sweep counts such a session's time only while GameService answers, and
    // leaves it for the outage tolerance, as long as the worker itself goes on without GameService.
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
                waitingForWorkSinceAvailable.Remove(session.SimulationSessionId);
                continue;
            }

            if (availability is not null && holdsDurableWork?.Invoke(session.SimulationSessionId) == true)
            {
                unboundSessionsSinceUtc.Remove(session.SimulationSessionId);
                ReleaseIfWorkOutlastedRetryHorizon(availability, session);
                continue;
            }

            waitingForWorkSinceAvailable.Remove(session.SimulationSessionId);

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

        foreach (var sessionId in waitingForWorkSinceAvailable.Keys.ToArray())
        {
            if (!activeSessionIds.Contains(sessionId))
            {
                waitingForWorkSinceAvailable.Remove(sessionId);
            }
        }
    }

    // A session whose combat work stayed pending for the whole retry horizon of the time that
    // GameService answered is released as any orphan is: the work itself is given up then.
    private void ReleaseIfWorkOutlastedRetryHorizon(GameServiceAvailability gameService, ActiveSimulationSession session)
    {
        var availableTime = gameService.AvailableTime;
        if (!waitingForWorkSinceAvailable.TryGetValue(session.SimulationSessionId, out var since))
        {
            waitingForWorkSinceAvailable[session.SimulationSessionId] = availableTime;
            return;
        }

        if (availableTime - since < config.UncertainOperationRetryHorizon)
        {
            return;
        }

        waitingForWorkSinceAvailable.Remove(session.SimulationSessionId);
        logger.LogWarning(
            "[SIMULATION] Releasing simulation session {SimulationSessionId}, whose combat work did not commit while no peer owned it for {Seconds:F0} seconds in which GameService answered.",
            session.SimulationSessionId,
            (availableTime - since).TotalSeconds);
        sessionOperations.ReleaseDetachedInBackground(session);
    }

    private bool IsCurrentSession(ActiveSimulationSession session)
    {
        return sessionStore.IsCurrent(session);
    }
}
