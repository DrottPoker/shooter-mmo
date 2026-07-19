using ShooterMmo.GameProtocol;
using SimulationWorker.Realtime;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class SnapshotPacketCacheTests
{
    [Fact]
    public void EqualVisibilitySetsReuseEncodedPackets()
    {
        var cache = new SnapshotPacketCache();
        cache.Reset();
        for (ulong entityId = 1; entityId <= 25; entityId++)
        {
            cache.Add(CreateSnapshot(entityId));
        }

        var firstVisibility = Enumerable.Range(1, 25)
            .Select(value => (ulong)value)
            .ToArray();
        var equalVisibility = firstVisibility.ToArray();
        var first = cache.GetOrCreate(firstVisibility, 7, 11, int.MaxValue, 9_500);
        var reused = cache.GetOrCreate(equalVisibility, 7, 11, int.MaxValue, 9_500);

        Assert.Same(first, reused);
        Assert.Equal(1, cache.VisibilityGroupCount);
        Assert.Equal(2, cache.EncodedPacketCount);
        Assert.Equal([24, 1], first.EntityCounts);
        Assert.Equal(first.Packets.Sum(packet => packet.Length), first.TotalBytes);
        Assert.All(first.Packets, packet =>
        {
            Assert.True(
                RealtimeProtocol.TryDecodeSimulationSnapshot(
                    packet,
                    out var decoded,
                    out var error),
                error);
            Assert.Equal(7u, decoded.SnapshotSequence);
            Assert.Equal(11u, decoded.ServerTick);
            Assert.Equal(2, decoded.ChunkCount);
        });
    }

    [Fact]
    public void DifferentVisibilitySetsReceiveIndependentPacketBatches()
    {
        var cache = new SnapshotPacketCache();
        cache.Reset();
        cache.Add(CreateSnapshot(1));
        cache.Add(CreateSnapshot(2));

        var first = cache.GetOrCreate([1, 2], 1, 1, int.MaxValue, 9_500);
        var second = cache.GetOrCreate([2, 3], 1, 1, int.MaxValue, 9_500);

        Assert.NotSame(first, second);
        Assert.Equal(2, cache.VisibilityGroupCount);
        Assert.Equal(2, cache.EncodedPacketCount);
        Assert.Equal(2, first.EntityCounts[0]);
        Assert.Equal(1, second.EntityCounts[0]);
    }

    private static RealtimeEntitySnapshot CreateSnapshot(ulong entityId)
    {
        return new RealtimeEntitySnapshot(
            entityId,
            (uint)entityId,
            new RealtimePlayerState(
                entityId,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                true,
                false));
    }
}
