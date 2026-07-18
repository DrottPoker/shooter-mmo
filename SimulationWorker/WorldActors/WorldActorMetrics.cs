using System.Diagnostics.Metrics;

namespace SimulationWorker.WorldActors;

public sealed record WorldActorMetricsSnapshot(
    long NpcPopulation,
    long MobPopulation,
    long EventDrivenPopulation,
    long DormantPopulation,
    long ActivePopulation,
    long ActiveInteractions,
    long AcceptedInteractions,
    long RejectedInteractions);

public sealed class WorldActorMetrics : IDisposable
{
    public const string MeterName = "ShooterMmo.SimulationWorker.WorldActors";

    private readonly Meter meter = new(MeterName);
    private readonly Histogram<double> interactionLatency;
    private long npcPopulation;
    private long mobPopulation;
    private long eventDrivenPopulation;
    private long dormantPopulation;
    private long activePopulation;
    private long activeInteractions;
    private long acceptedInteractions;
    private long rejectedInteractions;

    public WorldActorMetrics()
    {
        meter.CreateObservableGauge("simulation_worker.world_actors.npc", () => Volatile.Read(ref npcPopulation));
        meter.CreateObservableGauge("simulation_worker.world_actors.mob", () => Volatile.Read(ref mobPopulation));
        meter.CreateObservableGauge("simulation_worker.world_actors.event_driven", () => Volatile.Read(ref eventDrivenPopulation));
        meter.CreateObservableGauge("simulation_worker.world_actors.dormant", () => Volatile.Read(ref dormantPopulation));
        meter.CreateObservableGauge("simulation_worker.world_actors.active", () => Volatile.Read(ref activePopulation));
        meter.CreateObservableGauge("simulation_worker.world_interactions.active", () => Volatile.Read(ref activeInteractions));
        meter.CreateObservableCounter("simulation_worker.world_interactions.accepted", () => Volatile.Read(ref acceptedInteractions));
        meter.CreateObservableCounter("simulation_worker.world_interactions.rejected", () => Volatile.Read(ref rejectedInteractions));
        interactionLatency = meter.CreateHistogram<double>(
            "simulation_worker.world_interactions.duration",
            "ms");
    }

    public void ObservePopulation(IReadOnlyCollection<WorldActorRuntimeState> actors)
    {
        ArgumentNullException.ThrowIfNull(actors);
        Interlocked.Exchange(ref npcPopulation, actors.Count(actor => actor.Definition.Kind == "npc"));
        Interlocked.Exchange(ref mobPopulation, actors.Count(actor => actor.Definition.Kind == "mob"));
        Interlocked.Exchange(ref eventDrivenPopulation, actors.Count(actor => actor.ActivityTier == "event_driven"));
        Interlocked.Exchange(ref dormantPopulation, actors.Count(actor => actor.ActivityTier == "dormant"));
        Interlocked.Exchange(ref activePopulation, actors.Count(actor => actor.ActivityTier == "active"));
    }

    public void SetActiveInteractions(long count) =>
        Interlocked.Exchange(ref activeInteractions, count);

    public void RecordInteraction(TimeSpan duration, bool succeeded)
    {
        interactionLatency.Record(Math.Max(0d, duration.TotalMilliseconds));
        if (succeeded)
        {
            Interlocked.Increment(ref acceptedInteractions);
        }
        else
        {
            Interlocked.Increment(ref rejectedInteractions);
        }
    }

    public WorldActorMetricsSnapshot Capture()
    {
        return new WorldActorMetricsSnapshot(
            Volatile.Read(ref npcPopulation),
            Volatile.Read(ref mobPopulation),
            Volatile.Read(ref eventDrivenPopulation),
            Volatile.Read(ref dormantPopulation),
            Volatile.Read(ref activePopulation),
            Volatile.Read(ref activeInteractions),
            Volatile.Read(ref acceptedInteractions),
            Volatile.Read(ref rejectedInteractions));
    }

    public void Dispose()
    {
        meter.Dispose();
    }
}
