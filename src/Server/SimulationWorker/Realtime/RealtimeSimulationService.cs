using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.SimulationWorker.WorldCollision;

namespace Mmorpg.SimulationWorker.Realtime;

// Hosts the realtime UDP server. The realtime components are composed here from the
// injected services and run on one loop strand; see RealtimeServer.
public sealed class RealtimeSimulationService(
    SimulationWorkerConfig config,
    SimulationJoinService joinService,
    SimulationSessionReleaseService releaseService,
    ActiveSimulationSessionStore sessionStore,
    CarryStateStore carryStateStore,
    SimulationItemInteractionService itemInteractionService,
    SimulationCorpseInteractionService corpseInteractionService,
    DurableCorpseStore corpseStore,
    CorpseViewerRegistry corpseViewers,
    SimulationEntityRegistry entityRegistry,
    ConnectionEntityBindingRegistry connectionBindings,
    RealtimeTransportReadiness transportReadiness,
    ChunkedStaticCollisionWorld staticCollisionWorld,
    ICollisionWorld collisionWorld,
    ILogger<RealtimeSimulationService> logger,
    WorldCollisionStreamingStore? collisionStreamingStore = null,
    SimulationInterestManager? providedInterestManager = null,
    RealtimeNetworkMetrics? providedNetworkMetrics = null,
    SimulationWorkerRegistrationLease? registrationLease = null,
    RealtimePerformanceMetrics? providedPerformanceMetrics = null,
    WorldActorStore? providedWorldActorStore = null,
    WorldActorActivityScheduler? providedWorldActorActivityScheduler = null,
    WorldInteractionLeaseRegistry? providedWorldInteractionLeases = null,
    WorldInteractionAuthorityService? providedWorldInteractionAuthority = null,
    WorldActorMetrics? providedWorldActorMetrics = null,
    SimulationWorkerIdentity? workerIdentity = null,
    LiveMobCorpseStore? providedLiveMobCorpseStore = null,
    LiveMobCorpseInteractionService? providedLiveMobCorpseInteractionService = null,
    CorpseRuntimeMetrics? providedCorpseRuntimeMetrics = null,
    DevelopmentItemInteractionOptions? developmentItemInteractions = null,
    IWorldActorCapabilityAvailabilityPolicy? capabilityAvailability = null)
    : BackgroundService
{
    private readonly RealtimeServer server = RealtimeServerComposition.Create(
        new RealtimeServerDependencies(
            config,
            joinService,
            releaseService,
            sessionStore,
            carryStateStore,
            itemInteractionService,
            corpseInteractionService,
            corpseStore,
            corpseViewers,
            entityRegistry,
            connectionBindings,
            transportReadiness,
            staticCollisionWorld,
            collisionWorld,
            collisionStreamingStore,
            providedInterestManager,
            providedNetworkMetrics,
            registrationLease,
            providedPerformanceMetrics,
            providedWorldActorStore,
            providedWorldActorActivityScheduler,
            providedWorldInteractionLeases,
            providedWorldInteractionAuthority,
            providedWorldActorMetrics,
            workerIdentity,
            providedLiveMobCorpseStore,
            providedLiveMobCorpseInteractionService,
            providedCorpseRuntimeMetrics,
            developmentItemInteractions,
            capabilityAvailability),
        logger);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return server.RunAsync(stoppingToken);
    }

    public override void Dispose()
    {
        base.Dispose();
        server.Dispose();
    }
}
