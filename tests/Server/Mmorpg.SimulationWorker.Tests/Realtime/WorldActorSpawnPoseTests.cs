using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing.Content;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A world actor's spawn names the server tick of its pose and whether a Mob walks then, so a
// client starts a Mob that comes into view walking from that pose instead of from a rest pose.
public sealed class WorldActorSpawnPoseTests
{
    [Fact]
    public void AMobSpawnSaysWhetherTheMobWalksAtItsPoseTick()
    {
        using var fixture = new CombatTickFixture(
            RealtimeCombatTests.TestRules(respawnSeconds: 30f),
            RealtimeCombatTests.TestCatalog(),
            RealtimeMobAiTests.WolvesAt([6f]));
        var wolf = fixture.WorldActors!.ListActors().Single(actor => actor.Definition.Id == "mob.feral_wolf");
        fixture.Join(1, "Wolf Bait");

        // Before it noticed anyone the wolf stands where it spawned, and so does every other actor.
        var early = Watch(fixture, 2, "Early Watcher");
        var resting = SpawnOf(early, wolf.NetworkEntityId);
        Assert.False(resting.IsMoving);
        Assert.Equal(fixture.ServerTick, resting.PoseTick);
        var others = Spawns(early).Where(spawn => spawn.Kind != RealtimeWorldActorKind.Mob).ToArray();
        Assert.Contains(others, spawn => spawn.Kind == RealtimeWorldActorKind.Npc);
        Assert.Contains(others, spawn => spawn.Kind == RealtimeWorldActorKind.ResourceNode);
        Assert.All(others, spawn => Assert.False(spawn.IsMoving));

        // It runs at its bait: a player who comes into view now sees it walk from where it is.
        Assert.True(RunUntil(fixture, () => Walked(fixture, wolf)), "the wolf to walk");
        var during = Watch(fixture, 3, "Watcher During The Run");
        var walking = SpawnOf(during, wolf.NetworkEntityId);
        Assert.True(walking.IsMoving);
        Assert.Equal(fixture.ServerTick, walking.PoseTick);
        Assert.Equal((wolf.PositionX, wolf.PositionZ), (walking.PositionX, walking.PositionZ));

        // Once it stands, its pose is the rest pose that the worker sent reliably.
        Assert.True(RunUntil(fixture, () => !Walked(fixture, wolf)), "the wolf to stand");
        var after = Watch(fixture, 4, "Watcher After The Run");
        var stood = SpawnOf(after, wolf.NetworkEntityId);
        Assert.False(stood.IsMoving);
        Assert.Equal(fixture.ServerTick, stood.PoseTick);
        Assert.True(stood.StateRevision > resting.StateRevision);
    }

    // Only an active Mob walks: an NPC, a resource node, and a hidden Mob never spawn moving, so
    // the spawn always fits the protocol's rule.
    [Fact]
    public void OnlyAnActiveMobSpawnsMoving()
    {
        var store = new WorldActorStore(WorldActorTestData.Compile(), new SimulationEntityRegistry());
        var actors = store.ActivateAssignment("development-world-1", "local-shard-1", "test-runtime");
        WorldActorRuntimeState ActorOf(string actorId) => actors.First(actor => actor.Definition.Id == actorId);
        var wolf = ActorOf("mob.feral_wolf");

        var walking = WorldActorProtocolMapper.ToSpawn(wolf, 77, true);
        Assert.Equal((77u, true), (walking.PoseTick, walking.IsMoving));
        Assert.False(WorldActorProtocolMapper.ToSpawn(wolf, 77, false).IsMoving);
        Assert.False(WorldActorProtocolMapper.ToSpawn(ActorOf("npc.city_services"), 77, true).IsMoving);
        Assert.False(WorldActorProtocolMapper.ToSpawn(ActorOf("resource.iron_vein"), 77, true).IsMoving);

        Assert.True(wolf.SetActive(false));
        var hidden = WorldActorProtocolMapper.ToSpawn(wolf, 78, true);
        Assert.Equal((78u, false), (hidden.PoseTick, hidden.IsMoving));
        Assert.True(RealtimeProtocol.TryDecodeWorldActorSpawn(RealtimeProtocol.EncodeWorldActorSpawn(hidden), out _, out var error), error);
    }

    // A player joins, sees the World, and receives the spawn of every actor it sees.
    private static CombatTickPlayer Watch(CombatTickFixture fixture, int peerId, string name)
    {
        var player = fixture.Join(peerId, name);
        fixture.ShowWorldTo(player);
        return player;
    }

    private static IReadOnlyList<RealtimeWorldActorSpawn> Spawns(CombatTickPlayer player)
    {
        return player.Received<RealtimeWorldActorSpawn>(RealtimeMessageType.WorldActorSpawn, RealtimeProtocol.TryDecodeWorldActorSpawn);
    }

    private static RealtimeWorldActorSpawn SpawnOf(CombatTickPlayer player, ulong entityId)
    {
        return Assert.Single(Spawns(player), spawn => spawn.EntityId == entityId);
    }

    // Runs one tick and tells whether the actor's pose changed in it.
    private static bool Walked(CombatTickFixture fixture, WorldActorRuntimeState actor)
    {
        var before = (actor.PositionX, actor.PositionY, actor.PositionZ, actor.YawDegrees);
        fixture.RunTick();
        return before != (actor.PositionX, actor.PositionY, actor.PositionZ, actor.YawDegrees);
    }

    // Runs ticks until the condition, which runs one tick itself, holds, for at most ten seconds.
    private static bool RunUntil(CombatTickFixture fixture, Func<bool> tickAndCheck)
    {
        for (var tick = 0; tick < fixture.TickRateHz * 10; tick++)
        {
            if (tickAndCheck())
            {
                return true;
            }
        }

        return false;
    }
}
