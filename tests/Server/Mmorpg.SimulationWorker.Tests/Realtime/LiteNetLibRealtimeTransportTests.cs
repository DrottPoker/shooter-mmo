using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

public sealed class LiteNetLibRealtimeTransportTests
{
    [Fact]
    public void PacketPoolCoversOneBroadcastOfTheDefaultBudget()
    {
        // 1000 owner packets plus 38 MiB/s at 15 snapshots per second in 512-byte shares.
        Assert.Equal(6_188, LiteNetLibRealtimeTransport.PacketPoolSizeFor(1_000, 39_845_888 / 15));

        // 200 connections that all see each other queue 200 owner packets and 200 times nine
        // remote chunks, which the pool must hold.
        Assert.True(LiteNetLibRealtimeTransport.PacketPoolSizeFor(200, 39_845_888 / 15) >= 2_000);
    }

    [Fact]
    public void PacketPoolStaysWithinItsBounds()
    {
        Assert.Equal(1_000, LiteNetLibRealtimeTransport.PacketPoolSizeFor(1, 1_024));
        Assert.Equal(65_536, LiteNetLibRealtimeTransport.PacketPoolSizeFor(20_000, int.MaxValue));
    }
}
