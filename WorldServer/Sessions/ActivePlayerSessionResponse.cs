namespace WorldServer.Sessions;

public sealed record ActivePlayerSessionResponse(
    Guid WorldSessionId,
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string WorldId,
    DateTime JoinedAt,
    DateTime SessionExpiresAt,
    bool IsReconnect)
{
    public static ActivePlayerSessionResponse FromSession(ActivePlayerSession session)
    {
        return new ActivePlayerSessionResponse(
            session.WorldSessionId,
            session.AccountId,
            session.CharacterId,
            session.CharacterName,
            session.WorldId,
            session.JoinedAt,
            session.SessionExpiresAt,
            session.IsReconnect);
    }
}
