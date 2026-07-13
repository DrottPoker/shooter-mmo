using System.Net;

namespace WorldServer.Auth;

public sealed record ConsumeJoinTicketRequest(string Ticket, string WorldId);

public sealed record ConsumedJoinTicketResponse(
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string WorldId,
    Guid WorldSessionId,
    string WorldSessionToken,
    DateTime SessionExpiresAt,
    bool IsReconnect);

public sealed record WorldSessionCredentialRequest(string WorldId, string SessionToken);

public sealed record WorldSessionLeaseResponse(
    Guid WorldSessionId,
    Guid CharacterId,
    string WorldId,
    DateTime ExpiresAt,
    bool Released);

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
    string Host,
    int UdpPort,
    string InstanceId,
    int ProtocolVersion,
    string SimulationRevision,
    string CollisionRevision);

public sealed record WorldOfflineRequest(string InstanceId);

public sealed record WorldOfflineResponse(
    string WorldId,
    string InstanceId,
    DateTime OfflineAt);

public sealed record AuthServiceProblemDetails(string? Code, string? Detail, string? Message);

public sealed record AuthServiceResult<T>(T? Value, WorldServerErrorResponse? Error, int StatusCode)
{
    public bool Succeeded => Error is null;

    public static AuthServiceResult<T> Success(T value)
    {
        return new AuthServiceResult<T>(value, null, (int)HttpStatusCode.OK);
    }

    public static AuthServiceResult<T> Failure(int statusCode, string code, string message)
    {
        return new AuthServiceResult<T>(default, new WorldServerErrorResponse(code, message), statusCode);
    }
}

public sealed record WorldServerErrorResponse(string Code, string Message);
