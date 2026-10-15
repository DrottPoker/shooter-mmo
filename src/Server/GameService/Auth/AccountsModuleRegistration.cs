using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Mmorpg.GameService.Redis;

namespace Mmorpg.GameService.Auth;

// Accounts module composition: account and worker authentication, policies,
// login and registration rate limits, and Redis session acceleration.
public static class AccountsModuleRegistration
{
    public static IServiceCollection AddAccountsModule(
        this IServiceCollection services,
        AccountsModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton(options.RedisAcceleration);
        services.AddSingleton(options.SimulationWorkerCredentials);
        services.AddSingleton<RedisAccelerationMetrics>();
        services.AddSingleton<IAccountSessionCache, RedisAccountSessionCache>();
        services.AddSingleton<RedisAuthenticationRateLimiter>();
        services.AddScoped<AccountService>();
        services.AddScoped<SessionService>();
        services.AddHostedService<RedisAccelerationMetricsReporterService>();
        services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, AccountSessionAuthenticationHandler>(
                AuthenticationConstants.AccountSessionScheme,
                _ => { })
            .AddScheme<AuthenticationSchemeOptions, SimulationWorkerAuthenticationHandler>(
                AuthenticationConstants.SimulationWorkerScheme,
                _ => { });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthenticationConstants.AccountSessionPolicy, policy =>
            {
                policy.AddAuthenticationSchemes(AuthenticationConstants.AccountSessionScheme);
                policy.RequireAuthenticatedUser();
            });
            options.AddPolicy(AuthenticationConstants.SimulationWorkerPolicy, policy =>
            {
                policy.AddAuthenticationSchemes(AuthenticationConstants.SimulationWorkerScheme);
                policy.RequireAuthenticatedUser();
            });
        });
        services.AddRateLimiter(rateLimiterOptions =>
        {
            rateLimiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rateLimiterOptions.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var remaining)
                    ? remaining
                    : options.AuthenticationRateLimitWindow;
                context.HttpContext.Response.Headers.RetryAfter = Math.Max(
                        1,
                        (int)Math.Ceiling(retryAfter.TotalSeconds))
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);

                await Results.Problem(
                        statusCode: StatusCodes.Status429TooManyRequests,
                        title: "Too Many Requests",
                        detail: "Too many authentication attempts. Try again later.",
                        extensions: new Dictionary<string, object?>
                        {
                            ["code"] = "rate_limit_exceeded"
                        })
                    .ExecuteAsync(context.HttpContext);
            };

            AddAuthenticationRateLimitPolicy(rateLimiterOptions, "login", options);
            AddAuthenticationRateLimitPolicy(rateLimiterOptions, "register", options);
        });

        return services;
    }

    // Runs before the in-process rate limiter so login and registration share one
    // distributed window across GameService replicas.
    public static IApplicationBuilder UseDistributedAuthenticationRateLimiting(
        this IApplicationBuilder app)
    {
        return app.UseMiddleware<RedisAuthenticationRateLimitMiddleware>();
    }

    public static IEndpointRouteBuilder MapAccountsModule(this IEndpointRouteBuilder app)
    {
        return app.MapAccountEndpoints();
    }

    private static void AddAuthenticationRateLimitPolicy(
        RateLimiterOptions rateLimiterOptions,
        string policyName,
        AccountsModuleOptions options)
    {
        rateLimiterOptions.AddPolicy(policyName, httpContext =>
        {
            var remoteAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(
                $"{policyName}:{remoteAddress}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.AuthenticationPermitLimit,
                    Window = options.AuthenticationRateLimitWindow,
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
        });
    }
}
