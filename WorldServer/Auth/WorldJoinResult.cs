using System.Net;

namespace WorldServer.Auth;

public sealed record WorldJoinResult<T>(T? Value, WorldServerErrorResponse? Error, int StatusCode)
{
    public bool Succeeded => Error is null;

    public static WorldJoinResult<T> Success(T value)
    {
        return new WorldJoinResult<T>(value, null, (int)HttpStatusCode.OK);
    }

    public static WorldJoinResult<T> BadRequest(string code, string message)
    {
        return Failure((int)HttpStatusCode.BadRequest, code, message);
    }

    public static WorldJoinResult<T> Conflict(string code, string message)
    {
        return Failure((int)HttpStatusCode.Conflict, code, message);
    }

    public static WorldJoinResult<T> Failure(int statusCode, string code, string message)
    {
        return new WorldJoinResult<T>(default, new WorldServerErrorResponse(code, message), statusCode);
    }

}
