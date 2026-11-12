using Microsoft.Extensions.DependencyInjection;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;

namespace Mmorpg.SimulationWorker.Realtime;

// What combat needs beyond the realtime services. Without it the worker runs combat with
// the default rules, an empty item catalog, and Mobs that leave no corpse.
internal sealed record CombatDependencies(
    CombatRules Rules,
    ItemCatalogRuntimeDocument ItemCatalog,
    WorldActorRuntime? WorldActors,
    GameServiceClient? GameServiceClient,
    MobCorpseLifecycleService? MobCorpses,
    CombatMetrics? Metrics,
    ICombatRandom? Random = null)
{
    // Combat with the default rules, an empty item catalog, no Mobs, and no GameService.
    public static CombatDependencies CreateDefault()
    {
        return new CombatDependencies(CombatRules.Default, new ItemCatalogRuntimeDocument(), null, null, null, null);
    }

    // Combat draws from a random source seeded per worker runtime unless the host provides one.
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton(provider => new CombatDependencies(
            provider.GetRequiredService<CombatRules>(),
            provider.GetRequiredService<ItemCatalogRuntimeDocument>(),
            provider.GetRequiredService<WorldActorRuntime>(),
            provider.GetRequiredService<GameServiceClient>(),
            provider.GetRequiredService<MobCorpseLifecycleService>(),
            provider.GetRequiredService<CombatMetrics>(),
            provider.GetService<ICombatRandom>()));
    }
}

// The player combat authority: commits arrow batches, deaths, and kill credits, and gates the
// client operations of every other feature. Its own unit because those features need the gate
// before the combat simulation, which needs their corpse views and world actor timers, exists.
internal sealed class PlayerCombatFeature
{
    public PlayerCombatFeature(
        RealtimeCoreServices core,
        CombatDependencies dependencies,
        QuestProgressFeature quests)
    {
        var authority = new PlayerCombatAuthority(
            core.Config,
            dependencies.Rules,
            core.Clock,
            core.Peers,
            core.Sender,
            core.ItemStateStore,
            core.SessionStore,
            core.EntityRegistry,
            core.CarrySync,
            core.CorpseStore,
            core.AuthorityScheduler,
            core.Teardown,
            core.SessionOperations,
            core.OperationTracker,
            new PlayerCombatAuthorityOperations(
                dependencies.GameServiceClient,
                core.Completions,
                core.Logger),
            dependencies.Metrics,
            core.Logger,
            quests.Executor,
            quests.Progress);
        Authority = authority;
        var completions = core.Completions;
        core.Registries.ShutdownSteps.AddAsync(
            RealtimeShutdownOrder.CombatFlush,
            "combat flush",
            () => authority.FlushOnShutdownAsync(completions.TakeFor(authority)));
    }

    public PlayerCombatAuthority Authority { get; }
}

// The combat simulation: combatants, attacks and impacts, Mob brains and movement, and Mob
// deaths with their corpses, respawns, and quest drops.
internal sealed class CombatFeature
{
    public CombatFeature(
        RealtimeCoreServices core,
        CombatDependencies dependencies,
        PlayerCombatFeature playerCombat,
        QuestProgressFeature quests,
        CorpseFeature corpses,
        WorldActorFeature worldActors)
    {
        var combatants = new CombatantRegistry();
        var random = dependencies.Random
            ?? new SeededCombatRandom(core.WorkerIdentity?.RuntimeId.GetHashCode() ?? Environment.TickCount);
        var replicator = new CombatReplicator(
            core.Peers,
            core.Sender,
            core.ConnectionBindings,
            core.Presence,
            combatants);
        MobDeaths = new MobDeathProcessor(
            core.Config,
            core.Clock,
            core.WorldActorStore,
            worldActors.Timers,
            dependencies.WorldActors?.Catalog.LootProfiles ?? Array.Empty<WorldActorLootProfileDefinition>(),
            dependencies.WorldActors?.Catalog.RespawnProfiles ?? Array.Empty<WorldActorRespawnProfileDefinition>(),
            combatants,
            core.Presence,
            dependencies.MobCorpses,
            core.Completions,
            core.OperationTracker,
            random,
            core.Logger,
            quests.DropRoller);
        var lineOfSight = new CombatLineOfSight(core.CollisionWorld);
        Mobs = new MobSystem(
            core.Config,
            core.Clock,
            core.CollisionWorld,
            combatants,
            lineOfSight,
            core.Presence,
            dependencies.WorldActors,
            dependencies.Metrics);
        Combat = new CombatSystem(
            core.Config,
            dependencies.Rules,
            core.Clock,
            core.Peers,
            core.Sender,
            core.ItemStateStore,
            core.EntityRegistry,
            core.ConnectionBindings,
            core.Presence,
            core.WorldActorStore,
            dependencies.WorldActors?.Catalog.CombatProfiles ?? Array.Empty<WorldActorCombatProfileDefinition>(),
            combatants,
            replicator,
            playerCombat.Authority,
            MobDeaths,
            Mobs,
            new CombatItemCatalog(dependencies.ItemCatalog, core.Config.MovementSimulation.TickRateHz),
            lineOfSight,
            random,
            core.ShardRules,
            core.CorpseViewers,
            corpses.Views,
            core.CollisionWorld,
            dependencies.Metrics)
        {
            CloseWorldInteraction = worldActors.Interactions.CloseForPeer
        };

        CombatSystem.AddRoute(core.Registries.Routes, Combat.HandleIntent);
        Mobs.AddTickPhase(core.Registries.TickPhases);
        Combat.AddTickPhase(core.Registries.TickPhases);
        var hooks = core.Registries.Hooks;
        hooks.AddSpawnSent(replicator.OnSpawnSent);
        hooks.AddPlayerRemoved(Combat.OnPlayerEntityRemoved);
        hooks.AddPlayerJoined(Combat.OnPlayerJoined);
        core.Registries.ShutdownSteps.Add(RealtimeShutdownOrder.Combat, "combat", Combat.Clear);
    }

    public CombatSystem Combat { get; }

    public MobSystem Mobs { get; }

    public MobDeathProcessor MobDeaths { get; }
}
