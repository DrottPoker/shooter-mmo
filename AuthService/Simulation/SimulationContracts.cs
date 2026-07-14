namespace AuthService.Simulation;

public sealed record ShardResponse(
    string Id,
    string DisplayName,
    string WorldId,
    string FleetId,
    string FleetDisplayName,
    string RegionCode,
    string RuleSet,
    bool IsOnline,
    int ActivePlayers,
    int Capacity);

public sealed record SimulationEndpointResponse(
    string WorkerId,
    string RuntimeId,
    string Host,
    int UdpPort,
    int ProtocolVersion,
    string SimulationRevision,
    string CollisionRevision);

public sealed record SimulationWorkerHeartbeatRequest(
    string? FleetId,
    string? NodeId,
    string? ShardId,
    string? RuntimeId,
    DateTime StartedAt,
    string? Host,
    int UdpPort,
    int MaxConnections,
    int ActiveConnections,
    int ProtocolVersion,
    string? SimulationRevision,
    string? CollisionRevision);

public sealed record SimulationWorkerHeartbeatResponse(
    string WorkerId,
    string RuntimeId,
    string FleetId,
    string NodeId,
    string ShardId,
    string WorldId,
    string Host,
    int UdpPort,
    int MaxConnections,
    int ActiveConnections,
    int ProtocolVersion,
    string SimulationRevision,
    string CollisionRevision,
    DateTime LastHeartbeatAt,
    DateTime OnlineUntil);

public sealed record SimulationWorkerOfflineRequest(string? RuntimeId);

public sealed record SimulationWorkerOfflineResponse(
    string WorkerId,
    string RuntimeId,
    DateTime OfflineAt);

public sealed record JoinShardRequest(Guid CharacterId);

public sealed record JoinShardResponse(
    ShardResponse Shard,
    SimulationEndpointResponse Endpoint,
    Guid CharacterId,
    string JoinTicket,
    DateTime ExpiresAt,
    bool IsReconnect);

public sealed record ConsumeSimulationJoinTicketRequest(
    string? Ticket,
    string? WorkerId,
    string? RuntimeId,
    string? ShardId);

public sealed record ConsumedSimulationJoinTicketResponse(
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string ShardId,
    string WorldId,
    string WorkerId,
    string WorkerRuntimeId,
    Guid SimulationSessionId,
    string SimulationSessionToken,
    DateTime SessionExpiresAt,
    bool IsReconnect);

public sealed record SimulationSessionCredentialRequest(
    string? WorkerRuntimeId,
    string? SessionToken);

public sealed record SimulationSessionLeaseResponse(
    Guid SimulationSessionId,
    Guid CharacterId,
    string ShardId,
    string WorkerId,
    string WorkerRuntimeId,
    DateTime ExpiresAt,
    bool Released);
