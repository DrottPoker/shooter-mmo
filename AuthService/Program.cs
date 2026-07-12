using System.Threading.RateLimiting;
using AuthService.Auth;
using AuthService.Characters;
using AuthService.Database;
using AuthService.Worlds;
using Microsoft.AspNetCore.Authentication;
using Npgsql;
using ShooterMmo.Shared.Health;
using ShooterMmo.Shared.Http;
using ShooterMmo.Shared.Networking;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddSingleton(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Postgres connection string is not configured.");

    return NpgsqlDataSource.Create(connectionString);
});

builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<CharacterService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<WorldService>();
builder.Services.AddScoped<WorldSessionService>();
builder.Services.AddApiProblemDetails();
builder.Services
    .AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, AccountSessionAuthenticationHandler>(
        AuthenticationConstants.AccountSessionScheme,
        _ => { })
    .AddScheme<AuthenticationSchemeOptions, WorldServerAuthenticationHandler>(
        AuthenticationConstants.WorldServerScheme,
        _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthenticationConstants.AccountSessionPolicy, policy =>
    {
        policy.AddAuthenticationSchemes(AuthenticationConstants.AccountSessionScheme);
        policy.RequireAuthenticatedUser();
    });
    options.AddPolicy(AuthenticationConstants.WorldServerPolicy, policy =>
    {
        policy.AddAuthenticationSchemes(AuthenticationConstants.WorldServerScheme);
        policy.RequireAuthenticatedUser();
    });
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = builder.Configuration
            .GetValue("RateLimiting:Authentication:WindowSeconds", 60)
            .ToString();

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

    AddAuthenticationRateLimitPolicy(options, "login", builder.Configuration);
    AddAuthenticationRateLimitPolicy(options, "register", builder.Configuration);
});

var app = builder.Build();

app.UseApiPipeline();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Configuration.GetValue("Database:RunMigrationsOnStartup", true))
{
    var initializer = app.Services.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);
}

app.MapGet("/", () => Results.Redirect("/health"));

app.MapGet("/health", async (IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var timeout = TimeSpan.FromMilliseconds(
        configuration.GetValue("HealthChecks:TcpTimeoutMilliseconds", 1_000));

    var postgresEndpoint = PostgresConnectionString.ParseEndpoint(
        configuration.GetConnectionString("Postgres"));

    var redisEndpoint = RedisConnectionString.ParseEndpoint(
        configuration.GetConnectionString("Redis"));

    var dependencies = await Task.WhenAll(
        TcpHealthProbe.CheckAsync("postgres", postgresEndpoint, timeout, cancellationToken),
        TcpHealthProbe.CheckAsync("redis", redisEndpoint, timeout, cancellationToken));

    var status = dependencies.All(dependency => dependency.IsReachable)
        ? "healthy"
        : "degraded";

    return Results.Ok(new ServiceHealth(
        "AuthService",
        status,
        DateTimeOffset.UtcNow,
        dependencies));
});

app.MapAccountEndpoints();
app.MapCharacterEndpoints();
app.MapWorldEndpoints();

app.Run();

static void AddAuthenticationRateLimitPolicy(
    Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options,
    string policyName,
    IConfiguration configuration)
{
    options.AddPolicy(policyName, httpContext =>
    {
        var remoteAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{policyName}:{remoteAddress}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = configuration.GetValue("RateLimiting:Authentication:PermitLimit", 5),
                Window = TimeSpan.FromSeconds(
                    configuration.GetValue("RateLimiting:Authentication:WindowSeconds", 60)),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
}
