using Microsoft.Extensions.DependencyInjection;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// What item operations need beyond the realtime services: GameService for the operations, and
// the rules that decide when a character may reach its Bank and Recovery Storage. Without the
// options no development access applies, and without a policy the default capability
// availability applies.
internal sealed record ItemDependencies(
    SimulationItemInteractionService InteractionService,
    DevelopmentItemInteractionOptions? DevelopmentInteractions = null,
    IWorldActorCapabilityAvailabilityPolicy? CapabilityAvailability = null)
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton(provider => new ItemDependencies(
            provider.GetRequiredService<SimulationItemInteractionService>(),
            provider.GetRequiredService<DevelopmentItemInteractionOptions>(),
            provider.GetRequiredService<IWorldActorCapabilityAvailabilityPolicy>()));
    }
}

// The client item operations: moves, equips, splits, destroys, and storage access.
internal static class ItemFeature
{
    public static void Add(
        RealtimeCoreServices core,
        ItemDependencies dependencies,
        PlayerCombatFeature playerCombat)
    {
        var itemOperations = new ItemOperationHandler(
            core.Peers,
            core.Sender,
            core.SessionStore,
            core.ItemStateStore,
            core.EntityRegistry,
            core.CarrySync,
            core.Teardown,
            core.AuthorityScheduler,
            core.OperationTracker,
            new ItemInteractionAccessService(
                dependencies.DevelopmentInteractions ?? DevelopmentItemInteractionOptions.Disabled,
                core.WorldInteractionLeases,
                core.WorldActorStore,
                dependencies.CapabilityAvailability
                    ?? new DefaultWorldActorCapabilityAvailabilityPolicy()),
            new ItemAuthorityOperations(
                dependencies.InteractionService,
                core.Completions,
                core.Logger),
            playerCombat.Authority);
        ItemOperationHandler.AddRoute(core.Registries.Routes, itemOperations.HandleIntent);
    }
}
