using System.Collections.Concurrent;

namespace WorldServer.Sessions;

public sealed class ActivePlayerSessionStore
{
    private readonly ConcurrentDictionary<Guid, ActivePlayerSession> sessionsByCharacterId = new();

    public bool TryAdd(ActivePlayerSession session)
    {
        return sessionsByCharacterId.TryAdd(session.CharacterId, session);
    }

    public bool Remove(Guid characterId)
    {
        return sessionsByCharacterId.TryRemove(characterId, out _);
    }

    public IReadOnlyCollection<ActivePlayerSessionResponse> List()
    {
        return sessionsByCharacterId.Values
            .OrderBy(session => session.JoinedAt)
            .Select(ActivePlayerSessionResponse.FromSession)
            .ToArray();
    }
}

