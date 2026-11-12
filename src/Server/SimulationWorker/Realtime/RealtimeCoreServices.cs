using Microsoft.Extensions.Logging;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.SimulationWorker.WorldCollision;

namespace Mmorpg.SimulationWorker.Realtime;

// What almost every realtime feature takes, as one value: the host services that more than one
// feature uses, the loop infrastructure, the session plumbing that every authority operation
// uses, and the registries the features register in. RealtimeServerComposition builds it once,
// with the defaults applied. Loop-confined like its components.
internal sealed record RealtimeCoreServices
{
    public required SimulationWorkerConfig Config { get; init; }

    public required ILogger Logger { get; init; }

    public required RealtimeFeatureRegistries Registries { get; init; }

    // Host services.
    public required ActiveSimulationSessionStore SessionStore { get; init; }

    public required CharacterItemStateStore ItemStateStore { get; init; }

    public required SimulationEntityRegistry EntityRegistry { get; init; }

    public required ConnectionEntityBindingRegistry ConnectionBindings { get; init; }

    public required DurableCorpseStore CorpseStore { get; init; }

    public required CorpseViewerRegistry CorpseViewers { get; init; }

    public required WorldActorStore? WorldActorStore { get; init; }

    public required WorldInteractionLeaseRegistry? WorldInteractionLeases { get; init; }

    public required ChunkedStaticCollisionWorld StaticCollisionWorld { get; init; }

    public required ICollisionWorld CollisionWorld { get; init; }

    public required WorldCollisionStreamingStore? CollisionStreamingStore { get; init; }

    public required SimulationWorkerRegistrationLease? RegistrationLease { get; init; }

    public required SimulationWorkerIdentity? WorkerIdentity { get; init; }

    public required SimulationShardRules ShardRules { get; init; }

    public required DevelopmentAdminOptions DevelopmentAdmin { get; init; }

    // Loop infrastructure.
    public required RealtimeSimulationClock Clock { get; init; }

    public required RealtimeCompletionQueue Completions { get; init; }

    public required RealtimeOperationTracker OperationTracker { get; init; }

    public required RealtimeNetworkMetrics NetworkMetrics { get; init; }

    public required RealtimePerformanceMetrics PerformanceMetrics { get; init; }

    public required PeerAdmissionGuard AdmissionGuard { get; init; }

    public required RealtimePeerRegistry Peers { get; init; }

    public required RealtimePeerSender Sender { get; init; }

    public required EntityPresenceReplicator Presence { get; init; }

    public required AuthorityOperationScheduler AuthorityScheduler { get; init; }

    // Session plumbing: the committed carry state, the off-loop join and release, and the
    // teardown that every failed authority operation and ended session runs.
    public required CarryStateSynchronizer CarrySync { get; init; }

    public required SessionAuthorityOperations SessionOperations { get; init; }

    public required RealtimeSessionTeardown Teardown { get; init; }
}
