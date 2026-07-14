namespace AuthService.Simulation;

public sealed record DevelopmentSimulationBotTicketRequest(
    Guid BotInstanceId,
    int BotIndex,
    string? ShardId);

public sealed record DevelopmentSimulationBotTicketResponse(
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string ShardId,
    string WorldId,
    SimulationEndpointResponse Endpoint,
    string JoinTicket,
    DateTime ExpiresAt);

public sealed record DevelopmentSimulationBotPlacement(
    string ShardId,
    string WorldId,
    string WorkerId,
    string RuntimeId,
    string Host,
    int UdpPort,
    int MaxConnections,
    int ReportedActiveConnections,
    int ActiveDatabaseSessions,
    int PendingDatabaseTickets,
    int ProtocolVersion,
    string SimulationRevision,
    string CollisionRevision);

public sealed record DevelopmentSimulationBotAuthoritySnapshot(
    int Identities,
    int PendingTickets,
    int ActiveSessions,
    int ReleasedOrExpiredSessions);
