namespace Mmorpg.SimulationWorker.Realtime;

// The interest refresh on the tick before each snapshot tick and the movement snapshots of
// players and Mobs on the snapshot ticks. Entity presence, which both follow, is part of
// RealtimeCoreServices.
internal static class SnapshotFeature
{
    public static void Add(RealtimeCoreServices core, CombatFeature combat)
    {
        var phases = core.Registries.TickPhases;
        var cadence = core.Registries.TickCadence;
        new InterestRefresher(core.EntityRegistry, core.Presence, core.PerformanceMetrics)
            .AddTickPhase(phases, cadence);
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
            core.Clock,
            core.FailureIsolation).AddTickPhase(phases, cadence);
    }
}
