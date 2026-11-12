namespace Mmorpg.SimulationWorker.Realtime;

// Joins, leaves, and the lingering bodies that a connection which ended leaves in the world. The
// session plumbing that other features share, the session operations, the teardown, and the
// lingering bodies, is part of RealtimeCoreServices, because the combat authority and every other
// operation feature need it before this handler exists. The body timer releases a body's session
// through the combat authority, which keeps it until its durable work committed.
internal static class SessionFeature
{
    public static void Add(RealtimeCoreServices core, PlayerCombatFeature playerCombat)
    {
        var handler = new RealtimeSessionHandler(
            core.Config,
            core.Peers,
            core.Sender,
            core.SessionStore,
            core.ItemStateStore,
            core.EntityRegistry,
            core.ConnectionBindings,
            core.StaticCollisionWorld,
            core.CollisionWorld,
            core.AdmissionGuard,
            core.Presence,
            core.Registries.Hooks,
            core.Teardown,
            core.Bodies,
            core.SessionOperations,
            core.OperationTracker,
            core.RegistrationLease,
            core.NetworkMetrics,
            core.PerformanceMetrics,
            core.Logger,
            core.ShardRules,
            playerCombat.Authority);
        RealtimeSessionHandler.AddRoutes(core.Registries.Routes, handler.HandleJoinRequest, handler.HandleLeaveRequest);
        var timer = new LingeringBodyTimer(
            core.Bodies,
            core.SessionStore,
            core.Teardown,
            core.SessionOperations,
            playerCombat.Authority,
            core.Logger);
        timer.AddTickPhase(core.Registries.TickPhases);
        core.Registries.ShutdownSteps.Add(RealtimeShutdownOrder.LingeringBodies, "lingering bodies", timer.Clear);
    }
}
