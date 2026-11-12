using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// Where the answer to a player's operation goes once GameService answered. The operation
// commits at GameService whether or not the connection that sent it still exists, so what it
// committed belongs to the character while the worker still holds its session and its entity:
// with the peer that sent it, with the peer that a reconnect bound since, or with none while the
// character stands as a lingering body. A session that ended, or a character whose entity went,
// takes nothing more. Loop-confined.
internal sealed class OperationCompletionTargets(
    RealtimePeerRegistry peers,
    ConnectionEntityBindingRegistry connectionBindings,
    SimulationEntityRegistry entityRegistry,
    ActiveSimulationSessionStore sessionStore)
{
    // The target of an operation that the given peer started for the given session generation;
    // false when the session ended or the character's entity went.
    public bool TryResolve(
        RealtimePeerContext context,
        ActiveSimulationSession session,
        out OperationCompletionTarget target)
    {
        target = default;
        if (!sessionStore.TryGet(session.CharacterId, out var current)
            || current!.SimulationSessionId != session.SimulationSessionId
            || !sessionStore.IsCurrent(current, DateTime.UtcNow)
            || !entityRegistry.TryGetPlayerByCharacterId(session.CharacterId, out var entity)
            || entity!.Session.SimulationSessionId != session.SimulationSessionId)
        {
            return false;
        }

        IRealtimePeerConnection? requester = null;
        if (context.Session is { } requesterSession
            && requesterSession.SimulationSessionId == session.SimulationSessionId
            && SameToken(requesterSession, session)
            && peers.TryGetCurrentPeer(context, out var requesterPeer))
        {
            requester = requesterPeer;
        }

        RealtimePeerContext? ownerContext = null;
        IRealtimePeerConnection? owner = null;
        if (connectionBindings.TryGetConnectionId(entity.NetworkEntityId, out var connectionId)
            && peers.TryGetContext(connectionId, out var boundContext)
            && boundContext.Session?.SimulationSessionId == session.SimulationSessionId
            && peers.TryGetCurrentPeer(boundContext, out var boundPeer))
        {
            ownerContext = boundContext;
            owner = boundPeer;
        }

        target = new OperationCompletionTarget(
            current,
            entity,
            context,
            requester,
            ownerContext,
            owner,
            SameToken(current, session));
        return true;
    }

    private static bool SameToken(ActiveSimulationSession left, ActiveSimulationSession right)
    {
        return string.Equals(left.SimulationSessionToken, right.SimulationSessionToken, StringComparison.Ordinal);
    }
}

// The character that a completed operation belongs to. Session is the session's current
// generation on the worker, which a reconnect moved to a new token, and Entity the character's
// entity. Requester is the peer that sent the operation while it still plays that generation; the
// operation's own result goes only to it, since no other client knows the operation. Owner is
// the peer that plays the session now, which gets the owner updates, such as the item and quest
// log changes and the carry state; it is the requester, a reconnected peer, or none while the
// character is a lingering body. SameGeneration tells whether the operation ran with the
// session's current token: only such an operation's failure can end the session.
internal readonly record struct OperationCompletionTarget(
    ActiveSimulationSession Session,
    PlayerSimulationEntity Entity,
    RealtimePeerContext RequesterContext,
    IRealtimePeerConnection? Requester,
    RealtimePeerContext? OwnerContext,
    IRealtimePeerConnection? Owner,
    bool SameGeneration);
