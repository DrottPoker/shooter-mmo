using System.Threading.RateLimiting;
using AuthService.Auth;
using AuthService.Characters;
using AuthService.Config;
using AuthService.Database;
using AuthService.Health;
using AuthService.Http;
using AuthService.Simulation;
using Microsoft.AspNetCore.Authentication;
using Npgsql;
using ShooterMmo.Shared.Configuration;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration
    .AddJsonFile(
        Path.Combine("Config", "appsettings.json"),
        optional: false,
        reloadOnChange: true)
    .AddJsonFile(
        Path.Combine(
            "Config",
            $"appsettings.{builder.Environment.EnvironmentName}.json"),
        optional: true,
        reloadOnChange: true);
builder.Configuration.AddOptionalDotEnvFile(Directory.GetCurrentDirectory());
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var config = AuthServiceConfig.FromConfiguration(builder.Configuration);
var developmentSimulationBotOptions = DevelopmentSimulationBotOptions.FromConfiguration(
    builder.Configuration,
    builder.Environment.IsDevelopment());

builder.Services.AddSingleton(_ =>
{
    return NpgsqlDataSource.Create(config.PostgresConnectionString);
});

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(developmentSimulationBotOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DevelopmentSimulationBotAuthority>();
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<SimulationTopologySeeder>();
builder.Services.AddSingleton<PostgresHealthProbe>();
builder.Services.AddSingleton<AuthServiceHealthService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<CharacterService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<ShardService>();
builder.Services.AddScoped<SimulationSessionService>();
builder.Services.AddScoped<SimulationWorkerRegistryService>();
builder.Services.AddScoped<DevelopmentSimulationBotPlacementService>();
builder.Services.AddScoped<DevelopmentSimulationBotTicketService>();
builder.Services.AddApiProblemDetails();
builder.Services
    .AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, AccountSessionAuthenticationHandler>(
        AuthenticationConstants.AccountSessionScheme,
        _ => { })
    .AddScheme<AuthenticationSchemeOptions, SimulationWorkerAuthenticationHandler>(
        AuthenticationConstants.SimulationWorkerScheme,
        _ => { });
builder.Services.AddAuthorization(options =>
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
    var topologySeeder = app.Services.GetRequiredService<SimulationTopologySeeder>();
    await topologySeeder.SeedAsync(CancellationToken.None);
}

app.MapGet("/", () => Results.Redirect("/health/ready"));
app.MapGet("/health/live", () => Results.Ok(new
{
    service = "AuthService",
    status = "live",
    checkedAt = DateTimeOffset.UtcNow
}));
app.MapGet("/health/ready", async (
    AuthServiceHealthService healthService,
    CancellationToken cancellationToken) =>
{
    var health = await healthService.CheckReadinessAsync(cancellationToken);
    return Results.Json(
        health,
        statusCode: health.Status == "ready"
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable);
});

app.MapAccountEndpoints();
app.MapCharacterEndpoints();
app.MapSimulationEndpoints();
app.MapDevelopmentSimulationBotEndpoints(developmentSimulationBotOptions);

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
