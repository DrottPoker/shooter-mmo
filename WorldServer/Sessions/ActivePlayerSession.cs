using WorldServer.Auth;

namespace WorldServer.Sessions;

public sealed record ActivePlayerSession(
    Guid WorldSessionId,
    string WorldSessionToken,
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string WorldId,
    DateTime JoinedAt,
    DateTime SessionExpiresAt,
    bool IsReconnect)
{
    public static ActivePlayerSession FromJoinTicket(ConsumedJoinTicketResponse ticket)
    {
        return new ActivePlayerSession(
            ticket.WorldSessionId,
            ticket.WorldSessionToken,
            ticket.AccountId,
            ticket.CharacterId,
            ticket.CharacterName,
            ticket.WorldId,
            DateTime.UtcNow,
            ticket.SessionExpiresAt,
            ticket.IsReconnect);
    }
}
