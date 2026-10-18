using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Per-peer state of one UDP connection. Only the realtime loop reads or mutates it;
// off-loop code may read the immutable PeerId only.
internal sealed class RealtimePeerContext(
    int peerId,
    DateTime connectedAtUtc,
    UdpQuotaConfig quotaConfig)
{
    public const int MaximumQueuedAuthorityOperations = 8;
    public const int GracefulDisconnectDelayMilliseconds = 250;

    // Completions of work that belongs to no peer, such as a Mob's corpse, name this
    // context. It is never registered, so a failure never disconnects anyone.
    public static RealtimePeerContext System { get; } = new(-1, DateTime.MinValue, UdpQuotaConfig.Default);

    public int PeerId { get; } = peerId;

    public DateTime ConnectedAtUtc { get; } = connectedAtUtc;

    public UdpPeerQuota Quota { get; } = new(quotaConfig);

    public WorldInteractionIntentLimiter WorldInteractionIntentQuota { get; } = new();

    // Combat intents share one budget: 8 per second with a burst of 12.
    public TokenBucket CombatIntentQuota { get; } = new(8d, 12d);

    public bool JoinStarted { get; set; }

    public bool LeaveStarted { get; set; }

    public ActiveSimulationSession? Session { get; set; }

    public Queue<IRealtimeAuthorityOperation> AuthorityOperationQueue { get; } = new();

    public bool AuthorityOperationInFlight { get; set; }

    public string? CorpsePresenceSignature { get; set; }

    public DateTime? DisconnectAfterUtc { get; set; }

    // A rejected or finished peer keeps its connection briefly, so the reliable
    // message that explains the disconnect can still be delivered.
    public void ScheduleDisconnect(DateTime utcNow)
    {
        DisconnectAfterUtc = utcNow.AddMilliseconds(GracefulDisconnectDelayMilliseconds);
    }
}
