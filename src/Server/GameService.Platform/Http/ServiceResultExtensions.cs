namespace Mmorpg.GameService.Platform.Http;

using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;

public static class ServiceResultExtensions
{
    public static IResult ToHttpResult<T>(this ServiceResult<T> result)
    {
        if (result.Succeeded)
        {
            return Results.Ok(result.Value);
        }

        var problem = Results.Problem(
            statusCode: result.StatusCode,
            title: ReasonPhrases.GetReasonPhrase(result.StatusCode),
            detail: result.Error!.Message,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = result.Error.Code
            });
        return result.RetryAfter is { } retryAfter
            ? new RetryAfterResult(problem, retryAfter)
            : problem;
    }

    // Whole seconds, at least one, as RFC 9110 delay-seconds.
    public static string FormatRetryAfter(TimeSpan retryAfter)
    {
        return Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
    }

    private sealed class RetryAfterResult(IResult inner, TimeSpan retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = FormatRetryAfter(retryAfter);
            return inner.ExecuteAsync(httpContext);
        }
    }
}
