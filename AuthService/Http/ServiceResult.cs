namespace AuthService.Http;

public sealed record ServiceResult<T>(T? Value, ErrorResponse? Error, int StatusCode)
{
    public bool Succeeded => Error is null;

    public static ServiceResult<T> Ok(T value)
    {
        return new ServiceResult<T>(value, null, StatusCodes.Status200OK);
    }

    public static ServiceResult<T> BadRequest(string code, string message)
    {
        return Failure(StatusCodes.Status400BadRequest, code, message);
    }

    public static ServiceResult<T> Unauthorized(string code, string message)
    {
        return Failure(StatusCodes.Status401Unauthorized, code, message);
    }

    public static ServiceResult<T> Forbidden(string code, string message)
    {
        return Failure(StatusCodes.Status403Forbidden, code, message);
    }

    public static ServiceResult<T> NotFound(string code, string message)
    {
        return Failure(StatusCodes.Status404NotFound, code, message);
    }

    public static ServiceResult<T> Conflict(string code, string message)
    {
        return Failure(StatusCodes.Status409Conflict, code, message);
    }

    public static ServiceResult<T> UnprocessableEntity(string code, string message)
    {
        return Failure(StatusCodes.Status422UnprocessableEntity, code, message);
    }

    private static ServiceResult<T> Failure(int statusCode, string code, string message)
    {
        return new ServiceResult<T>(default, new ErrorResponse(code, message), statusCode);
    }
}
