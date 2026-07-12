namespace WorldServer.Auth;

public sealed record WorldJoinResult<T>(T? Value, WorldServerErrorResponse? Error, int StatusCode)
{
    public bool Succeeded => Error is null;

    public static WorldJoinResult<T> Success(T value)
    {
        return new WorldJoinResult<T>(value, null, StatusCodes.Status200OK);
    }

    public static WorldJoinResult<T> BadRequest(string code, string message)
    {
        return Failure(StatusCodes.Status400BadRequest, code, message);
    }

    public static WorldJoinResult<T> Conflict(string code, string message)
    {
        return Failure(StatusCodes.Status409Conflict, code, message);
    }

    public static WorldJoinResult<T> Failure(int statusCode, string code, string message)
    {
        return new WorldJoinResult<T>(default, new WorldServerErrorResponse(code, message), statusCode);
    }

    public IResult ToHttpResult()
    {
        return Succeeded
            ? Results.Ok(Value)
            : Results.Problem(
                statusCode: StatusCode,
                title: "World join failed",
                detail: Error!.Message,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = Error.Code
                });
    }
}
