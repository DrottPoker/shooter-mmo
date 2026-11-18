using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// What a connection's reliable queue has room for, in entities that may enter its view: the mark is
// half of the limit that disconnects a peer, an entity costs 2 packets, and a burst is the most.
public sealed class ReliableQueueEntryBudgetTests
{
    [Fact]
    public void TheMarkIsHalfOfTheLimitAndTheLeastLimitHoldsOneBurstUnderIt()
    {
        Assert.Equal(512, ReliableQueueEntryBudget.HighWaterOf(1_024));
        Assert.Equal(
            2 * ReliableQueueEntryBudget.PacketsPerEntry * SimulationInterestManager.MaximumEnteredPerRefresh,
            ReliableQueueEntryBudget.MinimumQueueLimit);
        Assert.Equal(
            SimulationInterestManager.MaximumEnteredPerRefresh,
            ReliableQueueEntryBudget.RoomFor(0, ReliableQueueEntryBudget.HighWaterOf(ReliableQueueEntryBudget.MinimumQueueLimit)));
    }

    [Theory]
    [InlineData(0, 512, 128)]
    [InlineData(256, 512, 128)]
    [InlineData(257, 512, 127)]
    [InlineData(300, 512, 106)]
    [InlineData(511, 512, 0)]
    [InlineData(512, 512, 0)]
    [InlineData(900, 512, 0)]
    [InlineData(0, 100, 50)]
    public void TheRoomIsWhatTheMarkLeavesAtTwoPacketsAnEntryAndNeverMoreThanABurst(int queued, int mark, int expected)
    {
        Assert.Equal(expected, ReliableQueueEntryBudget.RoomFor(queued, mark));
    }

    // The room is read from the peer's queues of every reliable channel together, the number that
    // the peer supervisor judges, and a connection without a peer has no queue to fill.
    [Fact]
    public void TheRoomOfAConnectionFollowsTheQueuesOfItsPeer()
    {
        using var fixture = new RealtimeLoopFixture();
        var player = fixture.ConnectPlayer(1);
        var budget = new ReliableQueueEntryBudget(fixture.Registry, ReliableQueueEntryBudget.HighWaterOf(1_024));
        Assert.Equal(128, budget.EntriesAllowed(1));

        player.Peer.QueuedReliablePackets[RealtimeProtocol.WorldChannel] = 200;
        player.Peer.QueuedReliablePackets[RealtimeProtocol.OwnerChannel] = 112;
        Assert.Equal(100, budget.EntriesAllowed(1));

        player.Peer.QueuedReliablePackets[RealtimeProtocol.WorldChannel] = 600;
        Assert.Equal(0, budget.EntriesAllowed(1));

        Assert.Equal(128, budget.EntriesAllowed(99));
    }
}
