using Microsoft.AspNetCore.RateLimiting;
using Mmorpg.GameService.Config;

namespace Mmorpg.GameService.Redis;

public sealed class RedisAuthenticationRateLimitMiddleware(
    RequestDelegate next,
    GameServiceConfig config)
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

        var permitLimit = config.AuthenticationPermitLimit;
        var window = config.AuthenticationRateLimitWindow;
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

    // Routing matches paths case-insensitively, so the policy comes from the matched
    // endpoint instead of a literal path comparison that a different casing could skip.
    public static string? ResolvePolicyName(HttpContext context)
    {
        var policyName = context.GetEndpoint()?.Metadata
            .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        return policyName is "login" or "register" ? policyName : null;
    }
}
