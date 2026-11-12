using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Combat;
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

// The host services that the realtime infrastructure and more than one feature take. A
// missing optional service gets the default that RealtimeServerComposition applies: a new
// interest manager, metrics the server owns, no registration lease, no world actors, a Shard
// rule set without player versus player, and no admin tools.
internal sealed record RealtimeHostDependencies(
    SimulationWorkerConfig Config,
    SimulationJoinService JoinService,
    SimulationSessionReleaseService ReleaseService,
    ActiveSimulationSessionStore SessionStore,
    CharacterItemStateStore ItemStateStore,
    SimulationEntityRegistry EntityRegistry,
    ConnectionEntityBindingRegistry ConnectionBindings,
    RealtimeTransportReadiness TransportReadiness,
    DurableCorpseStore CorpseStore,
    CorpseViewerRegistry CorpseViewers,
    ChunkedStaticCollisionWorld StaticCollisionWorld,
    ICollisionWorld CollisionWorld,
    WorldCollisionStreamingStore? CollisionStreamingStore = null,
    SimulationInterestManager? InterestManager = null,
    RealtimeNetworkMetrics? NetworkMetrics = null,
    RealtimePerformanceMetrics? PerformanceMetrics = null,
    SimulationWorkerRegistrationLease? RegistrationLease = null,
    SimulationWorkerIdentity? WorkerIdentity = null,
    WorldActorStore? WorldActorStore = null,
    WorldInteractionLeaseRegistry? WorldInteractionLeases = null,
    SimulationShardRules? ShardRules = null,
    DevelopmentAdminOptions? DevelopmentAdmin = null)
{
    // Registers the record from the services that Program.cs registers.
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton(provider => new RealtimeHostDependencies(
            provider.GetRequiredService<SimulationWorkerConfig>(),
            provider.GetRequiredService<SimulationJoinService>(),
            provider.GetRequiredService<SimulationSessionReleaseService>(),
            provider.GetRequiredService<ActiveSimulationSessionStore>(),
            provider.GetRequiredService<CharacterItemStateStore>(),
            provider.GetRequiredService<SimulationEntityRegistry>(),
            provider.GetRequiredService<ConnectionEntityBindingRegistry>(),
            provider.GetRequiredService<RealtimeTransportReadiness>(),
            provider.GetRequiredService<DurableCorpseStore>(),
            provider.GetRequiredService<CorpseViewerRegistry>(),
            provider.GetRequiredService<ChunkedStaticCollisionWorld>(),
            provider.GetRequiredService<ICollisionWorld>(),
            provider.GetRequiredService<WorldCollisionStreamingStore>(),
            provider.GetRequiredService<SimulationInterestManager>(),
            provider.GetRequiredService<RealtimeNetworkMetrics>(),
            provider.GetRequiredService<RealtimePerformanceMetrics>(),
            provider.GetRequiredService<SimulationWorkerRegistrationLease>(),
            provider.GetRequiredService<SimulationWorkerIdentity>(),
            provider.GetRequiredService<WorldActorStore>(),
            provider.GetRequiredService<WorldInteractionLeaseRegistry>(),
            provider.GetRequiredService<SimulationShardRules>(),
            provider.GetRequiredService<DevelopmentAdminOptions>()));
    }
}
