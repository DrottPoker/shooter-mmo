using System.Net;

namespace SimulationWorker.Auth;

public sealed record ConsumeSimulationJoinTicketRequest(
    string Ticket,
    string WorkerId,
    string RuntimeId,
    string ShardId);

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
    bool IsReconnect)
{
    public bool IsSyntheticBot { get; init; }
}

public sealed record SimulationSessionCredentialRequest(
    string WorkerRuntimeId,
    string SessionToken);

public sealed record SimulationSessionLeaseResponse(
    Guid SimulationSessionId,
    Guid CharacterId,
    string ShardId,
    string WorkerId,
    string WorkerRuntimeId,
    DateTime ExpiresAt,
    bool Released);

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

public sealed record SimulationWorkerHeartbeatRequest(
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

public sealed record SimulationWorkerOfflineRequest(string RuntimeId);

public sealed record SimulationWorkerOfflineResponse(
    string WorkerId,
    string RuntimeId,
    DateTime OfflineAt);

public sealed record AuthServiceProblemDetails(string? Code, string? Detail, string? Message);

public sealed record AuthServiceResult<T>(T? Value, SimulationWorkerErrorResponse? Error, int StatusCode)
{
    public bool Succeeded => Error is null;

    public static AuthServiceResult<T> Success(T value)
    {
        return new AuthServiceResult<T>(value, null, (int)HttpStatusCode.OK);
    }

    public static AuthServiceResult<T> Failure(int statusCode, string code, string message)
    {
        return new AuthServiceResult<T>(
            default,
            new SimulationWorkerErrorResponse(code, message),
            statusCode);
    }
}

public sealed record SimulationWorkerErrorResponse(string Code, string Message);
