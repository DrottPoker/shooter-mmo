using System.Diagnostics.Metrics;

namespace Mmorpg.SimulationWorker.GameServiceApi;

// Whether GameService answers, as the worker sees it, and what an outage cost. Labels are
// low-cardinality kinds, never an identity.
public sealed class GameServiceAvailabilityMetrics : IDisposable
{
    public const string MeterName = "Mmorpg.SimulationWorker.GameService";

    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> changes;
    private readonly Counter<long> refusedIntents;
    private readonly Counter<long> abandonedOperations;
    private volatile GameServiceAvailability? availability;

    public GameServiceAvailabilityMetrics()
    {
        meter.CreateObservableGauge(
            "simulation_worker.game_service.available",
            () => availability is { IsAvailable: false } ? 0 : 1);
        meter.CreateObservableGauge(
            "simulation_worker.game_service.heartbeat_age",
            ObserveHeartbeatAge,
            "s");
        changes = meter.CreateCounter<long>("simulation_worker.game_service.availability_changes");
        refusedIntents = meter.CreateCounter<long>("simulation_worker.game_service.refused_intents");
        abandonedOperations = meter.CreateCounter<long>("simulation_worker.game_service.abandoned_operations");
    }

    // The availability that the gauges read.
    public void Observe(GameServiceAvailability observed)
    {
        availability = observed;
    }

    public void RecordChange(bool available)
    {
        changes.Add(1, new KeyValuePair<string, object?>("state", available ? "available" : "unavailable"));
    }

    // Kind is item_operation, corpse_interaction, world_interaction, quest_intent, gathering, or
    // appearance.
    public void RecordRefusedIntent(string kind)
    {
        refusedIntents.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }

    // Kind is player_death, mob_kill_credit, or mob_corpse.
    public void RecordAbandonedOperation(string kind)
    {
        abandonedOperations.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }

    public void Dispose()
    {
        meter.Dispose();
    }

    // Seconds since the last successful registry heartbeat; nothing before the first one.
    private IEnumerable<Measurement<double>> ObserveHeartbeatAge()
    {
        return availability?.TimeSinceLastHeartbeat is { } age
            ? [new Measurement<double>(age.TotalSeconds)]
            : [];
    }
}
