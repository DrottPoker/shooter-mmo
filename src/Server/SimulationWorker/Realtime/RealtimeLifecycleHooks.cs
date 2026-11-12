using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// The lifecycle hooks that features add during composition. The hooks of a player's way in, the
// placements, the joined hooks, and the spawn-sent hooks, run in registration order, so the order
// in which the composition creates the feature units that add them is the call order. The hooks
// of its way out, the connection-ended and the removed hooks, run in reverse registration order:
// teardown mirrors setup, so the last feature to join a player is the first to see it go, while
// the features it builds on still hold the player. The lists keep registration order.
// Loop-confined once composed.
internal sealed class RealtimeLifecycleHooks
{
    private readonly List<Func<ActiveSimulationSession, PlayerMovementState?>> playerPlacements = [];
    private readonly List<Action<RealtimePeerContext, PlayerSimulationEntity>> playerJoined = [];
    private readonly List<Action<RealtimePeerContext, ActiveSimulationSession, PlayerSimulationEntity>> connectionEnded = [];
    private readonly List<Action<RealtimePeerContext, ActiveSimulationSession, ulong>> playerRemoved = [];
    private readonly List<Action<IRealtimePeerConnection, ulong>> spawnSent = [];

    public IReadOnlyList<Func<ActiveSimulationSession, PlayerMovementState?>> PlayerPlacements => playerPlacements;

    public IReadOnlyList<Action<RealtimePeerContext, PlayerSimulationEntity>> PlayerJoined => playerJoined;

    public IReadOnlyList<Action<RealtimePeerContext, ActiveSimulationSession, PlayerSimulationEntity>> ConnectionEnded => connectionEnded;

    public IReadOnlyList<Action<RealtimePeerContext, ActiveSimulationSession, ulong>> PlayerRemoved => playerRemoved;

    public IReadOnlyList<Action<IRealtimePeerConnection, ulong>> SpawnSent => spawnSent;

    // Asked where the entity of a joining session starts, before the join decides whether it
    // creates the entity or a reconnect keeps it. The first placement that returns a state wins;
    // without one the player starts at the World spawn.
    public void AddPlayerPlacement(Func<ActiveSimulationSession, PlayerMovementState?> placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        playerPlacements.Add(placement);
    }

    // Hears every character that joined, after its accept was sent and before its baseline, also
    // a reconnect that resumes a lingering body.
    public void AddPlayerJoined(Action<RealtimePeerContext, PlayerSimulationEntity> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        playerJoined.Add(hook);
    }

    // Hears every player whose connection ended while its session goes on, right after its entity
    // became a lingering body without a peer. Runs in reverse registration order.
    public void AddConnectionEnded(Action<RealtimePeerContext, ActiveSimulationSession, PlayerSimulationEntity> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        connectionEnded.Add(hook);
    }

    // Hears every player entity that teardown removes, with the session it belonged to. Runs in
    // reverse registration order, so a feature reads the state of the features it builds on
    // before they let the player go.
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

    public PlayerMovementState? PlaceNewPlayer(ActiveSimulationSession session)
    {
        foreach (var placement in playerPlacements)
        {
            if (placement(session) is { } state)
            {
                return state;
            }
        }

        return null;
    }

    public void RaisePlayerJoined(RealtimePeerContext context, PlayerSimulationEntity entity)
    {
        foreach (var hook in playerJoined)
        {
            hook(context, entity);
        }
    }

    public void RaiseConnectionEnded(RealtimePeerContext context, ActiveSimulationSession session, PlayerSimulationEntity entity)
    {
        for (var index = connectionEnded.Count - 1; index >= 0; index--)
        {
            connectionEnded[index](context, session, entity);
        }
    }

    public void RaisePlayerRemoved(RealtimePeerContext context, ActiveSimulationSession session, ulong entityId)
    {
        for (var index = playerRemoved.Count - 1; index >= 0; index--)
        {
            playerRemoved[index](context, session, entityId);
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
