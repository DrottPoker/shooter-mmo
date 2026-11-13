using System;
using System.Collections;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;

namespace Mmorpg.Client.Networking
{
    public enum WorldReconnectState
    {
        // No lost connection is being restored.
        Idle,

        // The short pause before the next attempt; the first attempt starts after none.
        Pausing,

        // An attempt asks GameService for a join ticket for the same character and shard.
        RequestingTicket,

        // An attempt joins the worker with that ticket.
        Joining
    }

    // What the notice over the World reads while a lost connection is restored.
    public interface IWorldReconnect
    {
        // Raised whenever the state changes.
        event Action Changed;

        WorldReconnectState State { get; }

        bool IsReconnecting { get; }

        // The whole seconds left of the window, rounded up; 0 while idle.
        int SecondsLeft { get; }
    }

    // The one owner of a lost connection. It listens to the realtime session's unexpected ends and
    // decides by ReconnectRules: an end that does not reconnect, or one outside a shown World,
    // leaves through WorldExit as before. A line that dropped keeps the World, and the client
    // joins again by itself: one attempt at a time asks GameService for a ticket for the same
    // character and shard and joins with it, with a short pause between attempts, until one
    // succeeds or the window from the loss ends. Success enters the World of the new session, so
    // the World scene goes on; the window's end and a definite refusal leave the lost World
    // through WorldExit, and a 401 signs out through the recovery. Leaving the World or the
    // account in any other way stops it. An attempt that is over never acts again, so a late
    // answer cannot complete a newer attempt. The bootstrap creates one, calls Tick every frame,
    // and runs its attempts as coroutines.
    public sealed class WorldReconnect : IWorldReconnect, IDisposable
    {
        public const float PauseSeconds = 1f;

        private readonly ClientSession clientSession;
        private readonly RealtimeSession realtimeSession;
        private readonly WorldJoin worldJoin;
        private readonly WorldExit worldExit;
        private readonly ClientSessionRecovery recovery;
        private readonly Func<float> clock;
        private readonly Func<bool> isWorldShown;
        private readonly Action<IEnumerator> run;
        private ActiveSimulationSessionResponse lostWorld;
        private float deadline;
        private float nextAttemptAt;
        // The attempt that may still act; every attempt that is over has a lower number.
        private int currentAttempt;
        private int attemptsInWindow;

        public WorldReconnect(
            ClientSession session,
            RealtimeSession realtime,
            WorldJoin join,
            WorldExit exit,
            ClientSessionRecovery sessionRecovery,
            Func<float> realtimeClock,
            Func<bool> worldShown,
            Action<IEnumerator> runRoutine)
        {
            clientSession = session ?? throw new ArgumentNullException(nameof(session));
            realtimeSession = realtime ?? throw new ArgumentNullException(nameof(realtime));
            worldJoin = join ?? throw new ArgumentNullException(nameof(join));
            worldExit = exit ?? throw new ArgumentNullException(nameof(exit));
            recovery = sessionRecovery ?? throw new ArgumentNullException(nameof(sessionRecovery));
            clock = realtimeClock ?? throw new ArgumentNullException(nameof(realtimeClock));
            isWorldShown = worldShown ?? throw new ArgumentNullException(nameof(worldShown));
            run = runRoutine ?? throw new ArgumentNullException(nameof(runRoutine));
            realtimeSession.UnexpectedlyDisconnected += OnUnexpectedlyDisconnected;
            clientSession.WorldLeft += OnWorldOrAccountLeft;
            clientSession.SignedOut += OnWorldOrAccountLeft;
            clientSession.SignedIn += OnWorldOrAccountLeft;
        }

        public event Action Changed;

        public WorldReconnectState State { get; private set; }

        public bool IsReconnecting
        {
            get { return State != WorldReconnectState.Idle; }
        }

        public int SecondsLeft
        {
            get
            {
                return IsReconnecting
                    ? Math.Max(0, (int)Math.Ceiling(deadline - clock()))
                    : 0;
            }
        }

        public void Dispose()
        {
            realtimeSession.UnexpectedlyDisconnected -= OnUnexpectedlyDisconnected;
            clientSession.WorldLeft -= OnWorldOrAccountLeft;
            clientSession.SignedOut -= OnWorldOrAccountLeft;
            clientSession.SignedIn -= OnWorldOrAccountLeft;
            currentAttempt++;
            lostWorld = null;
            State = WorldReconnectState.Idle;
        }

        // Ends the window when its time is up and starts the next attempt after its pause. A join
        // that the worker already accepted completes on this frame even at the deadline.
        public void Tick()
        {
            if (State == WorldReconnectState.Idle
                || (State == WorldReconnectState.Joining && realtimeSession.IsJoined))
            {
                return;
            }

            if (clock() >= deadline)
            {
                GiveUp("the " + clientSession.Settings.ReconnectWindowSeconds + " s to reconnect ended after "
                    + attemptsInWindow + " attempts");
                return;
            }

            if (State == WorldReconnectState.Pausing && clock() >= nextAttemptAt)
            {
                StartAttempt();
            }
        }

        private void OnUnexpectedlyDisconnected(RealtimeClientError error)
        {
            // A join that an attempt got accepted and lost again before it entered the World:
            // that attempt fails with the loss, and the window goes on.
            if (State != WorldReconnectState.Idle)
            {
                return;
            }

            var world = clientSession.ActiveSimulationSession;
            if (ReconnectRules.Classify(error) != RealtimeEnding.Reconnect
                || world == null
                || !clientSession.IsAuthenticated
                || !isWorldShown())
            {
                worldExit.LeaveEndedWorld(error);
                return;
            }

            lostWorld = world;
            deadline = clock() + clientSession.Settings.ReconnectWindowSeconds;
            nextAttemptAt = clock();
            attemptsInWindow = 0;
            ClientLog.Warning(
                ClientLogCategory.Client,
                "The connection of simulation session '" + world.simulationSessionId + "' was lost. Reconnecting for up to "
                + clientSession.Settings.ReconnectWindowSeconds + " s: " + error.ToDiagnostic());
            SetState(WorldReconnectState.Pausing);
        }

        // Leaving the World or the account in any way but this owner's stops the window, and a
        // join that an attempt runs is closed.
        private void OnWorldOrAccountLeft()
        {
            if (State == WorldReconnectState.Idle)
            {
                return;
            }

            ClientLog.Info(
                ClientLogCategory.Client,
                "The reconnect of simulation session '" + lostWorld.simulationSessionId
                + "' stopped because the client left its World.");
            Stop();
        }

        private void StartAttempt()
        {
            var attempt = ++currentAttempt;
            attemptsInWindow++;
            SetState(WorldReconnectState.RequestingTicket);
            run(AttemptRoutine(attempt, lostWorld));
        }

        private IEnumerator AttemptRoutine(int attempt, ActiveSimulationSessionResponse lost)
        {
            WorldJoinResult result = null;
            yield return worldJoin.Run(
                lost.characterId,
                lost.characterName,
                lost.shardId,
                ticket => AcceptTicket(attempt, lost, ticket),
                value => result = value);

            // An attempt that is over, by the window's end or by leaving the World, never acts.
            if (attempt != currentAttempt)
            {
                yield break;
            }

            Complete(lost, result);
        }

        // Only the current attempt joins, and only into the World that is on screen.
        private bool AcceptTicket(int attempt, ActiveSimulationSessionResponse lost, JoinShardResponse ticket)
        {
            if (attempt != currentAttempt)
            {
                return false;
            }

            if (!string.Equals(ticket.shard.worldId, lost.worldId, StringComparison.Ordinal))
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "The shard of the lost connection now runs World '" + ticket.shard.worldId + "' instead of '"
                    + lost.worldId + "'.");
                return false;
            }

            SetState(WorldReconnectState.Joining);
            return true;
        }

        private void Complete(ActiveSimulationSessionResponse lost, WorldJoinResult result)
        {
            switch (result.Status)
            {
                case WorldJoinStatus.Joined:
                    currentAttempt++;
                    lostWorld = null;
                    ClientLog.Info(
                        ClientLogCategory.Client,
                        "The connection of simulation session '" + lost.simulationSessionId + "' was restored as '"
                        + result.Session.simulationSessionId + "' after " + attemptsInWindow + " attempts"
                        + (result.Session.isReconnect ? ", resuming the character's body." : ", with a new entity."));
                    SetState(WorldReconnectState.Idle);
                    clientSession.EnterWorld(result.Session);
                    return;
                case WorldJoinStatus.TicketRefused:
                    var ticketFailure = ReconnectRules.ClassifyAttempt(result.TicketError);
                    if (ticketFailure == ReconnectAttemptFailure.SignOut)
                    {
                        Stop();
                        recovery.ReturnToLoginIfUnauthorized(result.TicketError);
                        return;
                    }

                    Fail(ticketFailure, "the join ticket was refused: " + result.TicketError.ToDiagnostic());
                    return;
                case WorldJoinStatus.JoinFailed:
                    Fail(
                        ReconnectRules.ClassifyAttempt(result.JoinError),
                        "the join failed: " + (result.JoinError != null ? result.JoinError.ToDiagnostic() : "no session"));
                    return;
                default:
                    Fail(ReconnectAttemptFailure.GiveUp, "the join ticket cannot reconnect: " + result.Status);
                    return;
            }
        }

        private void Fail(ReconnectAttemptFailure failure, string reason)
        {
            if (failure != ReconnectAttemptFailure.Retry)
            {
                GiveUp(reason);
                return;
            }

            ClientLog.Warning(
                ClientLogCategory.Client,
                "Reconnect attempt " + attemptsInWindow + " failed; trying again: " + reason);
            nextAttemptAt = clock() + PauseSeconds;
            SetState(WorldReconnectState.Pausing);
        }

        private void GiveUp(string reason)
        {
            var lost = lostWorld;
            Stop();
            ClientLog.Warning(
                ClientLogCategory.Client,
                "The connection of simulation session '" + lost.simulationSessionId
                + "' could not be restored: " + reason + ".");
            worldExit.LeaveLostWorld(lost.simulationSessionId);
        }

        // Ends the window: every attempt is over, and a join that one runs is closed.
        private void Stop()
        {
            currentAttempt++;
            lostWorld = null;
            if (realtimeSession.State != RealtimeConnectionState.Disconnected)
            {
                realtimeSession.Abort();
            }

            SetState(WorldReconnectState.Idle);
        }

        private void SetState(WorldReconnectState state)
        {
            if (State == state)
            {
                return;
            }

            State = state;
            Changed?.Invoke();
        }
    }
}
