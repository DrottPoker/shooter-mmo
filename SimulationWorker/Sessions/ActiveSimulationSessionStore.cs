using ShooterMmo.GameSimulation;

namespace SimulationWorker.Sessions;

public sealed class ActiveSimulationSessionStore
{
    // An invalidation is only read by a peer that still holds the session, which happens
    // on the next tick, so entries of detached sessions are dropped after this window.
    public static readonly TimeSpan InvalidationRetention = TimeSpan.FromMinutes(1);

    private readonly object syncRoot = new();
    private readonly Dictionary<Guid, ActiveSimulationSession> sessionsByCharacterId = [];
    private readonly Dictionary<Guid, StoredInvalidation> invalidationsBySimulationSessionId = [];
    private DateTime nextInvalidationPruneUtc = DateTime.MinValue;

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

    public ActiveSimulationSessionRegistration Register(ActiveSimulationSession session)
    {
        lock (syncRoot)
        {
            invalidationsBySimulationSessionId.Remove(session.SimulationSessionId);
            if (!sessionsByCharacterId.TryGetValue(session.CharacterId, out var existingSession))
            {
                sessionsByCharacterId.Add(session.CharacterId, session);
                return ActiveSimulationSessionRegistration.Joined;
            }

            if (existingSession.SimulationSessionId == session.SimulationSessionId)
            {
                sessionsByCharacterId[session.CharacterId] = session with
                {
                    JoinedAt = existingSession.JoinedAt,
                    IsReconnect = true
                };
                return ActiveSimulationSessionRegistration.Reconnected;
            }

            if (existingSession.SessionExpiresAt <= DateTime.UtcNow)
            {
                StoreInvalidation(existingSession.SimulationSessionId, SessionExpiredInvalidation());
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

    public bool IsCurrent(ActiveSimulationSession expectedSession, DateTime utcNow)
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

            if (current.SessionExpiresAt > utcNow)
            {
                return true;
            }

            sessionsByCharacterId.Remove(expectedSession.CharacterId);
            StoreInvalidation(current.SimulationSessionId, SessionExpiredInvalidation());
            return false;
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

    public bool Refresh(
        Guid characterId,
        Guid simulationSessionId,
        string simulationSessionToken,
        DateTime sessionExpiresAt)
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

            sessionsByCharacterId[characterId] = session with { SessionExpiresAt = sessionExpiresAt };
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
            if (!sessionsByCharacterId.TryGetValue(characterId, out var session)
                || session.SimulationSessionId != simulationSessionId
                || !string.Equals(
                    session.SimulationSessionToken,
                    simulationSessionToken,
                    StringComparison.Ordinal))
            {
                return false;
            }

            sessionsByCharacterId.Remove(characterId);
            StoreInvalidation(
                simulationSessionId,
                new ActiveSimulationSessionInvalidation(code, message));
            return true;
        }
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
}

public enum ActiveSimulationSessionRegistration
{
    Joined,
    Reconnected,
    ReplacedExpired,
    Conflict
}

public sealed record ActiveSimulationSessionInvalidation(string Code, string Message);
