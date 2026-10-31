namespace Mmorpg.GameService.Placement.Development;

internal sealed record DevelopmentSimulationBotTicketRequest(
    Guid BotInstanceId,
    int BotIndex,
    string? ShardId);

internal sealed record DevelopmentSimulationBotTicketResponse(
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string ShardId,
    string WorldId,
    SimulationEndpointResponse Endpoint,
    string JoinTicket,
    DateTime ExpiresAt);

internal sealed record DevelopmentSimulationBotPlacement(
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

internal sealed record DevelopmentSimulationBotAuthoritySnapshot(
    int Identities,
    int PendingTickets,
    int ActiveSessions,
    int ReleasedOrExpiredSessions);

internal sealed record DevelopmentSimulationBotReservation(
    string WorkerId,
    string WorkerRuntimeId,
    int ActiveSessions,
    int PendingTickets);
