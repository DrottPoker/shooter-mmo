using ShooterMmo.Shared.Configuration;
using ShooterMmo.Shared.Http;
using WorldServer.Auth;
using WorldServer.Config;
using WorldServer.Health;
using WorldServer.Sessions;
using WorldServer.Worlds;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddOptionalDotEnvFile(builder.Environment.ContentRootPath);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

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
builder.Services.AddHostedService<WorldRegistryHeartbeatService>();
builder.Services.AddSingleton<WorldServerHealthService>();
builder.Services.AddApiProblemDetails();

var app = builder.Build();

app.UseApiPipeline();

Console.WriteLine($"Starting {config.WorldServerId} on UDP port {config.UdpPort}.");

if (args.Contains("--health-check-only", StringComparer.OrdinalIgnoreCase))
{
    var healthService = app.Services.GetRequiredService<WorldServerHealthService>();
    var health = await healthService.CheckReadinessAsync(CancellationToken.None);
    foreach (var dependency in health.Dependencies)
    {
        Console.WriteLine(dependency.IsReachable
            ? $"{dependency.Name} ready at {dependency.Target}."
            : $"{dependency.Name} not ready at {dependency.Target}: {dependency.Error}");
    }

    Environment.ExitCode = health.Status == "ready" ? 0 : 1;
    return;
}

app.MapGet("/", () => Results.Redirect("/health/ready"));
app.MapGet("/health/live", () => Results.Ok(new
{
    service = "WorldServer",
    status = "live",
    checkedAt = DateTimeOffset.UtcNow
}));
app.MapGet("/health/ready", async (
    WorldServerHealthService healthService,
    CancellationToken cancellationToken) =>
{
    var health = await healthService.CheckReadinessAsync(cancellationToken);
    return Results.Json(
        health,
        statusCode: health.Status == "ready"
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable);
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
