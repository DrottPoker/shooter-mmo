namespace Mmorpg.SimulationWorker.Realtime;

// Player movement input, the movement of every player per tick, the grid of player positions,
// and the collision chunks streamed around players and moving Mobs. The carry state that
// movement applies is part of RealtimeCoreServices, because every item-changing feature
// commits it before combat, which blocks movement, exists.
internal static class MovementFeature
{
    public static void Add(RealtimeCoreServices core, CombatFeature combat)
    {
        MovementInputHandler.AddRoute(
            core.Registries.Routes,
            new MovementInputHandler(core.Peers, core.Sender, combat.Combat.IsMovementBlocked).HandleInput);
        var tickPhases = core.Registries.TickPhases;
        new CollisionStreamingTickPhase(
            core.Config,
            combat.Mobs,
            core.CollisionStreamingStore,
            core.PerformanceMetrics).AddTickPhase(tickPhases);
        new PlayerMovementTickPhase(core.CarrySync, core.PerformanceMetrics).AddTickPhase(tickPhases);
        tickPhases.Add(
            SimulationTickOrder.PlayerGrid,
            "player grid",
            static (in SimulationTick tick) => tick.PlayerGrid.Rebuild(tick.Players.AsReadOnlyList()));
    }
}
