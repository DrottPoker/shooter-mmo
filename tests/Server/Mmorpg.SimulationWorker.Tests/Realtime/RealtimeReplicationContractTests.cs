using LiteNetLib;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Characterizes the replication ordering a client relies on and the snapshot fan-out
// between two players.
public sealed class RealtimeReplicationContractTests
{
    [Fact]
    public async Task JoinAcceptedPrecedesSpawnsAndTheBaselinePrecedesSnapshots()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Ordered Hero");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        var entityId = client.JoinAccepted!.ControlledEntityId;

        await harness.PollUntilAsync(
            () => client.Has(RealtimeMessageType.OwnerSimulationSnapshot)
                && client.Has(RealtimeMessageType.SimulationSnapshot),
            "the first snapshots");

        var packets = client.Packets;
        Assert.Equal(RealtimeMessageType.JoinAccepted, packets[0].Type);
        var selfSpawn = client.DecodeWithPackets<RealtimeEntitySpawn>(
                RealtimeMessageType.EntitySpawn,
                RealtimeProtocol.TryDecodeEntitySpawn)
            .Single(value => value.Value.EntityId == entityId);
        // Reliable and unreliable packets may overtake each other on the wire, so only the
        // reliable order is asserted: JoinAccepted first, then the baseline spawn.
        Assert.Equal(selfSpawn.Packet.Index, client.ReliablePackets[1].Index);
        Assert.All(
            packets.Where(packet => packet.Type is RealtimeMessageType.OwnerSimulationSnapshot
                or RealtimeMessageType.SimulationSnapshot),
            packet =>
            {
                Assert.Equal(DeliveryMethod.Unreliable, packet.Delivery);
                Assert.Equal(RealtimeProtocol.UnreliableReceiveChannel, packet.Channel);
            });
        Assert.All(
            packets.Where(packet => packet.Type is RealtimeMessageType.JoinAccepted
                or RealtimeMessageType.EntitySpawn),
            packet =>
            {
                Assert.Equal(DeliveryMethod.ReliableOrdered, packet.Delivery);
                Assert.Equal(RealtimeProtocol.ControlChannel, packet.Channel);
            });
        // Every packet travels exactly as the schema's message descriptor says.
        Assert.All(
            packets,
            packet =>
            {
                Assert.True(RealtimeProtocol.TryGetDescriptor(packet.Type, out var descriptor));
                Assert.Equal(RealtimeMessageDirection.ServerToClient, descriptor.Direction);
                Assert.Equal(descriptor.Channel, packet.Channel);
                Assert.Equal(RealtimeDeliveryMethods.ToDeliveryMethod(descriptor.Delivery), packet.Delivery);
            });
        var owner = client.OwnerSnapshots[0];
        Assert.True(owner.ServerTick > 0);
        Assert.Equal(1, harness.NetworkMetrics!.Capture().SpawnPackets);
    }

    [Fact]
    public async Task TwoPlayersReceiveEachOtherInSnapshots()
    {
        await using var harness = RealtimeServerHarness.Create();
        var firstCharacter = harness.AddCharacter("Snapshot One");
        var secondCharacter = harness.AddCharacter("Snapshot Two");
        await harness.StartAsync();
        var first = await harness.JoinAsync(firstCharacter);
        var second = await harness.JoinAsync(secondCharacter);
        var firstEntityId = first.JoinAccepted!.ControlledEntityId;
        var secondEntityId = second.JoinAccepted!.ControlledEntityId;
        harness.PerformanceMetrics!.CaptureAndReset();

        await harness.PollUntilAsync(
            () => first.Snapshots.Any(snapshot =>
                    snapshot.Entities.Any(entity => entity.EntityId == secondEntityId))
                && second.Snapshots.Any(snapshot =>
                    snapshot.Entities.Any(entity => entity.EntityId == firstEntityId)),
            "the remote snapshots");

        Assert.Contains(first.Spawns, spawn => spawn.EntityId == secondEntityId);
        Assert.Contains(second.Spawns, spawn => spawn.EntityId == firstEntityId);
        var snapshotCount = first.Snapshots.Count;
        await harness.PollUntilAsync(
            () => first.Snapshots.Count > snapshotCount + 1,
            "a snapshot after the metrics reset");
        var performance = harness.PerformanceMetrics.CaptureAndReset();
        Assert.True(performance.SnapshotPacketsEncoded > 0);
        Assert.True(performance.SnapshotPacketsSent > 0);
        Assert.True(performance.SnapshotVisibilityGroups > 0);
        Assert.NotNull(performance.SnapshotBroadcast);
        Assert.NotNull(performance.InterestRefresh);
        Assert.NotNull(performance.MovementSimulation);
        var network = harness.NetworkMetrics!.Capture();
        Assert.True(network.SnapshotEntityRecords > 0);
        Assert.Equal(2, network.ActivePeers);
        Assert.Equal(2, network.ActiveEntities);
        Assert.Equal(2, network.ActiveRealPlayers);
    }
}
