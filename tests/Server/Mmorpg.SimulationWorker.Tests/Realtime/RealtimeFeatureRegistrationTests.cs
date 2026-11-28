using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Pins what the features register in the production composition: the tick phases and their
// order, one route per client message, the order of the lifecycle hooks, and the shutdown
// steps.
public sealed class RealtimeFeatureRegistrationTests
{
    [Fact]
    public async Task TheComposedTickPhasesRunInThePinnedOrder()
    {
        await using var harness = RealtimeServerHarness.Create();

        var phases = harness.Service.Server.TickPhases;

        Assert.Equal(
            [
                "collision streaming",
                "movement",
                "player grid",
                "world actors",
                "mobs",
                "combat",
                "lingering bodies",
                "interaction revalidation",
                "gathering",
                "appearances",
                "interest",
                "far states",
                "snapshots",
                "corpse presence",
                "game service availability",
                "character checkpoint"
            ],
            phases.Select(phase => phase.Name));
        Assert.Equal(
            [
                SimulationTickOrder.CollisionStreaming,
                SimulationTickOrder.Movement,
                SimulationTickOrder.PlayerGrid,
                SimulationTickOrder.WorldActors,
                SimulationTickOrder.Mobs,
                SimulationTickOrder.Combat,
                SimulationTickOrder.LingeringBodies,
                SimulationTickOrder.InteractionRevalidation,
                SimulationTickOrder.Gathering,
                SimulationTickOrder.Appearances,
                SimulationTickOrder.Interest,
                SimulationTickOrder.FarStates,
                SimulationTickOrder.Snapshots,
                SimulationTickOrder.CorpsePresence,
                SimulationTickOrder.GameServiceAvailability,
                SimulationTickOrder.CharacterCheckpoint
            ],
            phases.Select(phase => phase.Order));
    }

    // Runs every composed phase for two seconds of ticks and records which did their work on which
    // tick. On a snapshot tick only the snapshots and the phases of every tick work, the interest
    // refresh, the looks, and the far states work on the tick before each snapshot tick, and the
    // other periodic phases never share a tick. Fails when a phase picks its own ticks onto a snapshot tick.
    [Fact]
    public async Task NoPeriodicPhaseButTheSnapshotsWorksOnASnapshotTick()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions { EnableWorldActors = true });
        var phases = harness.Service.Server.TickPhases;
        var cadence = harness.Service.Server.Registries.TickCadence;
        var ticks = 2 * harness.Config.MovementSimulation.TickRateHz;
        var worked = phases.ToDictionary(phase => phase.Name, _ => new List<uint>());
        var grid = new Mmorpg.SimulationWorker.WorldActors.PlayerSpatialGrid();
        var players = new List<Mmorpg.SimulationWorker.Entities.PlayerSimulationEntity>();
        for (var serverTick = 1u; serverTick <= ticks; serverTick++)
        {
            var tick = new SimulationTick(serverTick, new SimulationTickPlayers(players), grid);
            foreach (var phase in phases)
            {
                if (phase.Run(in tick))
                {
                    worked[phase.Name].Add(serverTick);
                }
            }
        }

        Assert.Equal(2, cadence.SnapshotIntervalTicks);
        var periodic = worked.Where(pair => pair.Value.Count > 0 && pair.Value.Count < ticks).ToArray();
        Assert.Equal(
            ["appearances", "corpse presence", "far states", "interaction revalidation", "interest", "snapshots"],
            periodic.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        foreach (var (name, workedTicks) in periodic)
        {
            if (name == SnapshotReplicator.PhaseName)
            {
                Assert.All(workedTicks, tick => Assert.True(cadence.IsSnapshotTick(tick)));
                Assert.Equal(ticks / 2, workedTicks.Count);
                continue;
            }

            Assert.All(workedTicks, tick => Assert.False(cadence.IsSnapshotTick(tick), $"{name} worked on snapshot tick {tick}."));
        }

        Assert.Equal(worked[InterestRefresher.PhaseName], worked[AppearanceReplicator.PhaseName]);
        Assert.Equal(worked[InterestRefresher.PhaseName], worked[FarStateRefresher.PhaseName]);
        Assert.All(worked[InterestRefresher.PhaseName], tick => Assert.True(cadence.IsSnapshotTick(tick + 1)));
        Assert.Equal(4, worked[CorpsePresenceReplicator.PhaseName].Count);
        Assert.Equal(10, worked["interaction revalidation"].Count);
        Assert.Empty(worked[CorpsePresenceReplicator.PhaseName].Intersect(worked["interaction revalidation"]));
    }

    [Fact]
    public void TwoTickPhasesWithOneOrderFailNamingBoth()
    {
        var phases = new SimulationTickPhases();
        phases.Add(SimulationTickOrder.Combat, "combat", Skip);

        var error = Assert.Throws<InvalidOperationException>(
            () => phases.Add(SimulationTickOrder.Combat, "duels", Skip));

        Assert.Equal("The tick phases 'combat' and 'duels' both take order 600.", error.Message);
    }

    [Fact]
    public void ShutdownStepsRunByTheirOrderAndTwoWithOneOrderFailNamingBoth()
    {
        var steps = new RealtimeShutdownSteps();
        steps.Add(RealtimeShutdownOrder.Peers, "peers", () => { });
        steps.Add(RealtimeShutdownOrder.CombatFlush, "combat flush", () => { });
        steps.Add(RealtimeShutdownOrder.Combat, "combat", () => { });

        var error = Assert.Throws<InvalidOperationException>(
            () => steps.Add(RealtimeShutdownOrder.Combat, "duels", () => { }));

        Assert.Equal(["combat flush", "combat", "peers"], steps.Steps.Select(step => step.Name));
        Assert.Equal("The shutdown steps 'combat' and 'duels' both take order 200.", error.Message);
    }

    [Fact]
    public async Task EveryClientMessageTypeHasExactlyOneRoute()
    {
        await using var harness = RealtimeServerHarness.Create();
        var routes = harness.Service.Server.Registries.Routes;
        var clientTypes = Enum.GetValues<RealtimeMessageType>()
            .Where(type => RealtimeProtocol.TryGetDescriptor(type, out var descriptor)
                && descriptor.Direction == RealtimeMessageDirection.ClientToServer)
            .Order()
            .ToArray();

        Assert.NotEmpty(clientTypes);
        Assert.Equal(
            clientTypes,
            routes.Unauthenticated.Concat(routes.Authenticated).Select(route => route.Type).Order());
        Assert.Equal(
            [RealtimeMessageType.JoinRequest],
            routes.Unauthenticated.Select(route => route.Type));
    }

    [Fact]
    public async Task TheComposedLifecycleHooksRunInThePinnedOrder()
    {
        await using var harness = RealtimeServerHarness.Create();
        var hooks = harness.Service.Server.Registries.Hooks;

        Assert.Equal(
            [typeof(CharacterStatePersistence)],
            hooks.PlayerPlacements.Select(hook => hook.Method.DeclaringType));
        Assert.Equal(
            [
                typeof(GameServiceAvailabilityNotice),
                typeof(CombatSystem),
                typeof(AdminIntentHandler),
                typeof(ProgressionReplicator),
                typeof(CharacterStatePersistence)
            ],
            hooks.PlayerJoined.Select(hook => hook.Method.DeclaringType));
        // The lists keep registration order; the hooks of a player's way out run in reverse, so
        // persistence sees a player go while combat still holds it, and the release of a player
        // that the loop took out comes last, after persistence dropped its final state.
        Assert.Equal(
            [typeof(CombatSystem)],
            hooks.ConnectionEnded.Select(hook => hook.Method.DeclaringType));
        Assert.Equal(
            [
                typeof(RealtimeFailureIsolation),
                typeof(CombatSystem),
                typeof(ProgressionReplicator),
                typeof(CharacterStatePersistence)
            ],
            hooks.PlayerRemoved.Select(hook => hook.Method.DeclaringType));
        Assert.Equal(
            [typeof(CombatReplicator)],
            hooks.SpawnSent.Select(hook => hook.Method.DeclaringType));
    }

    [Fact]
    public void PlayerJoinedHooksRunInRegistrationOrder()
    {
        using var fixture = new RealtimeLoopFixture();
        var player = fixture.ConnectPlayer(3);
        var hooks = new RealtimeLifecycleHooks();
        var calls = new List<string>();
        hooks.AddPlayerJoined((context, entity) => calls.Add($"first {context.PeerId} {entity.NetworkEntityId}"));
        hooks.AddPlayerJoined((context, entity) => calls.Add($"second {context.PeerId} {entity.NetworkEntityId}"));

        hooks.RaisePlayerJoined(player.Context, player.Entity);

        Assert.Equal(
            [$"first 3 {player.Entity.NetworkEntityId}", $"second 3 {player.Entity.NetworkEntityId}"],
            calls);
    }

    // Teardown mirrors setup: the last feature to join a player is the first to see its connection
    // end and its entity go, while the features it builds on still hold the player.
    [Fact]
    public void ConnectionEndedAndPlayerRemovedHooksRunInReverseRegistrationOrder()
    {
        using var fixture = new RealtimeLoopFixture();
        var player = fixture.ConnectPlayer(3);
        var hooks = new RealtimeLifecycleHooks();
        var calls = new List<string>();
        hooks.AddConnectionEnded((context, session, entity) => calls.Add($"first leaves {context.PeerId}"));
        hooks.AddConnectionEnded((context, session, entity) => calls.Add($"second leaves {context.PeerId}"));
        hooks.AddPlayerRemoved((context, session, entityId) => calls.Add($"first removes {entityId}"));
        hooks.AddPlayerRemoved((context, session, entityId) => calls.Add($"second removes {entityId}"));

        hooks.RaiseConnectionEnded(player.Context, player.Session, player.Entity);
        hooks.RaisePlayerRemoved(player.Context, player.Session, player.Entity.NetworkEntityId);

        Assert.Equal(
            [
                "second leaves 3",
                "first leaves 3",
                $"second removes {player.Entity.NetworkEntityId}",
                $"first removes {player.Entity.NetworkEntityId}"
            ],
            calls);
    }

    [Fact]
    public async Task TheComposedShutdownStepsRunInThePinnedOrder()
    {
        await using var harness = RealtimeServerHarness.Create();

        Assert.Equal(
            [
                "combat flush",
                "character state flush",
                "combat",
                "progression",
                "peers",
                "presence",
                "connection bindings",
                "entities",
                "lingering bodies",
                "character state",
                "corpse viewers",
                "interaction leases",
                "world actors",
                "corpse views",
                "orphan sweep",
                "population gauges"
            ],
            harness.Service.Server.Registries.ShutdownSteps.Steps.Select(step => step.Name));
    }

    private static void Skip(in SimulationTick tick)
    {
    }
}
