using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Per-peer state of one UDP connection. Only the realtime loop reads or mutates it;
// off-loop code may read the immutable PeerId only. Its quotas refill on the time provider's
// clock, the system's unless the replication benchmark gives it its own.
internal sealed class RealtimePeerContext(
    int peerId,
    DateTime connectedAtUtc,
    UdpQuotaConfig quotaConfig,
    TimeProvider? timeProvider = null)
{
    public const int MaximumQueuedAuthorityOperations = 8;

    // Completions of work that belongs to no peer, such as a Mob's corpse, name this
    // context. It is never registered, so a failure never disconnects anyone.
    public static RealtimePeerContext System { get; } = new(-1, DateTime.MinValue, UdpQuotaConfig.Default);

    public int PeerId { get; } = peerId;

    public DateTime ConnectedAtUtc { get; } = connectedAtUtc;

    public UdpPeerQuota Quota { get; } = new(quotaConfig, timeProvider);

    public WorldInteractionIntentLimiter WorldInteractionIntentQuota { get; } = new();

    // Combat intents share one budget: 8 per second with a burst of 12.
    public TokenBucket CombatIntentQuota { get; } = new(8d, 12d, timeProvider);

    public bool JoinStarted { get; set; }

    // The session the peer plays, from its join until its connection ends; null before the join
    // and once a leave, an invalidation, or a reconnect from another client let go of it.
    public ActiveSimulationSession? Session { get; set; }

    public Queue<IRealtimeAuthorityOperation> AuthorityOperationQueue { get; } = new();

    public bool AuthorityOperationInFlight { get; set; }

    // The peer that a reconnect moved this peer's session to while an operation still ran on
    // this peer's lane. Its lane opens when that operation ends.
    public RealtimePeerContext? LaneSuccessor { get; set; }

    // The corpse presence this peer was sent last.
    public CorpsePresenceSent CorpsePresence { get; } = new();

    // The disconnect that this peer waits for, from the moment the worker decided to end the
    // connection; null while the connection goes on.
    public RealtimePendingDisconnect? PendingDisconnect { get; private set; }

    // Starts the disconnect with the data it carries. A peer that already goes keeps its first
    // reason, as its client takes the first message that ends the session, and gets null.
    public RealtimePendingDisconnect? BeginDisconnect(byte[]? data, DateTime utcNow)
    {
        if (PendingDisconnect is not null)
        {
            return null;
        }

        PendingDisconnect = new RealtimePendingDisconnect(data, utcNow);
        return PendingDisconnect;
    }
}
