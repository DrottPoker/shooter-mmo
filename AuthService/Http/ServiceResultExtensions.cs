namespace AuthService.Http;

using Microsoft.AspNetCore.WebUtilities;

public static class ServiceResultExtensions
{
    public static IResult ToHttpResult<T>(this ServiceResult<T> result)
    {
        if (result.Succeeded)
        {
            return Results.Ok(result.Value);
        }

        return Results.Problem(
            statusCode: result.StatusCode,
            title: ReasonPhrases.GetReasonPhrase(result.StatusCode),
            detail: result.Error!.Message,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = result.Error.Code
            });
    }
}
