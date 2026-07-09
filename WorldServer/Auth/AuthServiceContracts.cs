namespace WorldServer.Auth;

public sealed record ConsumeJoinTicketRequest(string Ticket);

public sealed record ConsumedJoinTicketResponse(
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string WorldId,
    DateTime ExpiresAt);

public sealed record AuthServiceErrorResponse(string Code, string Message);

public sealed record AuthServiceResult<T>(T? Value, WorldServerErrorResponse? Error, int StatusCode)
{
    public bool Succeeded => Error is null;

    public static AuthServiceResult<T> Success(T value)
    {
        return new AuthServiceResult<T>(value, null, StatusCodes.Status200OK);
    }

    public static AuthServiceResult<T> Failure(int statusCode, string code, string message)
    {
        return new AuthServiceResult<T>(default, new WorldServerErrorResponse(code, message), statusCode);
    }
}

public sealed record WorldServerErrorResponse(string Code, string Message);

