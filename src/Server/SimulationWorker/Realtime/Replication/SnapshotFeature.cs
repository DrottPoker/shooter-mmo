namespace Mmorpg.SimulationWorker.Realtime;

// The movement snapshots of players and Mobs at the snapshot rate. Entity presence, which
// snapshots follow, is part of RealtimeCoreServices.
internal static class SnapshotFeature
{
    public static void Add(RealtimeCoreServices core, CombatFeature combat)
    {
        new SnapshotReplicator(
            core.Config,
            combat.Mobs,
            core.EntityRegistry,
            core.ConnectionBindings,
            core.Peers,
            core.Sender,
            core.Presence,
            core.NetworkMetrics,
            core.PerformanceMetrics,
            core.Clock).AddTickPhase(core.Registries.TickPhases);
    }
}
