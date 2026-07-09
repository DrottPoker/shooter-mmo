namespace WorldServer.Sessions;

public sealed record ActivePlayerSessionResponse(
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string WorldId,
    DateTime JoinedAt)
{
    public static ActivePlayerSessionResponse FromSession(ActivePlayerSession session)
    {
        return new ActivePlayerSessionResponse(
            session.AccountId,
            session.CharacterId,
            session.CharacterName,
            session.WorldId,
            session.JoinedAt);
    }
}

