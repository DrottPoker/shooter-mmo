namespace AuthService.Worlds;

public sealed record WorldResponse(
    string Id,
    string DisplayName,
    string Host,
    int UdpPort,
    string RuleSet,
    bool IsOnline,
    DateTime? LastHeartbeatAt,
    DateTime? OnlineUntil);

public sealed record WorldHeartbeatResponse(
    string WorldId,
    DateTime LastHeartbeatAt,
    DateTime OnlineUntil);

public sealed record JoinWorldRequest(Guid CharacterId);

public sealed record JoinWorldResponse(
    WorldResponse World,
    Guid CharacterId,
    string JoinTicket,
    DateTime ExpiresAt,
    bool IsReconnect);

public sealed record ConsumeJoinTicketRequest(string? Ticket, string? WorldId);

public sealed record ConsumedJoinTicketResponse(
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string WorldId,
    Guid WorldSessionId,
    string WorldSessionToken,
    DateTime SessionExpiresAt,
    bool IsReconnect);

public sealed record WorldSessionCredentialRequest(string? WorldId, string? SessionToken);

public sealed record WorldSessionLeaseResponse(
    Guid WorldSessionId,
    Guid CharacterId,
    string WorldId,
    DateTime ExpiresAt,
    bool Released);
