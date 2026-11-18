namespace Mmorpg.GameService.Placement.Development;

// A bot's ticket request names the realtime protocol version and simulation revision that the bot
// was built with, as a player's join request does, and a worker of other versions refuses it.
internal sealed record DevelopmentSimulationBotTicketRequest(
    Guid BotInstanceId,
    int BotIndex,
    string? ShardId,
    int? ProtocolVersion = null,
    string? SimulationRevision = null);

internal sealed record DevelopmentSimulationBotTicketResponse(
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string ShardId,
    string WorldId,
    SimulationEndpointResponse Endpoint,
    Guid JoinTicketId,
    byte[] TransportKey,
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
