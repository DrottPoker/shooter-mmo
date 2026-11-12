namespace Mmorpg.SimulationWorker.Realtime;

// The development admin tools of the Admin menu. A join reaches them after combat.
internal static class AdminFeature
{
    public static void Add(RealtimeCoreServices core, CombatFeature combat)
    {
        var admin = new AdminIntentHandler(
            core.Config,
            core.DevelopmentAdmin,
            core.Peers,
            core.Sender,
            core.Presence,
            combat.Combat,
            core.CollisionWorld,
            core.CollisionStreamingStore);
        AdminIntentHandler.AddRoute(core.Registries.Routes, admin.HandleIntent);
        core.Registries.Hooks.AddPlayerJoined(admin.OnPlayerJoined);
    }
}
