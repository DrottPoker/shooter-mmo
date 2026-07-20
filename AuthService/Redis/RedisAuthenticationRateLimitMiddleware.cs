namespace AuthService.Redis;

public sealed class RedisAuthenticationRateLimitMiddleware(
    RequestDelegate next,
    IConfiguration configuration)
{
    public async Task InvokeAsync(
        HttpContext context,
        RedisAuthenticationRateLimiter limiter)
    {
        var policyName = ResolvePolicyName(context);
        if (policyName is null)
        {
            await next(context);
            return;
        }

        var permitLimit = configuration.GetValue(
            "RateLimiting:Authentication:PermitLimit",
            5);
        var window = TimeSpan.FromSeconds(configuration.GetValue(
            "RateLimiting:Authentication:WindowSeconds",
            60));
        var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var decision = await limiter.AcquireAsync(
            policyName,
            remoteAddress,
            permitLimit,
            window,
            context.RequestAborted);
        if (!decision.Available || decision.Allowed)
        {
            await next(context);
            return;
        }

        context.Response.Headers.RetryAfter = Math.Max(
                1,
                (int)Math.Ceiling(decision.RetryAfter.TotalSeconds))
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too Many Requests",
                detail: "Too many authentication attempts. Try again later.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "rate_limit_exceeded"
                })
            .ExecuteAsync(context);
    }

    private static string? ResolvePolicyName(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return null;
        }

        return context.Request.Path.Value switch
        {
            "/api/accounts/login" => "login",
            "/api/accounts/register" => "register",
            _ => null
        };
    }
}
