using Microsoft.Extensions.DependencyInjection;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// What world actors need beyond the realtime services: the activity scheduler, the authority
// that opens world interactions, their metrics, and GameService for gathering. Without them no
// world actor is active, no interaction opens, and no gathering yields.
internal sealed record WorldActorDependencies(
    WorldActorActivityScheduler? ActivityScheduler = null,
    WorldInteractionAuthorityService? InteractionAuthority = null,
    WorldActorMetrics? Metrics = null,
    SimulationGatheringService? GatheringService = null)
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton(provider => new WorldActorDependencies(
            provider.GetRequiredService<WorldActorActivityScheduler>(),
            provider.GetRequiredService<WorldInteractionAuthorityService>(),
            provider.GetRequiredService<WorldActorMetrics>(),
            provider.GetRequiredService<SimulationGatheringService>()));
    }
}

// The world actor runtime with its timers, the world interactions, and gathering.
internal sealed class WorldActorFeature
{
    public WorldActorFeature(
        RealtimeCoreServices core,
        WorldActorDependencies dependencies,
        QuestProgressFeature quests,
        PlayerCombatFeature playerCombat)
    {
        Timers = new WorldActorTimerQueue();
        var gathering = new GatheringHandler(
            core.Config,
            core.Clock,
            core.Peers,
            core.Sender,
            core.WorldInteractionLeases,
            Timers,
            core.Presence,
            core.SessionStore,
            core.ItemStateStore,
            core.CarrySync,
            core.Teardown,
            core.CompletionTargets,
            core.AuthorityScheduler,
            core.OperationTracker,
            new GatheringAuthorityOperations(
                dependencies.GatheringService,
                core.Completions,
                core.Logger,
                quests.Executor),
            dependencies.Metrics,
            quests.Progress);
        Interactions = new WorldInteractionHandler(
            dependencies.InteractionAuthority,
            core.Peers,
            core.Sender,
            core.CarrySync,
            core.Teardown,
            core.CompletionTargets,
            core.AuthorityScheduler,
            core.OperationTracker,
            new WorldInteractionAuthorityOperations(
                dependencies.InteractionAuthority,
                core.Completions,
                core.Logger),
            gathering,
            core.WorldInteractionLeases,
            playerCombat.Authority);
        Driver = new WorldActorRuntimeDriver(
            core.Config,
            core.WorldActorStore,
            dependencies.ActivityScheduler,
            dependencies.Metrics,
            core.WorkerIdentity,
            core.Presence,
            core.NetworkMetrics,
            Timers,
            core.Logger);

        WorldInteractionHandler.AddRoute(core.Registries.Routes, Interactions.HandleIntent);
        var tickPhases = core.Registries.TickPhases;
        Driver.AddTickPhase(tickPhases);
        Interactions.AddTickPhase(tickPhases, core.Config.MovementSimulation.TickRateHz);
        gathering.AddTickPhase(tickPhases);
        var shutdown = core.Registries.ShutdownSteps;
        var leases = core.WorldInteractionLeases;
        var worldActorStore = core.WorldActorStore;
        shutdown.Add(RealtimeShutdownOrder.InteractionLeases, "interaction leases", () => leases?.Clear());
        shutdown.Add(RealtimeShutdownOrder.WorldActors, "world actors", () => worldActorStore?.Clear());
    }

    // The timers of world actors, which Mob deaths schedule respawns on.
    public WorldActorTimerQueue Timers { get; }

    public WorldInteractionHandler Interactions { get; }

    public WorldActorRuntimeDriver Driver { get; }
}
