using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// The lifecycle hooks that features add during composition. Each list runs on the loop in
// registration order, so the order in which the composition adds them is the call order.
// Loop-confined once composed.
internal sealed class RealtimeLifecycleHooks
{
    private readonly List<Action<RealtimePeerContext, PlayerSimulationEntity>> playerJoined = [];
    private readonly List<Action<RealtimePeerContext, ActiveSimulationSession, ulong>> playerRemoved = [];
    private readonly List<Action<IRealtimePeerConnection, ulong>> spawnSent = [];

    public IReadOnlyList<Action<RealtimePeerContext, PlayerSimulationEntity>> PlayerJoined => playerJoined;

    public IReadOnlyList<Action<RealtimePeerContext, ActiveSimulationSession, ulong>> PlayerRemoved => playerRemoved;

    public IReadOnlyList<Action<IRealtimePeerConnection, ulong>> SpawnSent => spawnSent;

    // Hears every character that joined, after its accept was sent and before its baseline.
    public void AddPlayerJoined(Action<RealtimePeerContext, PlayerSimulationEntity> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        playerJoined.Add(hook);
    }

    // Hears every player entity that teardown removes, with the session it belonged to.
    public void AddPlayerRemoved(Action<RealtimePeerContext, ActiveSimulationSession, ulong> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        playerRemoved.Add(hook);
    }

    // Hears every spawn the presence replicator sends, so state that must follow a spawn, such
    // as a combatant's health, reaches the same peer right after it.
    public void AddSpawnSent(Action<IRealtimePeerConnection, ulong> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        spawnSent.Add(hook);
    }

    public void RaisePlayerJoined(RealtimePeerContext context, PlayerSimulationEntity entity)
    {
        foreach (var hook in playerJoined)
        {
            hook(context, entity);
        }
    }

    public void RaisePlayerRemoved(RealtimePeerContext context, ActiveSimulationSession session, ulong entityId)
    {
        foreach (var hook in playerRemoved)
        {
            hook(context, session, entityId);
        }
    }

    public void RaiseSpawnSent(IRealtimePeerConnection peer, ulong entityId)
    {
        foreach (var hook in spawnSent)
        {
            hook(peer, entityId);
        }
    }
}
