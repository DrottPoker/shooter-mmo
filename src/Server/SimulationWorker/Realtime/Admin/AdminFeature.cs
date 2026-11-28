namespace Mmorpg.SimulationWorker.Realtime;

// The development admin tools of the Admin menu. A join reaches them after combat. The level and
// experience tools run through the quest executor on the authority lane that the combat authority
// gates, and apply what GameService committed to the progression.
internal static class AdminFeature
{
    public static void Add(
        RealtimeCoreServices core,
        CombatFeature combat,
        QuestProgressFeature quests,
        PlayerCombatFeature playerCombat,
        ProgressionFeature progression)
    {
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(playerCombat);
        ArgumentNullException.ThrowIfNull(progression);
        var admin = new AdminIntentHandler(
            core.Config,
            core.DevelopmentAdmin,
            core.Peers,
            core.Sender,
            core.Presence,
            combat.Combat,
            core.CollisionWorld,
            core.CollisionChunks,
            core.CarrySync,
            core.Teardown,
            core.CompletionTargets,
            core.AuthorityScheduler,
            core.OperationTracker,
            core.Completions,
            quests.Executor,
            progression.Replicator,
            core.Logger,
            playerCombat.Authority);
        AdminIntentHandler.AddRoute(core.Registries.Routes, admin.HandleIntent);
        core.Registries.Hooks.AddPlayerJoined(admin.OnPlayerJoined);
    }
}
