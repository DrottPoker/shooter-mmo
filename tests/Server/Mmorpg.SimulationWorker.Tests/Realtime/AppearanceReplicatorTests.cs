using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The appearance replicator, driven tick by tick in memory, so the order of spawns, changes,
// and broadcasts is exact.
public sealed class AppearanceReplicatorTests
{
    [Fact]
    public void AChangeBeforeTheFirstBroadcastReachesTheViewersTheSpawnReached()
    {
        using var fixture = new RealtimeLoopFixture();
        var looks = new CharacterLookStore();
        var replicator = new AppearanceReplicator(
            fixture.Registry,
            fixture.Sender,
            fixture.Presence,
            fixture.Entities,
            fixture.Bindings,
            fixture.CharacterItemStateStore,
            looks,
            AppearanceReplicationSettings.Default);
        fixture.Presence.Appearances = replicator;
        var hooded = fixture.ConnectPlayer(1, "Hooded");
        var viewer = fixture.ConnectPlayer(2, "Viewer");
        foreach (var player in new[] { hooded, viewer })
        {
            looks.Register(
                player.Session.CharacterId,
                player.Session.SimulationSessionId,
                player.Session.SimulationSessionToken,
                new CharacterLook(CharacterStateTestData.Appearance));
        }

        // The viewer sees the player spawn with the hood up before any broadcast ran.
        SpawnEveryoneForEveryone(fixture);
        var spawn = Decode<RealtimeEntitySpawn>(viewer.Peer, RealtimeMessageType.EntitySpawn, RealtimeProtocol.TryDecodeEntitySpawn)
            .Single(candidate => candidate.EntityId == hooded.Entity.NetworkEntityId);
        Assert.False(spawn.Appearance.HeadwearLowered);

        Assert.True(looks.TryApply(
            hooded.Session.CharacterId,
            hooded.Session.SimulationSessionId,
            hooded.Session.SimulationSessionToken,
            CharacterStateTestData.Appearance with { HeadwearLowered = true }));
        replicator.Broadcast();

        var seen = Decode<RealtimeEntityAppearanceChanged>(
            viewer.Peer,
            RealtimeMessageType.EntityAppearanceChanged,
            RealtimeProtocol.TryDecodeEntityAppearanceChanged);
        Assert.True(Assert.Single(seen, change => change.EntityId == hooded.Entity.NetworkEntityId).Appearance.HeadwearLowered);
        var own = Decode<RealtimeEntityAppearanceChanged>(
            hooded.Peer,
            RealtimeMessageType.EntityAppearanceChanged,
            RealtimeProtocol.TryDecodeEntityAppearanceChanged);
        Assert.True(Assert.Single(own, change => change.EntityId == hooded.Entity.NetworkEntityId).Appearance.HeadwearLowered);

        // Nothing changed since: the next broadcast sends nothing more.
        var sent = hooded.Peer.Sent.Count + viewer.Peer.Sent.Count;
        replicator.Broadcast();
        Assert.Equal(sent, hooded.Peer.Sent.Count + viewer.Peer.Sent.Count);
    }

    [Fact]
    public void ThePlayersToolInUseReachesTheViewersAndTheOwnerAndLeavesWithIt()
    {
        using var fixture = new RealtimeLoopFixture();
        var looks = new CharacterLookStore();
        var replicator = new AppearanceReplicator(
            fixture.Registry,
            fixture.Sender,
            fixture.Presence,
            fixture.Entities,
            fixture.Bindings,
            fixture.CharacterItemStateStore,
            looks,
            AppearanceReplicationSettings.Default);
        fixture.Presence.Appearances = replicator;
        var miner = fixture.ConnectPlayer(1, "Miner");
        var viewer = fixture.ConnectPlayer(2, "Viewer");
        foreach (var player in new[] { miner, viewer })
        {
            looks.Register(
                player.Session.CharacterId,
                player.Session.SimulationSessionId,
                player.Session.SimulationSessionToken,
                new CharacterLook(CharacterStateTestData.Appearance));
        }

        var carry = miner.Session.CarryState;
        Assert.Equal(ItemStateApplyResult.Applied, fixture.CharacterItemStateStore.ApplyCommitted(
            miner.Session.CharacterId,
            miner.Session.SimulationSessionId,
            CharacterStateTestData.ItemState(
                new PlayerCarryState(carry.ItemStateRevision + 1, carry.CarriedWeight, carry.CarryCapacity),
                new SimulationEquippedItem("tool", Guid.NewGuid(), "tool.starter_pickaxe", 1, 1)),
            out _));
        SpawnEveryoneForEveryone(fixture);
        replicator.Broadcast();
        // The Tool slot is no visible slot, so others see the pickaxe only while it is used.
        var spawn = Decode<RealtimeEntitySpawn>(viewer.Peer, RealtimeMessageType.EntitySpawn, RealtimeProtocol.TryDecodeEntitySpawn)
            .Single(candidate => candidate.EntityId == miner.Entity.NetworkEntityId);
        Assert.Equal(string.Empty, spawn.Appearance.ToolInUse);
        Assert.DoesNotContain(spawn.Appearance.Equipment, item => item.EquipmentSlotId == "tool");

        miner.Entity.UsedEquipmentSlot = "tool";
        replicator.Broadcast();
        miner.Entity.UsedEquipmentSlot = string.Empty;
        replicator.Broadcast();

        foreach (var peer in new[] { viewer.Peer, miner.Peer })
        {
            var tools = Decode<RealtimeEntityAppearanceChanged>(
                    peer,
                    RealtimeMessageType.EntityAppearanceChanged,
                    RealtimeProtocol.TryDecodeEntityAppearanceChanged)
                .Where(change => change.EntityId == miner.Entity.NetworkEntityId)
                .Select(change => change.Appearance.ToolInUse)
                .ToArray();
            Assert.Equal(["tool.starter_pickaxe", string.Empty], tools.Skip(tools.Length - 2));
        }
    }

    private static void SpawnEveryoneForEveryone(RealtimeLoopFixture fixture)
    {
        var players = new List<Mmorpg.SimulationWorker.Entities.PlayerSimulationEntity>();
        fixture.Entities.CopyPlayersTo(players);
        fixture.Presence.RebuildInterestIndex(players);
        for (var refresh = 0; refresh < 64; refresh++)
        {
            fixture.Presence.RefreshInterests(null);
        }
    }

    private static IReadOnlyList<T> Decode<T>(RecordingPeerConnection peer, RealtimeMessageType type, RealtimeTestDecoder<T> decoder)
    {
        return peer.Sent
            .Where(send => send.Type == type)
            .Select(send =>
            {
                Assert.True(decoder(send.Data, out var value, out var error), error);
                return value;
            })
            .ToArray();
    }
}
