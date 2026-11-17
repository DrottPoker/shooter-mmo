namespace Mmorpg.SimulationWorker.Realtime;

// The interest refresh and the far states on the tick before each snapshot tick and the
// movement snapshots of players and Mobs on the snapshot ticks. Entity presence, which all follow,
// is part of RealtimeCoreServices.
internal static class SnapshotFeature
{
    public static void Add(RealtimeCoreServices core, CombatFeature combat)
    {
        var phases = core.Registries.TickPhases;
        var cadence = core.Registries.TickCadence;
        new InterestRefresher(core.EntityRegistry, core.Presence, core.PerformanceMetrics)
            .AddTickPhase(phases, cadence);
        new FarStateRefresher(
            core.Presence,
            core.Peers,
            core.Sender,
            core.FailureIsolation,
            core.Config.SnapshotRateHz).AddTickPhase(phases, cadence);
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
