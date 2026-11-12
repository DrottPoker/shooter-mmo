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
                "interaction revalidation",
                "gathering",
                "appearances",
                "snapshots",
                "corpse presence",
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
                SimulationTickOrder.InteractionRevalidation,
                SimulationTickOrder.Gathering,
                SimulationTickOrder.Appearances,
                SimulationTickOrder.Snapshots,
                SimulationTickOrder.CorpsePresence,
                SimulationTickOrder.CharacterCheckpoint
            ],
            phases.Select(phase => phase.Order));
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
            [typeof(CombatSystem), typeof(AdminIntentHandler), typeof(CharacterStatePersistence)],
            hooks.PlayerJoined.Select(hook => hook.Method.DeclaringType));
        // The lists keep registration order; the hooks of a player's way out run in reverse, so
        // persistence sees a player go while combat still holds it.
        Assert.Equal(
            [typeof(CharacterStatePersistence)],
            hooks.LeaveStarting.Select(hook => hook.Method.DeclaringType));
        Assert.Equal(
            [typeof(CombatSystem), typeof(CharacterStatePersistence)],
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

    // Teardown mirrors setup: the last feature to join a player is the first to see it leave and
    // go, while the features it builds on still hold the player.
    [Fact]
    public void LeaveStartingAndPlayerRemovedHooksRunInReverseRegistrationOrder()
    {
        using var fixture = new RealtimeLoopFixture();
        var player = fixture.ConnectPlayer(3);
        var hooks = new RealtimeLifecycleHooks();
        var calls = new List<string>();
        hooks.AddLeaveStarting((context, session) => calls.Add($"first leaves {context.PeerId}"));
        hooks.AddLeaveStarting((context, session) => calls.Add($"second leaves {context.PeerId}"));
        hooks.AddPlayerRemoved((context, session, entityId) => calls.Add($"first removes {entityId}"));
        hooks.AddPlayerRemoved((context, session, entityId) => calls.Add($"second removes {entityId}"));

        hooks.RaiseLeaveStarting(player.Context, player.Session);
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
                "peers",
                "presence",
                "connection bindings",
                "entities",
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
