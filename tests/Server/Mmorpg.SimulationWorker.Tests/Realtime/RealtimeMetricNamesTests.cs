using System.Diagnostics.Metrics;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Pins the realtime instrument names, so refactoring the realtime service cannot rename
// or drop a metric that dashboards and the periodic metrics log depend on.
public sealed class RealtimeMetricNamesTests
{
    private static readonly string[] ExpectedRealtimeInstruments =
    [
        "simulation_worker.realtime.connections.rejected",
        "simulation_worker.realtime.despawn.packets",
        "simulation_worker.realtime.entities",
        "simulation_worker.realtime.joins.accepted",
        "simulation_worker.realtime.joins.rejected",
        "simulation_worker.realtime.peers",
        "simulation_worker.realtime.peers.unauthenticated",
        "simulation_worker.realtime.players.real",
        "simulation_worker.realtime.players.synthetic",
        "simulation_worker.realtime.quota_rejected.packets",
        "simulation_worker.realtime.received.bytes",
        "simulation_worker.realtime.received.packets",
        "simulation_worker.realtime.sent.bytes",
        "simulation_worker.realtime.sent.packets",
        "simulation_worker.realtime.snapshot.bytes",
        "simulation_worker.realtime.snapshot.entity_records",
        "simulation_worker.realtime.snapshot_backpressure_dropped.packets",
        "simulation_worker.realtime.snapshot_dropped.packets",
        "simulation_worker.realtime.spawn.packets"
    ];

    private static readonly string[] ExpectedPerformanceInstruments =
    [
        "simulation_worker.corpse.mutation.active_aggregates",
        "simulation_worker.corpse.mutation.backlog",
        "simulation_worker.join.finalization.duration",
        "simulation_worker.join.queue_delay.duration",
        "simulation_worker.loop.completed_operations.backlog",
        "simulation_worker.loop.completed_operations.duration",
        "simulation_worker.loop.network_poll.duration",
        "simulation_worker.simulation.collision_streaming.duration",
        "simulation_worker.simulation.interest.duration",
        "simulation_worker.simulation.movement.duration",
        "simulation_worker.simulation.snapshot.entity_updates_deferred",
        "simulation_worker.simulation.snapshot.packets_encoded",
        "simulation_worker.simulation.snapshot.packets_sent",
        "simulation_worker.simulation.snapshot.visibility_groups",
        "simulation_worker.simulation.snapshot_broadcast.duration",
        "simulation_worker.simulation.tick.duration",
        "simulation_worker.simulation.tick.lag",
        "simulation_worker.simulation.tick.overruns",
        "simulation_worker.simulation.tick.resynchronizations"
    ];

    [Fact]
    public void RealtimeMetersPublishTheDocumentedInstrumentNames()
    {
        var published = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal)
        {
            [RealtimeNetworkMetrics.MeterName] = new(StringComparer.Ordinal),
            [RealtimePerformanceMetrics.MeterName] = new(StringComparer.Ordinal)
        };
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (published.TryGetValue(instrument.Meter.Name, out var names))
                {
                    lock (names)
                    {
                        names.Add(instrument.Name);
                    }
                }
            }
        };
        listener.Start();

        using var networkMetrics = new RealtimeNetworkMetrics();
        using var performanceMetrics = new RealtimePerformanceMetrics();

        Assert.Equal(ExpectedRealtimeInstruments, published[RealtimeNetworkMetrics.MeterName]);
        Assert.Equal(
            ExpectedPerformanceInstruments,
            published[RealtimePerformanceMetrics.MeterName]);
    }

    // The playtest dashboard reads the world actor, interaction, and gathering instruments.
    [Fact]
    public void WorldActorMeterPublishesTheDocumentedInstrumentNames()
    {
        var published = new SortedSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == WorldActorMetrics.MeterName)
                {
                    lock (published)
                    {
                        published.Add(instrument.Name);
                    }
                }
            }
        };
        listener.Start();

        using var metrics = new WorldActorMetrics();

        Assert.Equal(
            [
                "simulation_worker.gathering.active",
                "simulation_worker.gathering.cycles",
                "simulation_worker.resource_nodes.depleted",
                "simulation_worker.world_actors.active",
                "simulation_worker.world_actors.dormant",
                "simulation_worker.world_actors.event_driven",
                "simulation_worker.world_actors.mob",
                "simulation_worker.world_actors.npc",
                "simulation_worker.world_actors.resource_node",
                "simulation_worker.world_interactions.accepted",
                "simulation_worker.world_interactions.active",
                "simulation_worker.world_interactions.duration",
                "simulation_worker.world_interactions.rejected"
            ],
            published);
    }

    // Monitoring documents every combat instrument.
    [Fact]
    public void CombatMeterPublishesTheDocumentedInstrumentNames()
    {
        var published = new SortedSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == CombatMetrics.MeterName)
                {
                    lock (published)
                    {
                        published.Add(instrument.Name);
                    }
                }
            }
        };
        listener.Start();

        using var metrics = new CombatMetrics();

        Assert.Equal(
            [
                "simulation_worker.combat.active_combatants",
                "simulation_worker.combat.ammunition_batches",
                "simulation_worker.combat.attacks",
                "simulation_worker.combat.death_partitions",
                "simulation_worker.combat.deaths",
                "simulation_worker.combat.dropped_shots",
                "simulation_worker.combat.engaged_mobs",
                "simulation_worker.combat.hits",
                "simulation_worker.combat.mob_path_plans",
                "simulation_worker.combat.mob_path_queue",
                "simulation_worker.combat.mob_phase.duration",
                "simulation_worker.combat.moving_mobs",
                "simulation_worker.combat.pending_shots",
                "simulation_worker.combat.phase.duration"
            ],
            published);
    }

    [Fact]
    public void ServiceFeaturesDocumentNamesTheRealtimeMeters()
    {
        var document = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "docs",
            "SERVICE_FEATURES.md"));

        Assert.Contains($"`{RealtimeNetworkMetrics.MeterName}`", document, StringComparison.Ordinal);
        Assert.Contains(
            $"`{RealtimePerformanceMetrics.MeterName}`",
            document,
            StringComparison.Ordinal);
        Assert.Contains($"`{CombatMetrics.MeterName}`", document, StringComparison.Ordinal);
    }
}
