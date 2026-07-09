namespace AuthService.Http;

public static class ServiceResultExtensions
{
    public static IResult ToHttpResult<T>(this ServiceResult<T> result)
    {
        if (result.Succeeded)
        {
            return Results.Ok(result.Value);
        }

        return Results.Json(result.Error, statusCode: result.StatusCode);
    }
}

