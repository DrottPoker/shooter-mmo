using Microsoft.Extensions.DependencyInjection;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// What saving the place and vitals of players needs beyond the realtime services. Without it the
// worker sends no checkpoint; the final states still go with the session releases.
internal sealed record PersistenceDependencies(GameServiceClient? GameServiceClient)
{
    // No GameService, so no checkpoint.
    public static PersistenceDependencies CreateDefault()
    {
        return new PersistenceDependencies(GameServiceClient: null);
    }

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton(provider => new PersistenceDependencies(
            provider.GetRequiredService<GameServiceClient>()));
    }
}

// Where a character stands and its health and mana survive a logout: a joining player starts
// where it was saved, the changed players are saved at the checkpoint interval, lingering bodies
// too, and the final state goes with the release of every session the worker lets go of. It
// takes combat, whose combatants hold the vitals, so a join reaches combat first and a removal
// reaches it last.
internal static class PersistenceFeature
{
    public static void Add(
        RealtimeCoreServices core,
        PersistenceDependencies dependencies,
        CharacterFinalStateStore finalStates,
        CombatFeature combat)
    {
        var operations = dependencies.GameServiceClient is null
            ? null
            : new CharacterStateAuthorityOperations(
                dependencies.GameServiceClient,
                core.Config.SimulationWorkerId,
                core.Completions,
                core.Logger);
        var persistence = new CharacterStatePersistence(
            core.Config,
            core.StaticCollisionWorld.Revision,
            core.CollisionWorld,
            core.CollisionStreamingStore,
            core.SessionStore,
            core.EntityRegistry,
            combat.Combat,
            finalStates,
            core.OperationTracker,
            operations,
            core.Logger);
        var hooks = core.Registries.Hooks;
        hooks.AddPlayerPlacement(persistence.Place);
        hooks.AddPlayerJoined(persistence.OnPlayerJoined);
        hooks.AddPlayerRemoved(persistence.OnPlayerRemoved);
        persistence.AddTickPhase(core.Registries.TickPhases);
        core.Registries.ShutdownSteps.AddAsync(
            RealtimeShutdownOrder.CharacterStateFlush,
            "character state flush",
            persistence.FlushOnShutdownAsync);
    }
}
