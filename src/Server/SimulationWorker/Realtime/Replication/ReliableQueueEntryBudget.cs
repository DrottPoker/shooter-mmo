namespace Mmorpg.SimulationWorker.Realtime;

// How many entities a connection may take into its view now, as its reliable queue has room for
// them, which keeps the interest manager free of the transport. The count is the reliable packets
// that wait in the peer's queues, the number that the peer supervisor judges against
// Admission:MaxReliableQueuedPackets, and entries may fill the queue only up to a high-water mark.
//
// The mark is half of the limit: 512 of 1,024. An entry costs 2 packets, so the room is
// (mark - queued) / 2, at most one burst. A refresh that fills that room leaves the queue at the
// mark, and the other half of the limit is for everything else that is reliable at that time: the
// join's own messages, the combat events and far states of the same tick, and the answers to what
// the player does. A mark of three quarters would leave 256 packets, one burst, for all of it; a
// quarter would hold one burst under the mark and no more, so a client that is slightly behind
// would never get a whole one.
internal sealed class ReliableQueueEntryBudget(RealtimePeerRegistry peers, int highWater)
{
    // The reliable packets that one entity entering a view costs: its spawn and, for a combatant,
    // its combatant state.
    public const int PacketsPerEntry = 2;

    // The share of Admission:MaxReliableQueuedPackets that entries may fill, as a divisor: half.
    public const int HighWaterDivisor = 2;

    // The smallest Admission:MaxReliableQueuedPackets whose mark holds one whole burst of entries:
    // 512 for a mark of 256 packets, the 128 entities of a burst at 2 packets each.
    public const int MinimumQueueLimit =
        HighWaterDivisor * PacketsPerEntry * SimulationInterestManager.MaximumEnteredPerRefresh;

    // The mark for a queue limit.
    public static int HighWaterOf(int maxReliableQueuedPackets)
    {
        return maxReliableQueuedPackets / HighWaterDivisor;
    }

    // The entities that fit in the queue below the mark when it holds queued packets: none at or
    // over the mark, and never more than one burst.
    public static int RoomFor(int queued, int highWater)
    {
        return Math.Clamp(
            (highWater - queued) / PacketsPerEntry,
            0,
            SimulationInterestManager.MaximumEnteredPerRefresh);
    }

    // The entities that the connection's peer may take into its view now; a burst for a
    // connection without a peer, which has no queue to fill.
    public int EntriesAllowed(int connectionId)
    {
        if (!peers.TryGetContext(connectionId, out var context)
            || !peers.TryGetCurrentPeer(context, out var peer))
        {
            return SimulationInterestManager.MaximumEnteredPerRefresh;
        }

        return RoomFor(RealtimeReliableChannels.QueuedPackets(peer), highWater);
    }
}
