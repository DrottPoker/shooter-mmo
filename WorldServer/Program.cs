using ShooterMmo.Shared.Health;
using ShooterMmo.Shared.Http;
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
builder.Services.AddTransient<AuthServiceAuthenticationHandler>();
builder.Services.AddHttpClient<AuthServiceClient>(httpClient =>
{
    httpClient.BaseAddress = config.AuthServiceBaseUrl;
    httpClient.Timeout = config.AuthServiceTimeout;
}).AddHttpMessageHandler<AuthServiceAuthenticationHandler>();
builder.Services.AddSingleton<WorldJoinService>();
builder.Services.AddHostedService<WorldSessionHeartbeatService>();
builder.Services.AddApiProblemDetails();

var app = builder.Build();

app.UseApiPipeline();

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

if (app.Environment.IsDevelopment())
{
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

            return Results.Problem(
                statusCode: release.StatusCode,
                title: "AuthService request failed",
                detail: release.Error!.Message,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = release.Error.Code
                });
        }

        sessionStore.Remove(characterId, session.WorldSessionId, session.WorldSessionToken);
        return Results.NoContent();
    });
}

Console.WriteLine(app.Environment.IsDevelopment()
    ? $"WorldServer HTTP debug endpoints listening on {config.HttpUrl}."
    : "WorldServer HTTP debug endpoints are disabled outside Development.");
Console.WriteLine("WorldServer foundation is running. Press Ctrl+C to stop.");

await app.RunAsync();
