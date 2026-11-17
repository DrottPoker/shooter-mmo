using Microsoft.AspNetCore.RateLimiting;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Accounts.Redis;

internal sealed class RedisAuthenticationRateLimitMiddleware(
    RequestDelegate next,
    AccountsModuleOptions options)
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

        var permitLimit = options.AuthenticationPermitLimit;
        var window = options.AuthenticationRateLimitWindow;
        var decision = await limiter.AcquireAsync(
            policyName,
            ClientAddress.PartitionKey(context),
            permitLimit,
            window,
            context.RequestAborted);
        if (!decision.Available || decision.Allowed)
        {
            await next(context);
            return;
        }

        await FixedWindowRateLimitPolicy.WriteRejectionAsync(
            context,
            decision.RetryAfter,
            AccountsModule.AuthenticationRejectionDetail);
    }

    // Routing matches paths case-insensitively, so the policy comes from the matched
    // endpoint instead of a literal path comparison that a different casing could skip.
    public static string? ResolvePolicyName(HttpContext context)
    {
        var policyName = context.GetEndpoint()?.Metadata
            .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        return policyName is AccountsModule.LoginPolicy or AccountsModule.RegisterPolicy ? policyName : null;
    }
}
