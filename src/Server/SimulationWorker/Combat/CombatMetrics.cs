using System.Diagnostics.Metrics;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Combat;

// Combat instruments. Labels are low-cardinality kinds and outcomes, never an identity.
public sealed class CombatMetrics : IDisposable
{
    public const string MeterName = "Mmorpg.SimulationWorker.Combat";

    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> attacks;
    private readonly Counter<long> hits;
    private readonly Counter<long> deaths;
    private readonly Counter<long> ammunitionBatches;
    private readonly Counter<long> droppedShots;
    private readonly Counter<long> deathPartitions;
    private readonly Histogram<double> phaseDuration;
    private readonly Histogram<double> mobPhaseDuration;
    private readonly Counter<long> mobPathPlans;
    private long activeCombatants;
    private long pendingShots;
    private long engagedMobs;
    private long movingMobs;
    private long mobPathQueue;

    public CombatMetrics()
    {
        attacks = meter.CreateCounter<long>("simulation_worker.combat.attacks");
        hits = meter.CreateCounter<long>("simulation_worker.combat.hits");
        deaths = meter.CreateCounter<long>("simulation_worker.combat.deaths");
        ammunitionBatches = meter.CreateCounter<long>("simulation_worker.combat.ammunition_batches");
        droppedShots = meter.CreateCounter<long>("simulation_worker.combat.dropped_shots");
        deathPartitions = meter.CreateCounter<long>("simulation_worker.combat.death_partitions");
        phaseDuration = meter.CreateHistogram<double>("simulation_worker.combat.phase.duration", "ms");
        meter.CreateObservableGauge(
            "simulation_worker.combat.active_combatants",
            () => Volatile.Read(ref activeCombatants));
        meter.CreateObservableGauge(
            "simulation_worker.combat.pending_shots",
            () => Volatile.Read(ref pendingShots));
        mobPhaseDuration = meter.CreateHistogram<double>("simulation_worker.combat.mob_phase.duration", "ms");
        mobPathPlans = meter.CreateCounter<long>("simulation_worker.combat.mob_path_plans");
        meter.CreateObservableGauge(
            "simulation_worker.combat.engaged_mobs",
            () => Volatile.Read(ref engagedMobs));
        meter.CreateObservableGauge(
            "simulation_worker.combat.moving_mobs",
            () => Volatile.Read(ref movingMobs));
        meter.CreateObservableGauge(
            "simulation_worker.combat.mob_path_queue",
            () => Volatile.Read(ref mobPathQueue));
    }

    internal void RecordAttack(CombatantKind kind)
    {
        attacks.Add(1, Kind(kind));
    }

    internal void RecordHit(CombatantKind targetKind)
    {
        hits.Add(1, Kind(targetKind));
    }

    internal void RecordDeath(CombatantKind kind)
    {
        deaths.Add(1, Kind(kind));
    }

    // Outcome is committed, retried, rejected, or session_ended.
    public void RecordAmmunitionBatch(string outcome)
    {
        ammunitionBatches.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    }

    public void RecordDroppedShots(long count)
    {
        if (count > 0)
        {
            droppedShots.Add(count);
        }
    }

    // Outcome is committed, retried, rejected, or session_ended.
    public void RecordDeathPartition(string outcome)
    {
        deathPartitions.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    }

    public void RecordPhase(TimeSpan duration)
    {
        phaseDuration.Record(duration.TotalMilliseconds);
    }

    public void SetActiveCombatants(long count)
    {
        Interlocked.Exchange(ref activeCombatants, count);
    }

    public void SetPendingShots(long count)
    {
        Interlocked.Exchange(ref pendingShots, count);
    }

    // The Mob brain and movement phase of one tick.
    public void RecordMobPhase(TimeSpan duration)
    {
        mobPhaseDuration.Record(duration.TotalMilliseconds);
    }

    // Outcome is reached, closest, or no_path.
    public void RecordMobPathPlan(string outcome)
    {
        mobPathPlans.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    }

    // Mobs that chase, attack, or evade; Mobs whose pose changed this tick; and routes
    // that wait for the planning budget.
    public void SetMobActivity(long engaged, long moving, long queuedPlans)
    {
        Interlocked.Exchange(ref engagedMobs, engaged);
        Interlocked.Exchange(ref movingMobs, moving);
        Interlocked.Exchange(ref mobPathQueue, queuedPlans);
    }

    public void Dispose()
    {
        meter.Dispose();
    }

    private static KeyValuePair<string, object?> Kind(CombatantKind kind)
    {
        return new KeyValuePair<string, object?>(
            "kind",
            kind == CombatantKind.Player ? "player" : "mob");
    }
}
