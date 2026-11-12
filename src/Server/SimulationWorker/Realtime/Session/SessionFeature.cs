namespace Mmorpg.SimulationWorker.Realtime;

// Joins and leaves. The session plumbing that other features share, the session operations and
// the teardown, is part of RealtimeCoreServices, because the combat authority and every other
// operation feature need it before this handler, which defers leaves to combat, can exist.
internal sealed class SessionFeature
{
    public SessionFeature(RealtimeCoreServices core, PlayerCombatFeature playerCombat)
    {
        Handler = new RealtimeSessionHandler(
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
            core.SessionOperations,
            core.OperationTracker,
            core.RegistrationLease,
            core.NetworkMetrics,
            core.PerformanceMetrics,
            core.Logger,
            core.ShardRules,
            playerCombat.Authority);
        RealtimeSessionHandler.AddRoutes(core.Registries.Routes, Handler.HandleJoinRequest, Handler.HandleLeaveRequest);
    }

    public RealtimeSessionHandler Handler { get; }
}
