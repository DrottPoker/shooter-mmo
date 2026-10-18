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
    private long activeCombatants;
    private long pendingShots;

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
