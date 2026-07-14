namespace ShooterMmo.Tools.SimulationStressGenerator;

public sealed record StressWorkerHeartbeatRequest(
    string FleetId,
    string NodeId,
    string ShardId,
    string RuntimeId,
    DateTime StartedAt,
    string Host,
    int UdpPort,
    int MaxConnections,
    int ActiveConnections,
    int ProtocolVersion,
    string SimulationRevision,
    string CollisionRevision);

public sealed record StressWorkerHeartbeatResponse(
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

public sealed record StressWorkerOfflineRequest(string RuntimeId);

public sealed record StressWorkerOfflineResponse(
    string WorkerId,
    string RuntimeId,
    DateTime OfflineAt);

public sealed record StressConsumeTicketRequest(
    string Ticket,
    string WorkerId,
    string RuntimeId,
    string ShardId);

public sealed record StressConsumedTicketResponse(
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
    bool IsReconnect)
{
    public bool IsSyntheticBot { get; init; }
}

public sealed record StressSessionCredentialRequest(
    string WorkerRuntimeId,
    string SessionToken);

public sealed record StressSessionLeaseResponse(
    Guid SimulationSessionId,
    Guid CharacterId,
    string ShardId,
    string WorkerId,
    string WorkerRuntimeId,
    DateTime ExpiresAt,
    bool Released);

public sealed record StressProblemDetails(
    string Type,
    string Title,
    int Status,
    string Code,
    string Detail);

public sealed record StressIssuedTicket(
    int BotIndex,
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string Ticket,
    DateTime ExpiresAt);

public sealed record StressWorkerRegistration(
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
    DateTime StartedAt,
    DateTime LastHeartbeatAt,
    DateTime OnlineUntil);

public sealed record StressAuthorityCounters(
    long WorkerHeartbeats,
    long TicketsIssued,
    long TicketsConsumed,
    long TicketRejections,
    long SessionHeartbeats,
    long SessionReleases,
    int PendingTickets,
    int ActiveSessions);

public sealed record StressAuthorityResult<T>(T? Value, StressProblemDetails? Problem)
{
    public bool Succeeded => Problem is null;

    public static StressAuthorityResult<T> Success(T value)
    {
        return new StressAuthorityResult<T>(value, null);
    }

    public static StressAuthorityResult<T> Failure(
        int status,
        string code,
        string detail)
    {
        var title = status switch
        {
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            409 => "Conflict",
            _ => "Stress Authority Error"
        };
        return new StressAuthorityResult<T>(
            default,
            new StressProblemDetails(
                $"https://shooter-mmo.local/problems/{code}",
                title,
                status,
                code,
                detail));
    }
}
