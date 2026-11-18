using System.Diagnostics.CodeAnalysis;
using Mmorpg.GameSimulation;

namespace Mmorpg.SimulationWorker.Sessions;

// The sessions the worker holds, by character, with their leases. A lease counts from the send time of
// the request that was answered with it, on the monotonic clock of the time provider, so a step of
// the host's wall clock neither drops every session at once nor extends a lease beyond GameService's.
public sealed class ActiveSimulationSessionStore(TimeProvider? timeProvider = null, PresenceMetrics? metrics = null)
{
    // An invalidation is only read by a peer that still holds the session, which happens
    // on the next tick, so entries of detached sessions are dropped after this window.
    public static readonly TimeSpan InvalidationRetention = TimeSpan.FromMinutes(1);

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly object syncRoot = new();
    private readonly Dictionary<Guid, ActiveSimulationSession> sessionsByCharacterId = [];
    private readonly Dictionary<Guid, StoredInvalidation> invalidationsBySimulationSessionId = [];

    // The current sessions whose account session ended, with GameService's code for the end. Such
    // a session stays current: only its connection ends, and its body stays.
    private readonly Dictionary<Guid, string> accountSessionEndsBySimulationSessionId = [];
    private int accountSessionEndCount;
    private DateTime nextInvalidationPruneUtc = DateTime.MinValue;

    // The joins that consume a ticket now, by the order they began, and the refusals of a
    // session generation's credentials that wait for them.
    private readonly SortedSet<long> joinsInFlight = [];
    private readonly List<WaitingRefusal> waitingRefusals = [];
    private long lastJoin;

    public int PendingInvalidationCount
    {
        get
        {
            lock (syncRoot)
            {
                return invalidationsBySimulationSessionId.Count;
            }
        }
    }

    // A new generation of a session comes from a ticket of a valid account session, so it carries
    // no account session end. Two joins of one session may consume their tickets at GameService in
    // one order and register here in the other, since the work after the consumption differs, so
    // a generation that GameService issued before the one held is refused, and the held one stays
    // untouched.
    public ActiveSimulationSessionRegistration Register(ActiveSimulationSession session)
    {
        session = WithLease(session);
        lock (syncRoot)
        {
            if (sessionsByCharacterId.TryGetValue(session.CharacterId, out var held)
                && held.SimulationSessionId == session.SimulationSessionId
                && session.Generation > 0
                && session.Generation < held.Generation)
            {
                return ActiveSimulationSessionRegistration.StaleGeneration;
            }

            invalidationsBySimulationSessionId.Remove(session.SimulationSessionId);
            if (!sessionsByCharacterId.TryGetValue(session.CharacterId, out var existingSession))
            {
                sessionsByCharacterId.Add(session.CharacterId, session);
                return ActiveSimulationSessionRegistration.Joined;
            }

            if (existingSession.SimulationSessionId == session.SimulationSessionId)
            {
                ForgetAccountSessionEndLocked(session.SimulationSessionId);
                sessionsByCharacterId[session.CharacterId] = session with
                {
                    JoinedAt = existingSession.JoinedAt,
                    IsReconnect = true
                };
                return ActiveSimulationSessionRegistration.Reconnected;
            }

            if (IsLeaseOver(existingSession))
            {
                ForgetAccountSessionEndLocked(existingSession.SimulationSessionId);
                StoreInvalidation(existingSession.SimulationSessionId, SessionExpiredInvalidation());
                metrics?.RecordSessionEnded(PresenceMetrics.SessionLeaseExpired);
                sessionsByCharacterId[session.CharacterId] = session;
                return ActiveSimulationSessionRegistration.ReplacedExpired;
            }

            return ActiveSimulationSessionRegistration.Conflict;
        }
    }

    public bool TryGet(Guid characterId, out ActiveSimulationSession? session)
    {
        lock (syncRoot)
        {
            return sessionsByCharacterId.TryGetValue(characterId, out session);
        }
    }

    // The store's monotonic clock, from which a lease counts: whoever sends a request that renews a
    // lease takes the timestamp right before it sends.
    public long GetTimestamp()
    {
        return time.GetTimestamp();
    }

    // Whether the exact session generation is held and its lease has not run out. A lease that ran
    // out ends the session here: it is removed, and its invalidation says session_expired.
    public bool IsCurrent(ActiveSimulationSession expectedSession)
    {
        lock (syncRoot)
        {
            if (!sessionsByCharacterId.TryGetValue(expectedSession.CharacterId, out var current)
                || current.SimulationSessionId != expectedSession.SimulationSessionId
                || !string.Equals(
                    current.SimulationSessionToken,
                    expectedSession.SimulationSessionToken,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!IsLeaseOver(current))
            {
                return true;
            }

            sessionsByCharacterId.Remove(expectedSession.CharacterId);
            ForgetAccountSessionEndLocked(current.SimulationSessionId);
            StoreInvalidation(current.SimulationSessionId, SessionExpiredInvalidation());
            metrics?.RecordSessionEnded(PresenceMetrics.SessionLeaseExpired);
            return false;
        }
    }

    // Whether the session is its character's current generation, whatever its local lease says.
    public bool IsCurrentGeneration(ActiveSimulationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (syncRoot)
        {
            return IsCurrentGenerationLocked(session.CharacterId, session.SimulationSessionId, session.SimulationSessionToken);
        }
    }

    // Runs the action unless a newer generation of the session is registered, and says whether it
    // ran. The state that the worker keeps per character belongs to the session, not to one
    // generation of it, and a join that registers a newer generation fills it in right after it
    // registered. So whoever ends an entity of an older generation removes that state only through
    // here: the check and the action are one step against Register, which takes the same lock, and
    // the state that a newer generation stores stays. The action runs under the lock, so it may
    // take the locks of the per-character stores, which never call back into this store.
    public bool RunUnlessSuperseded(ActiveSimulationSession session, Action action)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(action);
        lock (syncRoot)
        {
            if (sessionsByCharacterId.TryGetValue(session.CharacterId, out var current)
                && current.SimulationSessionId == session.SimulationSessionId
                && !string.Equals(current.SimulationSessionToken, session.SimulationSessionToken, StringComparison.Ordinal))
            {
                return false;
            }

            action();
            return true;
        }
    }

    // Records that the account session of the current session generation ended, with GameService's
    // code for the end. The session stays current. True only when this call recorded it: false
    // when the generation is not the current one or its end was recorded already, whose code stays.
    public bool MarkAccountSessionEnded(
        Guid characterId,
        Guid simulationSessionId,
        string simulationSessionToken,
        string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        lock (syncRoot)
        {
            if (!IsCurrentGenerationLocked(characterId, simulationSessionId, simulationSessionToken))
            {
                return false;
            }

            if (!accountSessionEndsBySimulationSessionId.TryAdd(simulationSessionId, code))
            {
                return false;
            }

            Volatile.Write(ref accountSessionEndCount, accountSessionEndsBySimulationSessionId.Count);
            return true;
        }
    }

    // GameService's code for the end of the account session of the session's current generation,
    // which the loop reads for every connected peer, so it takes no lock while no account session
    // ended.
    public bool TryGetAccountSessionEnd(ActiveSimulationSession session, [NotNullWhen(true)] out string? code)
    {
        ArgumentNullException.ThrowIfNull(session);
        code = null;
        if (Volatile.Read(ref accountSessionEndCount) == 0)
        {
            return false;
        }

        lock (syncRoot)
        {
            return IsCurrentGenerationLocked(session.CharacterId, session.SimulationSessionId, session.SimulationSessionToken)
                && accountSessionEndsBySimulationSessionId.TryGetValue(session.SimulationSessionId, out code);
        }
    }

    // True when a reconnect re-registered the same session with a newer token, so the
    // older connection must detach without removing the shared entity or carry state.
    public bool IsSupersededByReconnect(ActiveSimulationSession session)
    {
        lock (syncRoot)
        {
            return sessionsByCharacterId.TryGetValue(session.CharacterId, out var current)
                && current.SimulationSessionId == session.SimulationSessionId
                && !string.Equals(
                    current.SimulationSessionToken,
                    session.SimulationSessionToken,
                    StringComparison.Ordinal);
        }
    }

    // Renews the lease of the exact generation: it counts from the timestamp that the request was
    // sent at, taken from GetTimestamp, and lasts the duration that GameService answered.
    public bool Refresh(
        Guid characterId,
        Guid simulationSessionId,
        string simulationSessionToken,
        long requestedTimestamp,
        TimeSpan leaseDuration)
    {
        lock (syncRoot)
        {
            if (!sessionsByCharacterId.TryGetValue(characterId, out var session)
                || session.SimulationSessionId != simulationSessionId
                || !string.Equals(
                    session.SimulationSessionToken,
                    simulationSessionToken,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var lease = new SessionLease(requestedTimestamp, leaseDuration);
            sessionsByCharacterId[characterId] = session with
            {
                Lease = lease,
                SessionExpiresAt = ReportedExpiry(lease)
            };
            return true;
        }
    }

    public bool RefreshCarryState(
        Guid characterId,
        Guid simulationSessionId,
        string simulationSessionToken,
        PlayerCarryState carryState)
    {
        ArgumentNullException.ThrowIfNull(carryState);
        lock (syncRoot)
        {
            if (!sessionsByCharacterId.TryGetValue(characterId, out var session)
                || session.SimulationSessionId != simulationSessionId
                || !string.Equals(
                    session.SimulationSessionToken,
                    simulationSessionToken,
                    StringComparison.Ordinal))
            {
                return false;
            }

            sessionsByCharacterId[characterId] = session with { CarryState = carryState };
            return true;
        }
    }

    public bool Remove(Guid characterId, Guid simulationSessionId, string simulationSessionToken)
    {
        lock (syncRoot)
        {
            if (!sessionsByCharacterId.TryGetValue(characterId, out var session)
                || session.SimulationSessionId != simulationSessionId
                || !string.Equals(
                    session.SimulationSessionToken,
                    simulationSessionToken,
                    StringComparison.Ordinal))
            {
                return false;
            }

            invalidationsBySimulationSessionId.Remove(simulationSessionId);
            ForgetAccountSessionEndLocked(simulationSessionId);
            return sessionsByCharacterId.Remove(characterId);
        }
    }

    public bool Invalidate(
        Guid characterId,
        Guid simulationSessionId,
        string simulationSessionToken,
        string code,
        string message)
    {
        lock (syncRoot)
        {
            return InvalidateLocked(characterId, simulationSessionId, simulationSessionToken, code, message);
        }
    }

    // A join consumes a ticket from now on until EndJoin. Consuming the ticket of a reconnect
    // rotates the token of its session at GameService before the join registers the new token
    // here, so GameService may refuse the old token of a session that goes on.
    public long BeginJoin()
    {
        lock (syncRoot)
        {
            var join = ++lastJoin;
            joinsInFlight.Add(join);
            return join;
        }
    }

    // The join registered its session or failed. A refused generation that waited only for joins
    // that ended now ends, unless one of them registered a newer generation of its session.
    public void EndJoin(long join)
    {
        List<WaitingRefusal>? ended = null;
        lock (syncRoot)
        {
            if (!joinsInFlight.Remove(join))
            {
                return;
            }

            var oldestInFlight = joinsInFlight.Count == 0 ? long.MaxValue : joinsInFlight.Min;
            for (var index = waitingRefusals.Count - 1; index >= 0; index--)
            {
                var refusal = waitingRefusals[index];
                if (refusal.LastJoinBefore >= oldestInFlight)
                {
                    continue;
                }

                waitingRefusals.RemoveAt(index);
                if (InvalidateLocked(
                        refusal.CharacterId,
                        refusal.SimulationSessionId,
                        refusal.SimulationSessionToken,
                        refusal.Code,
                        refusal.Message))
                {
                    (ended ??= []).Add(refusal);
                }
            }
        }

        if (ended is not null)
        {
            foreach (var refusal in ended)
            {
                refusal.Ended?.Invoke();
            }
        }
    }

    // GameService refused the credentials of the session generation: it ended the session, or a
    // reconnect consumed its ticket, which rotated the token there before the reconnect's join
    // registers the new token here. Only a join of this worker consumes a ticket of a session that
    // it holds, so the generation ends at once while no join is in flight, and otherwise once
    // every join that was in flight has ended, unless one of them registered a newer generation
    // of the session, which goes on. Ended runs when the generation ends this way, outside the
    // store's lock.
    public SessionCredentialsRefusal RefuseCredentials(
        Guid characterId,
        Guid simulationSessionId,
        string simulationSessionToken,
        string code,
        string message,
        Action? ended = null)
    {
        lock (syncRoot)
        {
            if (!IsCurrentGenerationLocked(characterId, simulationSessionId, simulationSessionToken))
            {
                return SessionCredentialsRefusal.EarlierGeneration;
            }

            if (joinsInFlight.Count > 0)
            {
                waitingRefusals.Add(new WaitingRefusal(
                    characterId,
                    simulationSessionId,
                    simulationSessionToken,
                    code,
                    message,
                    lastJoin,
                    ended));
                return SessionCredentialsRefusal.WaitsForJoins;
            }

            InvalidateLocked(characterId, simulationSessionId, simulationSessionToken, code, message);
        }

        ended?.Invoke();
        return SessionCredentialsRefusal.Ended;
    }

    private bool InvalidateLocked(
        Guid characterId,
        Guid simulationSessionId,
        string simulationSessionToken,
        string code,
        string message)
    {
        if (!IsCurrentGenerationLocked(characterId, simulationSessionId, simulationSessionToken))
        {
            return false;
        }

        sessionsByCharacterId.Remove(characterId);
        ForgetAccountSessionEndLocked(simulationSessionId);
        StoreInvalidation(
            simulationSessionId,
            new ActiveSimulationSessionInvalidation(code, message));
        metrics?.RecordSessionEnded(PresenceMetrics.SessionInvalidated);
        return true;
    }

    public bool TryTakeInvalidation(
        Guid simulationSessionId,
        out ActiveSimulationSessionInvalidation? invalidation)
    {
        lock (syncRoot)
        {
            if (!invalidationsBySimulationSessionId.Remove(simulationSessionId, out var stored))
            {
                invalidation = null;
                return false;
            }

            invalidation = stored.Invalidation;
            return true;
        }
    }

    public int PruneInvalidations(DateTime recordedBeforeUtc)
    {
        lock (syncRoot)
        {
            return PruneInvalidationsLocked(recordedBeforeUtc);
        }
    }

    public IReadOnlyCollection<ActiveSimulationSession> ListActiveSessions()
    {
        lock (syncRoot)
        {
            return sessionsByCharacterId.Values.ToArray();
        }
    }

    public IReadOnlyCollection<ActiveSimulationSessionResponse> List()
    {
        lock (syncRoot)
        {
            return sessionsByCharacterId.Values
                .OrderBy(session => session.JoinedAt)
                .Select(ActiveSimulationSessionResponse.FromSession)
                .ToArray();
        }
    }

    // Whether the time since the lease's request was sent reached its length.
    private bool IsLeaseOver(ActiveSimulationSession session)
    {
        return time.GetElapsedTime(session.Lease.StartedTimestamp) >= session.Lease.Duration;
    }

    // A session that arrives without a lease gets one from its wall clock expiry, measured once,
    // now. A session of a join or a renewal always has one.
    private ActiveSimulationSession WithLease(ActiveSimulationSession session)
    {
        if (session.Lease.IsSet)
        {
            return session;
        }

        var remaining = session.SessionExpiresAt - time.GetUtcNow().UtcDateTime;
        return session with
        {
            Lease = new SessionLease(time.GetTimestamp(), remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)
        };
    }

    // The wall clock time the lease ends at, as the client is told: now plus what remains.
    private DateTime ReportedExpiry(SessionLease lease)
    {
        return time.GetUtcNow().UtcDateTime + (lease.Duration - time.GetElapsedTime(lease.StartedTimestamp));
    }

    private bool IsCurrentGenerationLocked(Guid characterId, Guid simulationSessionId, string simulationSessionToken)
    {
        return sessionsByCharacterId.TryGetValue(characterId, out var current)
            && current.SimulationSessionId == simulationSessionId
            && string.Equals(current.SimulationSessionToken, simulationSessionToken, StringComparison.Ordinal);
    }

    private void ForgetAccountSessionEndLocked(Guid simulationSessionId)
    {
        if (accountSessionEndsBySimulationSessionId.Remove(simulationSessionId))
        {
            Volatile.Write(ref accountSessionEndCount, accountSessionEndsBySimulationSessionId.Count);
        }
    }

    private static ActiveSimulationSessionInvalidation SessionExpiredInvalidation()
    {
        return new ActiveSimulationSessionInvalidation(
            "session_expired",
            "The simulation session lease expired.");
    }

    private void StoreInvalidation(
        Guid simulationSessionId,
        ActiveSimulationSessionInvalidation invalidation)
    {
        var now = DateTime.UtcNow;
        if (now >= nextInvalidationPruneUtc)
        {
            PruneInvalidationsLocked(now - InvalidationRetention);
            nextInvalidationPruneUtc = now + TimeSpan.FromSeconds(1);
        }

        invalidationsBySimulationSessionId[simulationSessionId] = new StoredInvalidation(
            invalidation,
            now);
    }

    private int PruneInvalidationsLocked(DateTime recordedBeforeUtc)
    {
        var removed = 0;
        foreach (var pair in invalidationsBySimulationSessionId.ToArray())
        {
            if (pair.Value.RecordedAtUtc < recordedBeforeUtc)
            {
                invalidationsBySimulationSessionId.Remove(pair.Key);
                removed++;
            }
        }

        return removed;
    }

    private readonly record struct StoredInvalidation(
        ActiveSimulationSessionInvalidation Invalidation,
        DateTime RecordedAtUtc);

    // A refused generation that waits for the joins up to LastJoinBefore, the last join that
    // began before the refusal came.
    private sealed record WaitingRefusal(
        Guid CharacterId,
        Guid SimulationSessionId,
        string SimulationSessionToken,
        string Code,
        string Message,
        long LastJoinBefore,
        Action? Ended);
}

// What a refusal of a session generation's credentials did: the generation ended, it waits for
// the joins in flight, or a newer generation of the session already replaced it.
public enum SessionCredentialsRefusal
{
    Ended,
    WaitsForJoins,
    EarlierGeneration
}

public enum ActiveSimulationSessionRegistration
{
    Joined,
    Reconnected,
    ReplacedExpired,
    Conflict,

    // A newer generation of the same session is registered already: GameService issued this one's
    // token before it, so the join that holds it consumed its ticket first and lost the session.
    StaleGeneration
}

public sealed record ActiveSimulationSessionInvalidation(string Code, string Message);
