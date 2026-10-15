namespace Mmorpg.GameService.Placement;

internal sealed record ShardResponse(
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

internal sealed record SimulationEndpointResponse(
    string WorkerId,
    string RuntimeId,
    string Host,
    int UdpPort,
    int ProtocolVersion,
    string SimulationRevision,
    string CollisionRevision);

internal sealed record SimulationWorkerHeartbeatRequest(
    string? FleetId,
    string? NodeId,
    string? ShardId,
    string? WorldId,
    string? RuntimeId,
    DateTime StartedAt,
    string? Host,
    int UdpPort,
    int MaxConnections,
    int ActiveConnections,
    int ProtocolVersion,
    string? SimulationRevision,
    string? CollisionRevision);

internal sealed record SimulationWorkerHeartbeatResponse(
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

internal sealed record SimulationWorkerOfflineRequest(string? RuntimeId);

internal sealed record SimulationWorkerOfflineResponse(
    string WorkerId,
    string RuntimeId,
    DateTime OfflineAt);

internal sealed record JoinShardRequest(Guid CharacterId);

internal sealed record JoinShardResponse(
    ShardResponse Shard,
    SimulationEndpointResponse Endpoint,
    Guid CharacterId,
    string JoinTicket,
    DateTime ExpiresAt,
    bool IsReconnect);

internal sealed record ConsumeSimulationJoinTicketRequest(
    string? Ticket,
    string? WorkerId,
    string? RuntimeId,
    string? ShardId);

internal sealed record ConsumedSimulationJoinTicketResponse(
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
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity,
    bool IsReconnect)
{
    public bool IsSyntheticBot { get; init; }

    // The lease duration lets the worker set a deadline on its own clock, so clock
    // differences between the worker and the database never move the deadline.
    public long SessionLeaseDurationMilliseconds { get; init; }
}

internal sealed record SimulationSessionCredentialRequest(
    string? WorkerRuntimeId,
    string? SessionToken);

internal sealed record SimulationSessionLeaseResponse(
    Guid SimulationSessionId,
    Guid CharacterId,
    string ShardId,
    string WorkerId,
    string WorkerRuntimeId,
    DateTime ExpiresAt,
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity,
    bool Released)
{
    // The lease duration lets the worker set a deadline on its own clock, so clock
    // differences between the worker and the database never move the deadline.
    public long LeaseDurationMilliseconds { get; init; }
}
