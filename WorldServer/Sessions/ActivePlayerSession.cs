using WorldServer.Auth;

namespace WorldServer.Sessions;

public sealed record ActivePlayerSession(
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string WorldId,
    DateTime JoinedAt)
{
    public static ActivePlayerSession FromJoinTicket(ConsumedJoinTicketResponse ticket)
    {
        return new ActivePlayerSession(
            ticket.AccountId,
            ticket.CharacterId,
            ticket.CharacterName,
            ticket.WorldId,
            DateTime.UtcNow);
    }
}

