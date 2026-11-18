using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Server.Common.Characters;
using Mmorpg.Server.Common.Configuration;
using Mmorpg.Server.Common.Health;
using Mmorpg.Server.Common.Items;
using Mmorpg.Server.Common.Quests;
using Mmorpg.Server.Telemetry;
using Mmorpg.SimulationWorker;
using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Health;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.Telemetry;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.SimulationWorker.WorldCollision;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Quests;

// `probe` is the health check of a container: it runs before any host or configuration exists.
if (ContainerProbe.IsProbe(args))
{
    Environment.ExitCode = await ContainerProbe.RunAsync(args, Console.Out, CancellationToken.None);
    return;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration.AddJsonFile(
    Path.Combine("Config", "appsettings.json"),
    optional: false,
    reloadOnChange: false);
builder.Configuration.AddJsonFile(
    Path.Combine(
        "Config",
        $"appsettings.{builder.Environment.EnvironmentName}.json"),
    optional: true,
    reloadOnChange: false);
builder.Configuration.AddOptionalDotEnvFile(
    Directory.GetCurrentDirectory(),
    AppContext.BaseDirectory);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var config = SimulationWorkerConfig.FromConfiguration(builder.Configuration);
if (!builder.Environment.IsDevelopment())
{
    var environmentErrors = config.ErrorsOutsideDevelopment(builder.Configuration);
    if (environmentErrors.Count > 0)
    {
        throw new InvalidOperationException(
            "SimulationWorker configuration is invalid:" + Environment.NewLine
            + string.Join(Environment.NewLine, environmentErrors.Select(error => $"- {error}")));
    }
}

var telemetryOptions = TelemetryOptions.FromConfiguration(builder.Configuration);
var workerIdentity = SimulationWorkerIdentity.Create();
builder.AddServerTelemetry(
    telemetryOptions,
    SimulationWorkerTelemetry.CreateResource(
        config,
        workerIdentity,
        telemetryOptions,
        builder.Environment.EnvironmentName),
    SimulationWorkerTelemetry.MeterNames,
    [SimulationWorkerTelemetry.ActivitySourceName]);
var worldActorRuntime = WorldActorLoader.Load(config);
WorldActorProtocolCompatibility.Validate(worldActorRuntime);
var itemCatalog = ItemCatalogFileLoader.Load(config.ItemCatalogPath);
WorldActorItemReferences.Validate(worldActorRuntime.Catalog, itemCatalog);
var questCatalog = QuestCatalogFileLoader.Load(config.QuestCatalogPath);
QuestItemReferences.Validate(questCatalog.Quests, itemCatalog);
var combatRules = CombatRulesLoader.Load(config.CombatRulesPath);
var appearanceCatalog = CharacterAppearanceFileLoader.Load(config.CharacterAppearancePath);
var developmentItemInteractionOptions =
    DevelopmentItemInteractionOptions.FromConfiguration(
        builder.Configuration,
        builder.Environment.IsDevelopment());
var developmentAdminOptions =
    DevelopmentAdminOptions.FromConfiguration(
        builder.Configuration,
        builder.Environment.IsDevelopment());
var collisionStreamingStore = WorldCollisionLoader.LoadStreaming(config);
// The chunks around the spawn load before the loop starts; the loop then streams the rest on the
// store's background loader.
collisionStreamingStore.LoadBeforeStart([
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
builder.Services.AddSingleton(itemCatalog);
builder.Services.AddSingleton(questCatalog);
builder.Services.AddSingleton(combatRules);
builder.Services.AddSingleton(appearanceCatalog);
builder.Services.AddSingleton<CharacterLookStore>();
builder.Services.AddSingleton(new DevelopmentBotLooks(appearanceCatalog, itemCatalog));
builder.Services.AddSingleton<SimulationShardRules>();
builder.Services.AddSingleton<CombatMetrics>();
builder.Services.AddSingleton<PresenceMetrics>();
builder.Services.AddSingleton(developmentItemInteractionOptions);
builder.Services.AddSingleton(developmentAdminOptions);
// Registered through a factory so that the host disposes the store, and with it the chunk
// loader's task, once the realtime loop has stopped.
builder.Services.AddSingleton(_ => collisionStreamingStore);
builder.Services.AddSingleton(staticCollisionWorld);
builder.Services.AddSingleton(dynamicCollisionWorld);
builder.Services.AddSingleton(collisionWorld);
builder.Services.AddSingleton<ICollisionWorld>(collisionWorld);
builder.Services.AddSingleton<ActiveSimulationSessionStore>();
builder.Services.AddSingleton<CharacterItemStateStore>();
builder.Services.AddSingleton<DurableCorpseStore>();
builder.Services.AddSingleton<LiveMobCorpseStore>();
builder.Services.AddSingleton<CorpseRuntimeMetrics>();
builder.Services.AddSingleton<CorpseViewerRegistry>();
builder.Services.AddSingleton<SimulationItemInteractionService>();
builder.Services.AddSingleton<SimulationGatheringService>();
builder.Services.AddSingleton<SimulationCorpseInteractionService>();
builder.Services.AddSingleton<LiveMobCorpseInteractionService>();
builder.Services.AddSingleton<MobCorpseLifecycleService>();
builder.Services.AddSingleton<SimulationEntityRegistry>();
builder.Services.AddSingleton<WorldActorStore>();
builder.Services.AddSingleton<WorldActorActivityScheduler>();
builder.Services.AddSingleton<WorldInteractionLeaseRegistry>();
builder.Services.AddSingleton<IWorldActorCapabilityAvailabilityPolicy,
    DefaultWorldActorCapabilityAvailabilityPolicy>();
builder.Services.AddSingleton<NpcItemLifecycleCapabilityExecutor>();
builder.Services.AddSingleton<IWorldActorCapabilityHandler,
    InsuranceWorldActorCapabilityHandler>();
builder.Services.AddSingleton<CharacterQuestLogStore>();
builder.Services.AddSingleton(services => CharacterSessionStores.Create(
    services.GetRequiredService<CharacterItemStateStore>(),
    services.GetRequiredService<CharacterLookStore>(),
    services.GetRequiredService<CharacterQuestLogStore>()));
builder.Services.AddSingleton<QuestOperationExecutor>();
builder.Services.AddSingleton<IWorldActorCapabilityHandler,
    QuestsWorldActorCapabilityHandler>();
builder.Services.AddSingleton<IWorldActorCapabilityHandler>(
    new ItemStorageWorldActorCapabilityHandler(RealtimeWorldActorCapabilityKind.Bank));
builder.Services.AddSingleton<IWorldActorCapabilityHandler>(
    new ItemStorageWorldActorCapabilityHandler(RealtimeWorldActorCapabilityKind.RecoveryStorage));
builder.Services.AddSingleton<IWorldActorCapabilityHandler,
    VendorWorldActorCapabilityHandler>();
builder.Services.AddSingleton<IWorldActorCapabilityHandler,
    GatheringWorldActorCapabilityHandler>();
builder.Services.AddSingleton<WorldActorCapabilityRegistry>();
builder.Services.AddSingleton<WorldActorLineOfSightService>();
builder.Services.AddSingleton<WorldActorMetrics>();
builder.Services.AddSingleton<WorldInteractionAuthorityService>();
builder.Services.AddSingleton<ConnectionEntityBindingRegistry>();
builder.Services.AddSingleton<RealtimeTransportReadiness>();
builder.Services.AddSingleton<RealtimeLoopLiveness>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SimulationWorkerRegistrationLease>();
builder.Services.AddSingleton<GameServiceAvailabilityMetrics>();
builder.Services.AddSingleton<GameServiceCallMetrics>();
builder.Services.AddSingleton<GameServiceAvailability>();
builder.Services.AddSingleton<SimulationWorkerStopReason>();
builder.Services.AddSingleton(new SimulationInterestManager(config.InterestManagement));
builder.Services.AddSingleton<RealtimeNetworkMetrics>();
// The worker also counts its garbage collection pauses one by one from the runtime's events.
builder.Services.AddSingleton(_ => new RealtimePerformanceMetrics(new GcPauseMonitor()));
builder.Services.AddSingleton(workerIdentity);
builder.Services.AddTransient<GameServiceAuthenticationHandler>();
builder.Services.AddHttpClient<GameServiceClient>(httpClient =>
{
    httpClient.BaseAddress = config.GameServiceBaseUrl;
    httpClient.Timeout = config.GameServiceTimeout;
}).AddHttpMessageHandler<GameServiceAuthenticationHandler>();
builder.Services.AddSingleton<SimulationJoinService>();
builder.Services.AddSingleton<SimulationSessionReleaseService>();
builder.Services.AddSingleton<SimulationWorkerHealthService>();
// The dependency records that the realtime service takes, one per realtime feature that needs
// host services.
RealtimeHostDependencies.Register(builder.Services);
ItemDependencies.Register(builder.Services);
CorpseDependencies.Register(builder.Services);
WorldActorDependencies.Register(builder.Services);
CombatDependencies.Register(builder.Services);
AppearanceDependencies.Register(builder.Services);
QuestDependencies.Register(builder.Services);
PersistenceDependencies.Register(builder.Services);
builder.Services.AddSimulationWorkerShutdownTimeout(config);
builder.Services.AddSimulationWorkerHostedServices();

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
        + "Production requires an open Bank or Recovery Storage NPC interaction.");
}

if (developmentAdminOptions.Enabled)
{
    logger.LogWarning(
        "Development admin tools are enabled: every player may change its walk speed, teleport, "
        + "restore its health, become invulnerable, and use the quest tools.");
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
    workerIdentity.RuntimeId,
    GameSimulationCompatibility.Revision,
    staticCollisionWorld.Revision,
    collisionStreamingStore.LoadedChunkCount,
    collisionStreamingStore.AvailableChunkCount);

logger.LogInformation(
    "Garbage collector: {GarbageCollector}.",
    GcPauseMonitor.DescribeConfiguration());

logger.LogInformation(
    "Loaded world actor catalog revision {ActorCatalogRevision} with {ActorDefinitionCount} definitions and spawn revision {ActorSpawnRevision} with {ActorInstanceCount} assignment instances.",
    worldActorRuntime.Catalog.Revision,
    worldActorRuntime.Catalog.Actors.Length,
    worldActorRuntime.Spawns.Revision,
    worldActorRuntime.Spawns.SpawnInstances.Length);

if (telemetryOptions.IsEnabled)
{
    logger.LogInformation(
        "Exporting metrics, traces, and logs over OTLP to {OtlpEndpoint}.",
        telemetryOptions.OtlpEndpoint);
}
else
{
    logger.LogInformation(
        "Telemetry export is disabled. Set Telemetry:OtlpEndpoint to export metrics, traces, and logs.");
}

await host.RunAsync();
