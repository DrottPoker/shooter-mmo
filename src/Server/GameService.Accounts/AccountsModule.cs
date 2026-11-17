using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Accounts.PasswordHashing;
using Mmorpg.GameService.Accounts.Persistence;
using Mmorpg.GameService.Accounts.Redis;
using Mmorpg.GameService.Accounts.Retention;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Modules;
using Mmorpg.GameService.Platform.Retention;

namespace Mmorpg.GameService.Accounts;

// Accounts: account and worker authentication, policies, login and registration rate
// limits, the password hashing lane, and Redis session acceleration.
public sealed class AccountsModule(AccountsModuleOptions options) : IGameServiceModule
{
    internal const string LoginPolicy = "login";
    internal const string RegisterPolicy = "register";
    internal const string AuthenticationRejectionDetail = "Too many authentication attempts. Try again later.";

    public string Name => "Accounts";

    public IReadOnlyList<DatabaseMigration> Migrations => AccountsSchemaMigrations.All;

    public IReadOnlyList<string> MeterNames { get; } =
        [PasswordHashingMetrics.MeterName, RedisAccelerationMetrics.MeterName];

    public void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(options);
        services.AddSingleton(options.RedisAcceleration);
        services.AddSingleton(options.PasswordHashing);
        services.AddSingleton(options.SimulationWorkerCredentials);
        services.AddSingleton<PasswordHashingMetrics>();
        services.AddSingleton<IPasswordHashAlgorithm, BCryptPasswordHashAlgorithm>();
        services.AddSingleton<PasswordHashingLane>();
        services.AddSingleton<RedisAccelerationMetrics>();
        services.AddSingleton<IAccountSessionCache, RedisAccountSessionCache>();
        services.AddSingleton<RedisAuthenticationRateLimiter>();
        services.AddSingleton<IAccountLocks, AccountLocks>();
        services.AddSingleton<IAccountSessionActivity, AccountSessionActivity>();
        services.AddSingleton<IRetentionTask, AccountSessionRetentionTask>();
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
        services.AddAuthorization(authorizationOptions =>
        {
            authorizationOptions.AddPolicy(AuthenticationConstants.AccountSessionPolicy, policy =>
            {
                policy.AddAuthenticationSchemes(AuthenticationConstants.AccountSessionScheme);
                policy.RequireAuthenticatedUser();
            });
            authorizationOptions.AddPolicy(AuthenticationConstants.SimulationWorkerPolicy, policy =>
            {
                policy.AddAuthenticationSchemes(AuthenticationConstants.SimulationWorkerScheme);
                policy.RequireAuthenticatedUser();
            });
        });
        services.AddRateLimiter(rateLimiterOptions => AddRateLimitPolicies(rateLimiterOptions, options));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAccountEndpoints();
    }

    // Runs before the in-process rate limiter so login and registration share one
    // distributed window across GameService replicas.
    public static void UseDistributedAuthenticationRateLimiting(IApplicationBuilder app)
    {
        app.UseMiddleware<RedisAuthenticationRateLimitMiddleware>();
    }

    // Login and registration count per client address, each in its own window; the account
    // routes count per account, as docs/SERVICE_FEATURES.md describes under Rate Limits.
    internal static void AddRateLimitPolicies(RateLimiterOptions rateLimiterOptions, AccountsModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(rateLimiterOptions);
        ArgumentNullException.ThrowIfNull(options);

        var authentication = new FixedWindowRateLimit(
            options.AuthenticationPermitLimit,
            options.AuthenticationRateLimitWindow);
        foreach (var policyName in new[] { LoginPolicy, RegisterPolicy })
        {
            rateLimiterOptions.AddPolicy(
                policyName,
                new FixedWindowRateLimitPolicy(
                    policyName,
                    authentication,
                    ClientAddress.PartitionKey,
                    AuthenticationRejectionDetail));
        }

        rateLimiterOptions.AddPolicy(
            AccountRateLimitPolicies.Action,
            new FixedWindowRateLimitPolicy(
                AccountRateLimitPolicies.Action,
                options.AccountActionsRateLimit,
                context => $"{RouteOf(context)}:{AccountOf(context)}",
                "Too many requests for this account. Try again later."));
        rateLimiterOptions.AddPolicy(
            AccountRateLimitPolicies.Read,
            new FixedWindowRateLimitPolicy(
                AccountRateLimitPolicies.Read,
                options.AccountReadsRateLimit,
                AccountOf,
                "Too many requests for this account. Try again later."));
    }

    // The limiter runs after authorization, so an account route always has its account. A
    // request without one would have been refused first; it counts by address all the same.
    private static string AccountOf(HttpContext context)
    {
        var accountId = context.User.FindFirst(AuthenticationConstants.AccountIdClaim)?.Value;
        return string.IsNullOrEmpty(accountId)
            ? "address:" + ClientAddress.PartitionKey(context)
            : "account:" + accountId;
    }

    private static string RouteOf(HttpContext context)
    {
        return context.GetEndpoint() is RouteEndpoint endpoint
            ? $"{context.Request.Method} {endpoint.RoutePattern.RawText}"
            : $"{context.Request.Method} {context.Request.Path}";
    }
}
