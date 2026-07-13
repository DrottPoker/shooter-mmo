using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShooterMmo.GameSimulation;
using ShooterMmo.Shared.Configuration;
using WorldServer.Auth;
using WorldServer.Config;
using WorldServer.Health;
using WorldServer.Realtime;
using WorldServer.Sessions;
using WorldServer.WorldCollision;
using WorldServer.Worlds;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration.AddOptionalDotEnvFile(Directory.GetCurrentDirectory());
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var config = WorldServerConfig.FromConfiguration(builder.Configuration);
var staticCollisionWorld = WorldCollisionLoader.Load(config);
var dynamicCollisionWorld = new DynamicCollisionWorld(staticCollisionWorld.ChunkSize);
var collisionWorld = new CompositeCollisionWorld(
    staticCollisionWorld,
    dynamicCollisionWorld);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(staticCollisionWorld);
builder.Services.AddSingleton(dynamicCollisionWorld);
builder.Services.AddSingleton(collisionWorld);
builder.Services.AddSingleton<ICollisionWorld>(collisionWorld);
builder.Services.AddSingleton<ActivePlayerSessionStore>();
builder.Services.AddSingleton<RealtimeTransportReadiness>();
builder.Services.AddSingleton(WorldServerInstanceIdentity.Create());
builder.Services.AddTransient<AuthServiceAuthenticationHandler>();
builder.Services.AddHttpClient<AuthServiceClient>(httpClient =>
{
    httpClient.BaseAddress = config.AuthServiceBaseUrl;
    httpClient.Timeout = config.AuthServiceTimeout;
}).AddHttpMessageHandler<AuthServiceAuthenticationHandler>();
builder.Services.AddSingleton<WorldJoinService>();
builder.Services.AddSingleton<WorldSessionReleaseService>();
builder.Services.AddSingleton<WorldServerHealthService>();
builder.Services.AddHostedService<RealtimeServerService>();
builder.Services.AddHostedService<WorldSessionHeartbeatService>();
builder.Services.AddHostedService<WorldRegistryHeartbeatService>();

using var host = builder.Build();

if (args.Contains("--health-check-only", StringComparer.OrdinalIgnoreCase))
{
    var healthService = host.Services.GetRequiredService<WorldServerHealthService>();
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

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WorldServer");
logger.LogInformation(
    "Starting {WorldServerId} as a headless .NET worker on UDP port {UdpPort}, advertising {AdvertisedHost}:{AdvertisedUdpPort}, with simulation revision {SimulationRevision} and collision revision {CollisionRevision}.",
    config.WorldServerId,
    config.UdpPort,
    config.AdvertisedHost,
    config.AdvertisedUdpPort,
    GameSimulationCompatibility.Revision,
    staticCollisionWorld.Revision);

await host.RunAsync();
