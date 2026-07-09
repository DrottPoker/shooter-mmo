namespace AuthService.Worlds;

public sealed record WorldResponse(
    string Id,
    string DisplayName,
    string Host,
    int UdpPort,
    string RuleSet,
    bool IsOnline);

public sealed record JoinWorldRequest(Guid CharacterId);

public sealed record JoinWorldResponse(
    WorldResponse World,
    Guid CharacterId,
    string JoinTicket,
    DateTime ExpiresAt);

public sealed record ConsumeJoinTicketRequest(string? Ticket);

public sealed record ConsumedJoinTicketResponse(
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string WorldId,
    DateTime ExpiresAt);
