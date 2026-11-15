using System.Runtime.ExceptionServices;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// Where a new player entity starts: a position and a facing, on whose ground the join places it.
internal readonly record struct PlayerPlace(float X, float Y, float Z, float YawDegrees)
{
    public SimulationVector3 Position => new(X, Y, Z);

    public static PlayerPlace Spawn(MovementSpawnConfig spawn) => new(spawn.X, spawn.Y, spawn.Z, spawn.YawDegrees);
}

// The lifecycle hooks that features add during composition. The hooks of a player's way in, the
// placements, the joined hooks, and the spawn-sent hooks, run in registration order, so the order
// in which the composition creates the feature units that add them is the call order. The hooks
// of its way out, the connection-ended and the removed hooks, run in reverse registration order:
// teardown mirrors setup, so the last feature to join a player is the first to see it go, while
// the features it builds on still hold the player. The lists keep registration order.
// Loop-confined once composed.
internal sealed class RealtimeLifecycleHooks
{
    private readonly List<Func<ActiveSimulationSession, PlayerPlace?>> playerPlacements = [];
    private readonly List<Action<RealtimePeerContext, PlayerSimulationEntity>> playerJoined = [];
    private readonly List<Action<RealtimePeerContext, ActiveSimulationSession, PlayerSimulationEntity>> connectionEnded = [];
    private readonly List<Action<RealtimePeerContext, ActiveSimulationSession, ulong>> playerRemoved = [];
    private readonly List<Action<IRealtimePeerConnection, ulong>> spawnSent = [];

    public IReadOnlyList<Func<ActiveSimulationSession, PlayerPlace?>> PlayerPlacements => playerPlacements;

    public IReadOnlyList<Action<RealtimePeerContext, PlayerSimulationEntity>> PlayerJoined => playerJoined;

    public IReadOnlyList<Action<RealtimePeerContext, ActiveSimulationSession, PlayerSimulationEntity>> ConnectionEnded => connectionEnded;

    public IReadOnlyList<Action<RealtimePeerContext, ActiveSimulationSession, ulong>> PlayerRemoved => playerRemoved;

    public IReadOnlyList<Action<IRealtimePeerConnection, ulong>> SpawnSent => spawnSent;

    // Asked where the new entity of a joining session starts; a reconnect that keeps its entity
    // asks none. The first placement that returns a place wins; without one the player starts at
    // the World spawn. A join may ask again when it waited for the collision of the place.
    public void AddPlayerPlacement(Func<ActiveSimulationSession, PlayerPlace?> placement)
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

    public PlayerPlace? PlaceNewPlayer(ActiveSimulationSession session)
    {
        foreach (var placement in playerPlacements)
        {
            if (placement(session) is { } place)
            {
                return place;
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

    // Every hook runs, also after one of them threw: the entity is gone already, so a hook
    // that was skipped would leave its state behind for good. The first exception is thrown
    // again once all of them ran.
    public void RaisePlayerRemoved(RealtimePeerContext context, ActiveSimulationSession session, ulong entityId)
    {
        ExceptionDispatchInfo? failure = null;
        for (var index = playerRemoved.Count - 1; index >= 0; index--)
        {
            try
            {
                playerRemoved[index](context, session, entityId);
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        failure?.Throw();
    }

    public void RaiseSpawnSent(IRealtimePeerConnection peer, ulong entityId)
    {
        foreach (var hook in spawnSent)
        {
            hook(peer, entityId);
        }
    }
}
