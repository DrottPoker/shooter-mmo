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
    string Host,
    int UdpPort,
    string InstanceId,
    int ProtocolVersion,
    string SimulationRevision,
    string CollisionRevision,
    DateTime LastHeartbeatAt,
    DateTime OnlineUntil);

public sealed record WorldHeartbeatRequest(
    string? Host,
    int UdpPort,
    string? InstanceId,
    int ProtocolVersion,
    string? SimulationRevision,
    string? CollisionRevision);

public sealed record WorldOfflineRequest(string? InstanceId);

public sealed record WorldOfflineResponse(
    string WorldId,
    string InstanceId,
    DateTime OfflineAt);

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
