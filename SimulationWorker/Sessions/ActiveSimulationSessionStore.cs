namespace SimulationWorker.Sessions;

public sealed class ActiveSimulationSessionStore
{
    private readonly object syncRoot = new();
    private readonly Dictionary<Guid, ActiveSimulationSession> sessionsByCharacterId = [];
    private readonly Dictionary<Guid, ActiveSimulationSessionInvalidation> invalidationsBySimulationSessionId = [];

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
                invalidationsBySimulationSessionId[existingSession.SimulationSessionId] =
                    SessionExpiredInvalidation();
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
            invalidationsBySimulationSessionId[current.SimulationSessionId] = SessionExpiredInvalidation();
            return false;
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
            invalidationsBySimulationSessionId[simulationSessionId] = new ActiveSimulationSessionInvalidation(
                code,
                message);
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

            invalidation = stored;
            return true;
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
}

public enum ActiveSimulationSessionRegistration
{
    Joined,
    Reconnected,
    ReplacedExpired,
    Conflict
}

public sealed record ActiveSimulationSessionInvalidation(string Code, string Message);
