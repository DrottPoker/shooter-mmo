using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// Takes one player or one peer out of play after an unexpected exception that concerns only it,
// so the rest of the Shard goes on. The tick phases that walk players or recipients one by one
// call it from their per-player catch blocks. Loop-confined.
internal sealed class RealtimeFailureIsolation(
    ActiveSimulationSessionStore sessionStore,
    RealtimePeerSender sender,
    RealtimeLoopGuard guard,
    SessionAuthorityOperations sessionOperations,
    CharacterFinalStateStore finalStates)
{
    // What the client gets, as for a packet that failed in the dispatcher.
    public const string Code = "internal_error";
    public const string Message = "SimulationWorker could not process the request.";

    // The sessions of removed players whose entity has not gone yet.
    private readonly Dictionary<Guid, ActiveSimulationSession> awaitingRelease = [];

    // Releases a removed player's session once its entity is gone.
    public void AddHooks(RealtimeLifecycleHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        hooks.AddPlayerRemoved(OnPlayerRemoved);
    }

    // The player's own state failed, so the player cannot stay: its session is invalidated with
    // internal_error. The peer supervisor then disconnects its peer with that code and removes
    // the entity after this tick, and a lingering body goes in the body phase. Once the entity is
    // gone, the session is released in the background without a final state, since the state
    // may be broken, so the character is free at once and resumes from its last checkpoint.
    public void RemovePlayer(string stepName, PlayerSimulationEntity entity, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var session = entity.Session;
        guard.PlayerFailed(stepName, entity.NetworkEntityId, session.CharacterId, exception);
        if (sessionStore.Invalidate(
                session.CharacterId,
                session.SimulationSessionId,
                session.SimulationSessionToken,
                Code,
                Message))
        {
            awaitingRelease[session.SimulationSessionId] = session;
        }
    }

    // Building or sending the packets of one peer failed: only that peer is disconnected with
    // internal_error, the way the packet dispatcher does it. Its character stays as a body.
    public void DisconnectPeer(
        string stepName,
        IRealtimePeerConnection peer,
        RealtimePeerContext context,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(context);
        guard.PeerFailed(stepName, context.PeerId, exception);
        if (context.PendingDisconnect is not null)
        {
            return;
        }

        try
        {
            sender.RejectProtocol(peer, context, Code, Message);
        }
        catch (Exception)
        {
            // A peer whose rejection fails still goes, without the message and without a wait.
            context.BeginDisconnect(null, DateTime.UtcNow)?.MarkAcknowledged();
        }
    }

    private void OnPlayerRemoved(RealtimePeerContext context, ActiveSimulationSession session, ulong entityId)
    {
        if (awaitingRelease.Count == 0 || !awaitingRelease.Remove(session.SimulationSessionId, out var removed))
        {
            return;
        }

        // Persistence keeps no final state of a session that the store no longer holds; this
        // makes sure none goes with the release.
        finalStates.Remove(removed.CharacterId, removed.SimulationSessionId);
        sessionOperations.ReleaseDetachedInBackground(removed);
    }
}
