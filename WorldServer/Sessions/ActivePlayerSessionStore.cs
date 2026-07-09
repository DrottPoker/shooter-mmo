namespace WorldServer.Sessions;

public sealed class ActivePlayerSessionStore
{
    private readonly object syncRoot = new();
    private readonly Dictionary<Guid, ActivePlayerSession> sessionsByCharacterId = [];

    public ActivePlayerSessionRegistration Register(ActivePlayerSession session)
    {
        lock (syncRoot)
        {
            if (!sessionsByCharacterId.TryGetValue(session.CharacterId, out var existingSession))
            {
                sessionsByCharacterId.Add(session.CharacterId, session);
                return ActivePlayerSessionRegistration.Joined;
            }

            if (existingSession.WorldSessionId == session.WorldSessionId)
            {
                sessionsByCharacterId[session.CharacterId] = session with
                {
                    JoinedAt = existingSession.JoinedAt,
                    IsReconnect = true
                };
                return ActivePlayerSessionRegistration.Reconnected;
            }

            if (existingSession.SessionExpiresAt <= DateTime.UtcNow)
            {
                sessionsByCharacterId[session.CharacterId] = session;
                return ActivePlayerSessionRegistration.ReplacedExpired;
            }

            return ActivePlayerSessionRegistration.Conflict;
        }
    }

    public bool TryGet(Guid characterId, out ActivePlayerSession? session)
    {
        lock (syncRoot)
        {
            return sessionsByCharacterId.TryGetValue(characterId, out session);
        }
    }

    public bool Refresh(
        Guid characterId,
        Guid worldSessionId,
        string worldSessionToken,
        DateTime sessionExpiresAt)
    {
        lock (syncRoot)
        {
            if (!sessionsByCharacterId.TryGetValue(characterId, out var session)
                || session.WorldSessionId != worldSessionId
                || !string.Equals(
                    session.WorldSessionToken,
                    worldSessionToken,
                    StringComparison.Ordinal))
            {
                return false;
            }

            sessionsByCharacterId[characterId] = session with { SessionExpiresAt = sessionExpiresAt };
            return true;
        }
    }

    public bool Remove(Guid characterId, Guid worldSessionId, string worldSessionToken)
    {
        lock (syncRoot)
        {
            if (!sessionsByCharacterId.TryGetValue(characterId, out var session)
                || session.WorldSessionId != worldSessionId
                || !string.Equals(
                    session.WorldSessionToken,
                    worldSessionToken,
                    StringComparison.Ordinal))
            {
                return false;
            }

            return sessionsByCharacterId.Remove(characterId);
        }
    }

    public IReadOnlyCollection<ActivePlayerSession> ListActiveSessions()
    {
        lock (syncRoot)
        {
            return sessionsByCharacterId.Values.ToArray();
        }
    }

    public IReadOnlyCollection<ActivePlayerSessionResponse> List()
    {
        lock (syncRoot)
        {
            return sessionsByCharacterId.Values
                .OrderBy(session => session.JoinedAt)
                .Select(ActivePlayerSessionResponse.FromSession)
                .ToArray();
        }
    }
}

public enum ActivePlayerSessionRegistration
{
    Joined,
    Reconnected,
    ReplacedExpired,
    Conflict
}
