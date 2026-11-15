using System.Diagnostics.Metrics;

namespace Mmorpg.SimulationWorker.WorldActors;

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
    public const string MeterName = "Mmorpg.SimulationWorker.WorldActors";

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
    private long resourceNodePopulation;
    private long depletedResourceNodes;
    private long activeGatherers;
    private readonly Counter<long> gatheringCycles;

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
        meter.CreateObservableGauge(
            "simulation_worker.world_actors.resource_node",
            () => Volatile.Read(ref resourceNodePopulation));
        meter.CreateObservableGauge(
            "simulation_worker.resource_nodes.depleted",
            () => Volatile.Read(ref depletedResourceNodes));
        meter.CreateObservableGauge(
            "simulation_worker.gathering.active",
            () => Volatile.Read(ref activeGatherers));
        gatheringCycles = meter.CreateCounter<long>("simulation_worker.gathering.cycles");
    }

    // Counts the actors of each kind and tier in one pass, without allocating. The loop calls it
    // when an assignment activates and then once a second from the world actor phase.
    public void ObservePopulation(IReadOnlyList<WorldActorRuntimeState> actors)
    {
        ArgumentNullException.ThrowIfNull(actors);
        long npcs = 0, mobs = 0, eventDriven = 0, dormant = 0, active = 0, resourceNodes = 0, depleted = 0;
        for (var index = 0; index < actors.Count; index++)
        {
            var actor = actors[index];
            switch (actor.Definition.Kind)
            {
                case "npc":
                    npcs++;
                    break;
                case "mob":
                    mobs++;
                    break;
                case "resource_node":
                    resourceNodes++;
                    break;
            }

            switch (actor.ActivityTier)
            {
                case "event_driven":
                    eventDriven++;
                    break;
                case "dormant":
                    dormant++;
                    break;
                case "active":
                    active++;
                    break;
            }

            if (actor.ResourceProfile is not null && !actor.IsActive)
            {
                depleted++;
            }
        }

        Interlocked.Exchange(ref npcPopulation, npcs);
        Interlocked.Exchange(ref mobPopulation, mobs);
        Interlocked.Exchange(ref eventDrivenPopulation, eventDriven);
        Interlocked.Exchange(ref dormantPopulation, dormant);
        Interlocked.Exchange(ref activePopulation, active);
        Interlocked.Exchange(ref resourceNodePopulation, resourceNodes);
        Interlocked.Exchange(ref depletedResourceNodes, depleted);
    }

    public void SetActiveGatherers(long count) =>
        Interlocked.Exchange(ref activeGatherers, count);

    // Outcome is yielded, rejected, or retried: low cardinality, never an identity.
    public void RecordGatheringCycle(string outcome)
    {
        gatheringCycles.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
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
