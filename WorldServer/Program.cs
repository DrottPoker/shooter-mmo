using ShooterMmo.Shared.Health;
using ShooterMmo.Shared.Networking;
using WorldServer.Auth;
using WorldServer.Config;
using WorldServer.Sessions;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: true,
    reloadOnChange: false);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var config = WorldServerConfig.FromConfiguration(builder.Configuration);

builder.WebHost.UseUrls(config.HttpUrl);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<ActivePlayerSessionStore>();
builder.Services.AddSingleton(_ => new HttpClient
{
    BaseAddress = config.AuthServiceBaseUrl,
    Timeout = config.AuthServiceTimeout
});
builder.Services.AddSingleton<AuthServiceClient>();
builder.Services.AddSingleton<WorldJoinService>();
builder.Services.AddHostedService<WorldSessionHeartbeatService>();

var app = builder.Build();

var redisHealth = await TcpHealthProbe.CheckAsync(
    "redis",
    RedisConnectionString.ParseEndpoint(config.RedisConnectionString),
    config.HealthCheckTimeout,
    CancellationToken.None);

Console.WriteLine($"Starting {config.WorldServerId} on UDP port {config.UdpPort}.");
Console.WriteLine(redisHealth.IsReachable
    ? $"Redis reachable at {redisHealth.Target}."
    : $"Redis unreachable at {redisHealth.Target}: {redisHealth.Error}");

if (args.Contains("--health-check-only", StringComparer.OrdinalIgnoreCase))
{
    return;
}

app.MapGet("/", () => Results.Redirect("/health"));

app.MapGet("/health", async (
    AuthServiceClient authServiceClient,
    CancellationToken cancellationToken) =>
{
    var dependencies = new[]
    {
        await TcpHealthProbe.CheckAsync(
            "redis",
            RedisConnectionString.ParseEndpoint(config.RedisConnectionString),
            config.HealthCheckTimeout,
            cancellationToken),
        await authServiceClient.CheckHealthAsync(cancellationToken)
    };

    var status = dependencies.All(dependency => dependency.IsReachable)
        ? "healthy"
        : "degraded";

    return Results.Ok(new ServiceHealth(
        "WorldServer",
        status,
        DateTimeOffset.UtcNow,
        dependencies));
});

app.MapPost("/debug/join", async (
    DebugJoinRequest request,
    WorldJoinService joinService,
    CancellationToken cancellationToken) =>
{
    var result = await joinService.JoinAsync(request, cancellationToken);
    return result.ToHttpResult();
});

app.MapGet("/debug/sessions", (ActivePlayerSessionStore sessionStore) =>
{
    return Results.Ok(sessionStore.List());
});

app.MapDelete("/debug/sessions/{characterId:guid}", async (
    Guid characterId,
    ActivePlayerSessionStore sessionStore,
    AuthServiceClient authServiceClient,
    CancellationToken cancellationToken) =>
{
    if (!sessionStore.TryGet(characterId, out var session))
    {
        return Results.NoContent();
    }

    var release = await authServiceClient.ReleaseWorldSessionAsync(
        session!.WorldSessionId,
        session.WorldId,
        session.WorldSessionToken,
        cancellationToken);

    if (!release.Succeeded)
    {
        if (release.StatusCode is StatusCodes.Status401Unauthorized
            or StatusCodes.Status404NotFound
            or StatusCodes.Status409Conflict)
        {
            sessionStore.Remove(characterId, session.WorldSessionId, session.WorldSessionToken);
            return Results.NoContent();
        }

        return Results.Json(release.Error, statusCode: release.StatusCode);
    }

    sessionStore.Remove(characterId, session.WorldSessionId, session.WorldSessionToken);
    return Results.NoContent();
});

Console.WriteLine($"WorldServer HTTP debug endpoint listening on {config.HttpUrl}.");
Console.WriteLine("WorldServer foundation is running. Press Ctrl+C to stop.");

await app.RunAsync();
