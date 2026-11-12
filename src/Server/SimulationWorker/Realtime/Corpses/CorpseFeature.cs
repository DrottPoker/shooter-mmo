using Microsoft.Extensions.DependencyInjection;
using Mmorpg.SimulationWorker.Corpses;

namespace Mmorpg.SimulationWorker.Realtime;

// What corpses need beyond the realtime services: GameService for player corpse operations, and
// the live Mob corpses with their operations and metrics. Without the live Mob corpse services
// only player corpses exist.
internal sealed record CorpseDependencies(
    SimulationCorpseInteractionService InteractionService,
    LiveMobCorpseStore? LiveMobCorpses = null,
    LiveMobCorpseInteractionService? LiveMobCorpseInteractions = null,
    CorpseRuntimeMetrics? Metrics = null)
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton(provider => new CorpseDependencies(
            provider.GetRequiredService<SimulationCorpseInteractionService>(),
            provider.GetRequiredService<LiveMobCorpseStore>(),
            provider.GetRequiredService<LiveMobCorpseInteractionService>(),
            provider.GetRequiredService<CorpseRuntimeMetrics>()));
    }
}

// Corpse presence, the corpse views of their viewers, and the corpse operations.
internal sealed class CorpseFeature
{
    public CorpseFeature(
        RealtimeCoreServices core,
        CorpseDependencies dependencies,
        AppearanceFeature appearance,
        PlayerCombatFeature playerCombat)
    {
        var views = new CorpseViewDistributor(
            core.CorpseViewers,
            core.WorldInteractionLeases,
            core.Peers,
            core.Sender,
            core.CorpseStore,
            dependencies.LiveMobCorpses);
        Views = views;
        var presence = new CorpsePresenceReplicator(
            core.Config,
            core.CorpseStore,
            dependencies.LiveMobCorpses,
            dependencies.Metrics,
            views,
            core.Peers,
            core.Sender)
        {
            VisibleEquipmentSlots = appearance.Settings.VisibleEquipmentSlots
        };
        var authority = new CorpseAuthorityOperations(
            dependencies.InteractionService,
            dependencies.LiveMobCorpseInteractions,
            core.Completions,
            core.Logger);
        MutationCoordinator = authority.MutationCoordinator;
        var finalizer = new CorpseOperationFinalizer(
            core.Peers,
            core.Sender,
            core.SessionStore,
            core.ItemStateStore,
            core.EntityRegistry,
            core.CarrySync,
            core.Teardown,
            core.AuthorityScheduler,
            core.CorpseViewers,
            core.WorldInteractionLeases,
            views,
            core.CorpseStore,
            dependencies.LiveMobCorpses,
            playerCombat.Authority);
        var interactions = new CorpseInteractionHandler(
            core.Config,
            core.Peers,
            core.Sender,
            core.ItemStateStore,
            core.CorpseStore,
            dependencies.LiveMobCorpses,
            core.CorpseViewers,
            core.WorldInteractionLeases,
            core.AuthorityScheduler,
            core.OperationTracker,
            core.Completions,
            authority,
            finalizer,
            playerCombat.Authority);

        CorpseInteractionHandler.AddRoute(core.Registries.Routes, interactions.HandleIntent);
        presence.AddTickPhase(core.Registries.TickPhases);
        var shutdown = core.Registries.ShutdownSteps;
        shutdown.Add(RealtimeShutdownOrder.CorpseViewers, "corpse viewers", core.CorpseViewers.Clear);
        shutdown.Add(RealtimeShutdownOrder.CorpseViews, "corpse views", views.ClearSnapshots);
    }

    public CorpseViewDistributor Views { get; }

    public CorpseMutationCoordinator MutationCoordinator { get; }
}
