using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShooterMmo.GameSimulation;
using ShooterMmo.Shared.Configuration;
using SimulationWorker.Auth;
using SimulationWorker.Config;
using SimulationWorker.Corpses;
using SimulationWorker.Entities;
using SimulationWorker.Health;
using SimulationWorker.Items;
using SimulationWorker.Realtime;
using SimulationWorker.Registry;
using SimulationWorker.Sessions;
using SimulationWorker.WorldActors;
using SimulationWorker.WorldCollision;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration.AddJsonFile(
    Path.Combine("Config", "appsettings.json"),
    optional: false,
    reloadOnChange: true);
builder.Configuration.AddJsonFile(
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

var config = SimulationWorkerConfig.FromConfiguration(builder.Configuration);
var worldActorRuntime = WorldActorLoader.Load(config);
WorldActorProtocolCompatibility.Validate(worldActorRuntime);
var developmentItemInteractionOptions =
    DevelopmentItemInteractionOptions.FromConfiguration(
        builder.Configuration,
        builder.Environment.IsDevelopment());
var collisionStreamingStore = WorldCollisionLoader.LoadStreaming(config);
collisionStreamingStore.Refresh([
    new SimulationVector3(
        config.MovementSpawn.X,
        config.MovementSpawn.Y,
        config.MovementSpawn.Z)
]);
var staticCollisionWorld = collisionStreamingStore.CollisionWorld;
if (staticCollisionWorld.ChunkCount == 0)
{
    throw new InvalidOperationException(
        "No collision chunk is available around the configured movement spawn.");
}
var dynamicCollisionWorld = new DynamicCollisionWorld(staticCollisionWorld.ChunkSize);
var collisionWorld = new CompositeCollisionWorld(
    staticCollisionWorld,
    dynamicCollisionWorld);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(worldActorRuntime);
builder.Services.AddSingleton(developmentItemInteractionOptions);
builder.Services.AddSingleton(collisionStreamingStore);
builder.Services.AddSingleton(staticCollisionWorld);
builder.Services.AddSingleton(dynamicCollisionWorld);
builder.Services.AddSingleton(collisionWorld);
builder.Services.AddSingleton<ICollisionWorld>(collisionWorld);
builder.Services.AddSingleton<ActiveSimulationSessionStore>();
builder.Services.AddSingleton<CarryStateStore>();
builder.Services.AddSingleton<DurableCorpseStore>();
builder.Services.AddSingleton<CorpseViewerRegistry>();
builder.Services.AddSingleton<ItemInteractionAccessService>();
builder.Services.AddSingleton<SimulationItemInteractionService>();
builder.Services.AddSingleton<SimulationCorpseInteractionService>();
builder.Services.AddSingleton<SimulationEntityRegistry>();
builder.Services.AddSingleton<WorldActorStore>();
builder.Services.AddSingleton<WorldActorActivityScheduler>();
builder.Services.AddSingleton<WorldInteractionLeaseRegistry>();
builder.Services.AddSingleton<IWorldActorCapabilityAvailabilityPolicy,
    DefaultWorldActorCapabilityAvailabilityPolicy>();
builder.Services.AddSingleton<NpcItemLifecycleCapabilityExecutor>();
builder.Services.AddSingleton<IWorldActorCapabilityHandler,
    InsuranceWorldActorCapabilityHandler>();
builder.Services.AddSingleton<IWorldActorCapabilityHandler,
    QuestOfferWorldActorCapabilityHandler>();
builder.Services.AddSingleton<IWorldActorCapabilityHandler,
    QuestTurnInWorldActorCapabilityHandler>();
builder.Services.AddSingleton<WorldActorCapabilityRegistry>();
builder.Services.AddSingleton<WorldActorLineOfSightService>();
builder.Services.AddSingleton<WorldActorMetrics>();
builder.Services.AddSingleton<WorldInteractionAuthorityService>();
builder.Services.AddSingleton<ConnectionEntityBindingRegistry>();
builder.Services.AddSingleton<RealtimeTransportReadiness>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SimulationWorkerRegistrationLease>();
builder.Services.AddSingleton(new SimulationInterestManager(config.InterestManagement));
builder.Services.AddSingleton<RealtimeNetworkMetrics>();
builder.Services.AddSingleton<RealtimePerformanceMetrics>();
builder.Services.AddSingleton(SimulationWorkerIdentity.Create());
builder.Services.AddTransient<AuthServiceAuthenticationHandler>();
builder.Services.AddHttpClient<AuthServiceClient>(httpClient =>
{
    httpClient.BaseAddress = config.AuthServiceBaseUrl;
    httpClient.Timeout = config.AuthServiceTimeout;
}).AddHttpMessageHandler<AuthServiceAuthenticationHandler>();
builder.Services.AddSingleton<SimulationJoinService>();
builder.Services.AddSingleton<SimulationSessionReleaseService>();
builder.Services.AddSingleton<SimulationWorkerHealthService>();
builder.Services.AddHostedService<RealtimeSimulationService>();
builder.Services.AddHostedService<SimulationSessionHeartbeatService>();
builder.Services.AddHostedService<SimulationWorkerHeartbeatService>();
builder.Services.AddHostedService<SimulationWorkerRegistrationLeaseMonitor>();
builder.Services.AddHostedService<DurableCorpseRestorationService>();
builder.Services.AddHostedService<RealtimeMetricsReporterService>();

using var host = builder.Build();

if (args.Contains("--health-check-only", StringComparer.OrdinalIgnoreCase))
{
    var healthService = host.Services.GetRequiredService<SimulationWorkerHealthService>();
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

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SimulationWorker");
if (developmentItemInteractionOptions.GlobalBankAndRecoveryAccess)
{
    logger.LogWarning(
        "Development global Bank and Recovery Storage access is enabled. "
        + "Production service-point authority remains unchanged.");
}

logger.LogInformation(
    "Starting simulation worker {WorkerId} for fleet {FleetId}, node {NodeId}, shard {ShardId}, and world {WorldId} on UDP port {UdpPort}. Advertising {AdvertisedHost}:{AdvertisedUdpPort} with runtime {RuntimeId}, simulation revision {SimulationRevision}, collision revision {CollisionRevision}, and {LoadedCollisionChunks} of {AvailableCollisionChunks} collision chunks initially loaded.",
    config.SimulationWorkerId,
    config.FleetId,
    config.NodeId,
    config.ShardId,
    config.WorldId,
    config.UdpPort,
    config.AdvertisedHost,
    config.AdvertisedUdpPort,
    host.Services.GetRequiredService<SimulationWorkerIdentity>().RuntimeId,
    GameSimulationCompatibility.Revision,
    staticCollisionWorld.Revision,
    collisionStreamingStore.LoadedChunkCount,
    collisionStreamingStore.AvailableChunkCount);

logger.LogInformation(
    "Loaded world actor revision {WorldActorRevision} with {ActorDefinitionCount} definitions and {ActorInstanceCount} assignment instances.",
    worldActorRuntime.Revision,
    worldActorRuntime.Actors.Length,
    worldActorRuntime.SpawnInstances.Length);

await host.RunAsync();
