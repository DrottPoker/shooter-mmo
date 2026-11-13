using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // The reconnect on the hub of the harness and a GameService whose join tickets the test
    // answers. A frame runs as Unity runs one: the hub's Update, the bootstrap's Update with the
    // reconnect's Tick, then the coroutines. The tests run outside a World scene, so no exit loads
    // a scene; the World counts as shown unless a test says otherwise.
    public sealed class WorldReconnectTests
    {
        private const string SessionToken = "account-token";
        private static readonly string LostSessionId = RealtimeHubMessages.SessionId.ToString("D");
        private static readonly string RestoredSessionId = RealtimeHubMessages.G(1101).ToString("D");

        private readonly List<TestRoutine> routines = new List<TestRoutine>();
        private readonly List<WorldReconnectState> states = new List<WorldReconnectState>();
        private RealtimeHubHarness harness;
        private ClientSession session;
        private ClientNotices notices;
        private FakeJoinTickets tickets;
        private WorldExit exit;
        private WorldReconnect reconnect;
        private bool worldShown;
        private int worldsEntered;
        private int worldsLeft;

        [SetUp]
        public void SetUp()
        {
            // NUnit runs every test of the fixture on one instance.
            routines.Clear();
            states.Clear();
            worldsEntered = 0;
            worldsLeft = 0;
            harness = new RealtimeHubHarness();
            session = new ClientSession(RealtimeHubHarness.Settings);
            notices = new ClientNotices(() => harness.Now);
            tickets = new FakeJoinTickets();
            var recovery = new ClientSessionRecovery(session, harness.Hub.Session, notices);
            exit = new WorldExit(session, harness.Hub.Session, recovery, notices);
            worldShown = true;
            reconnect = new WorldReconnect(
                session,
                harness.Hub.Session,
                new WorldJoin(session, tickets, harness.Hub.Session),
                exit,
                recovery,
                () => harness.Now,
                () => worldShown,
                routine => Run(routine));
            reconnect.Changed += () => states.Add(reconnect.State);

            session.SignIn(new AuthResponse { accountId = "account-1", username = "hub_hero", sessionToken = SessionToken });
            session.Select(
                new CharacterResponse { id = RealtimeHubMessages.CharacterId.ToString("D"), name = RealtimeHubMessages.CharacterName },
                new ShardResponse { id = RealtimeHubMessages.ShardId, worldId = RealtimeHubMessages.WorldId });
            harness.JoinSession();
            session.EnterWorld(harness.JoinedSession);
            session.WorldEntered += () => worldsEntered++;
            session.WorldLeft += () => worldsLeft++;
        }

        [TearDown]
        public void TearDown()
        {
            reconnect.Dispose();
            harness.Dispose();
        }

        // Rule 1 and 5: a line that dropped keeps the World. One attempt asks for a ticket for the
        // same character and shard and joins with it; the World continues with the new session,
        // the notice goes, and the lost session ended once for every listener before the join.
        [Test]
        public void ALostLineJoinsTheSameCharacterAgainAndKeepsTheWorld()
        {
            LoseLine();

            Assert.That(reconnect.IsReconnecting, Is.True, "The notice over the World shows.");
            Assert.That(reconnect.SecondsLeft, Is.EqualTo(ClientSettings.DefaultReconnectWindowSeconds));
            Assert.That(session.ActiveSimulationSession.simulationSessionId, Is.EqualTo(LostSessionId), "The World stays.");
            Assert.That(
                harness.Timeline.Where(entry => entry.StartsWith("Unexpectedly", StringComparison.Ordinal)
                    || entry.StartsWith("SessionEnded", StringComparison.Ordinal)),
                Is.EqualTo(new[] { "UnexpectedlyDisconnected connection_lost", "SessionEnded connection_lost" }));
            Assert.That(tickets.Requests, Has.Count.EqualTo(1));
            Assert.That(
                (tickets.Last.SessionToken, tickets.Last.ShardId, tickets.Last.CharacterId),
                Is.EqualTo((SessionToken, RealtimeHubMessages.ShardId, RealtimeHubMessages.CharacterId.ToString("D"))));

            harness.Now += 0.5f;
            Restore(tickets.Last);

            Assert.That(reconnect.State, Is.EqualTo(WorldReconnectState.Idle));
            Assert.That(reconnect.SecondsLeft, Is.Zero, "The notice goes.");
            Assert.That(
                states,
                Is.EqualTo(new[]
                {
                    WorldReconnectState.Pausing,
                    WorldReconnectState.RequestingTicket,
                    WorldReconnectState.Joining,
                    WorldReconnectState.Idle
                }));
            Assert.That(session.ActiveSimulationSession.simulationSessionId, Is.EqualTo(RestoredSessionId));
            Assert.That(session.ActiveSimulationSession.isReconnect, Is.True);
            Assert.That((worldsEntered, worldsLeft), Is.EqualTo((1, 0)), "The client never left its World.");
            Assert.That(harness.Hub.Session.IsJoined, Is.True);
            Assert.That(harness.Hub.Movement.MovementSession, Is.Not.Null);
            Assert.That(notices.PendingCode, Is.Empty);
        }

        // Rule 3: nothing that needs the worker sends while the connection is restored.
        [Test]
        public void NothingIsSentWhileTheConnectionIsRestored()
        {
            LoseLine();
            harness.Clear();

            Assert.That(
                harness.Hub.Movement.TrySendMovementInputs(
                    new[] { new PlayerMovementInput(1, 1, null) },
                    null),
                Is.False);
            Assert.That(harness.Hub.Items.TrySendItemOperation(RealtimeHubMessages.ItemIntent()), Is.False);
            Assert.That(harness.Hub.Combat.TrySendCombatIntent(RealtimeHubMessages.CombatIntent()), Is.False);
            Assert.That(harness.Transport.Sent, Is.Empty);
        }

        // Rule 4 and 6: every attempt that can get better by waiting repeats after a short pause,
        // one at a time, until the window from the loss ends. A join that still runs then is
        // closed, the World is left, and CharacterSelect says that the connection was not restored.
        [Test]
        public void AttemptsRepeatUntilTheWindowEndsAndThenTheWorldIsLeft()
        {
            LoseLine();
            tickets.Last.Refuse(new ApiError(ApiErrorKind.Network, 0, "network_error", "Cannot connect.", string.Empty));
            Frame();
            Assert.That(reconnect.State, Is.EqualTo(WorldReconnectState.Pausing));
            Frame();
            Assert.That(tickets.Requests, Has.Count.EqualTo(1), "The next attempt waits for its pause.");

            harness.Now += WorldReconnect.PauseSeconds;
            Frame();
            Assert.That(tickets.Requests, Has.Count.EqualTo(2));
            tickets.Last.Grant(RealtimeHubHarness.Placement("ticket-2"));
            Frame();
            Assert.That(reconnect.State, Is.EqualTo(WorldReconnectState.Joining));
            Assert.That(harness.Transport.IsStarted, Is.True);

            // The worker never answers, so the join times out, and the next attempt joins again.
            harness.Now += RealtimeHubHarness.TimeoutSeconds;
            Frame();
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT realtime_timeout" }));
            Assert.That(reconnect.State, Is.EqualTo(WorldReconnectState.Pausing));
            harness.Now += WorldReconnect.PauseSeconds;
            Frame();
            tickets.Last.Grant(RealtimeHubHarness.Placement("ticket-3"));
            Frame();
            Assert.That(tickets.Requests, Has.Count.EqualTo(3));
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Connecting));
            Assert.That(reconnect.SecondsLeft, Is.EqualTo(8));

            harness.Now = 100f + ClientSettings.DefaultReconnectWindowSeconds;
            Frame();

            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT connection_closed" }));
            Assert.That(reconnect.State, Is.EqualTo(WorldReconnectState.Idle));
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.Transport.IsStarted, Is.False, "The join that still ran is closed.");
            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(worldsLeft, Is.EqualTo(1));
            Assert.That(notices.PendingCode, Is.EqualTo(WorldExit.ReconnectFailedCode));
        }

        // Rule 6: an answer that waiting does not change ends the window at once.
        [Test]
        public void ARefusedTicketGivesUpAtOnce()
        {
            LoseLine();

            tickets.Last.Refuse(new ApiError(ApiErrorKind.Http, 409, "character_already_active", "Active elsewhere.", "c-1"));
            Frame();

            Assert.That(reconnect.State, Is.EqualTo(WorldReconnectState.Idle));
            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(notices.PendingCode, Is.EqualTo(WorldExit.ReconnectFailedCode));
            harness.Now += 5f;
            Frame();
            Assert.That(tickets.Requests, Has.Count.EqualTo(1), "No attempt follows.");
        }

        [Test]
        public void ARefusedJoinGivesUpAtOnce()
        {
            LoseLine();
            tickets.Last.Grant(RealtimeHubHarness.Placement("ticket-2"));
            Frame();
            harness.Transport.QueueConnected();
            Frame();

            harness.Queue(RealtimeProtocol.EncodeJoinRejected("invalid_ticket", "The ticket is not valid."));
            Frame();

            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "SIMULATION invalid_ticket" }));
            Assert.That(reconnect.State, Is.EqualTo(WorldReconnectState.Idle));
            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(notices.PendingCode, Is.EqualTo(WorldExit.ReconnectFailedCode));
        }

        // A 401 for the ticket is the forced sign-out, to LoginMenu with the text of its code.
        [Test]
        public void AnUnauthorizedTicketSignsOut()
        {
            LoseLine();

            tickets.Last.Refuse(new ApiError(ApiErrorKind.Http, 401, "invalid_session_token", "Expired.", string.Empty));
            Frame();

            Assert.That(harness.TakeFailures(), Has.Length.EqualTo(1));
            Assert.That(reconnect.State, Is.EqualTo(WorldReconnectState.Idle));
            Assert.That(session.IsAuthenticated, Is.False);
            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(notices.PendingCode, Is.EqualTo("invalid_session_token"));
        }

        // Rule 4: an attempt that is over never acts, so the late ticket of the last window
        // cannot start a join in the next one.
        [Test]
        public void ALateAnswerOfAnEarlierAttemptIsIgnored()
        {
            LoseLine();
            var late = tickets.Last;
            harness.Now = 100f + ClientSettings.DefaultReconnectWindowSeconds;
            Frame();
            Assert.That(session.ActiveSimulationSession, Is.Null, "The first window gave up.");

            harness.JoinSession(Accepted(RealtimeHubMessages.G(1102).ToString("D"), false));
            session.EnterWorld(harness.JoinedSession);
            LoseLine();
            var current = tickets.Last;
            Assert.That(current, Is.Not.SameAs(late));

            late.Grant(RealtimeHubHarness.Placement("late-ticket"));
            Frame();
            Assert.That(harness.Transport.IsStarted, Is.False, "The late ticket starts no join.");
            Assert.That(reconnect.State, Is.EqualTo(WorldReconnectState.RequestingTicket));

            Restore(current);
            Assert.That(session.ActiveSimulationSession.simulationSessionId, Is.EqualTo(RestoredSessionId));
        }

        // A loss right after a restored connection starts a window of its own.
        [Test]
        public void ASecondLossRightAfterASuccessStartsANewWindow()
        {
            LoseLine();
            harness.Now += 15f;
            Restore(tickets.Last);

            LoseLine();

            Assert.That(reconnect.IsReconnecting, Is.True);
            Assert.That(reconnect.SecondsLeft, Is.EqualTo(ClientSettings.DefaultReconnectWindowSeconds));
            Assert.That(tickets.Requests, Has.Count.EqualTo(2));
            Assert.That(session.ActiveSimulationSession.simulationSessionId, Is.EqualTo(RestoredSessionId));
        }

        // Rule 2: an end that the server decided leaves the World at once with its text.
        [Test]
        public void AServerDisconnectLeavesTheWorldWithItsText()
        {
            harness.Deliver(RealtimeProtocol.EncodeServerDisconnect("session_revoked", "The session ended."));
            reconnect.Tick();

            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "SIMULATION session_revoked" }));
            Assert.That(reconnect.IsReconnecting, Is.False);
            Assert.That(tickets.Requests, Is.Empty);
            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(notices.PendingCode, Is.EqualTo("session_revoked"));
        }

        // A new login of the account signs out, to LoginMenu with its text.
        [Test]
        public void AReplacedAccountSessionSignsOut()
        {
            harness.Deliver(RealtimeProtocol.EncodeServerDisconnect("account_session_replaced", "Logged in elsewhere."));

            Assert.That(harness.TakeFailures(), Has.Length.EqualTo(2));
            Assert.That(reconnect.IsReconnecting, Is.False);
            Assert.That(session.IsAuthenticated, Is.False);
            Assert.That(notices.PendingCode, Is.EqualTo("account_session_replaced"));
        }

        // Without a World on screen there is nothing to keep, so the end goes the way it did.
        [Test]
        public void ALostLineOutsideAShownWorldLeavesTheWorld()
        {
            worldShown = false;

            harness.Transport.QueueDisconnected(DisconnectReason.Timeout);
            Frame();

            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT connection_lost" }));
            Assert.That(reconnect.IsReconnecting, Is.False);
            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(notices.PendingCode, Is.EqualTo("connection_lost"));
        }

        // Leave Shard while the connection is restored never names the lost session on another
        // connection: the join that runs is closed and the World is left at once.
        [Test]
        public void LeavingTheWorldStopsTheReconnect()
        {
            LoseLine();
            tickets.Last.Grant(RealtimeHubHarness.Placement("ticket-2"));
            Frame();
            harness.Transport.QueueConnected();
            Frame();
            harness.Clear();
            var leftWorld = false;

            var leave = new TestRoutine(exit.Leave(_ => Assert.Fail("No leave is sent."), () => leftWorld = true));
            Assert.That(leave.Step(), Is.False);
            Frame();

            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT connection_closed" }));
            Assert.That(leftWorld, Is.True);
            Assert.That(harness.Transport.Sent, Is.Empty);
            Assert.That(harness.Transport.IsStarted, Is.False);
            Assert.That(reconnect.State, Is.EqualTo(WorldReconnectState.Idle));
            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(notices.PendingCode, Is.Empty);
            Assert.That(notices.TryGetBody(out _, out _), Is.False);
        }

        // Drops the line of the joined session and runs the frame that finds it and the first
        // attempt, which waits for its ticket.
        private void LoseLine()
        {
            harness.Transport.QueueDisconnected(DisconnectReason.Timeout);
            Frame();
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT connection_lost" }));
            Assert.That(reconnect.State, Is.EqualTo(WorldReconnectState.RequestingTicket));
        }

        // Grants the ticket of the attempt and lets the worker accept its join as a reconnect.
        private void Restore(FakeJoinTickets.Request request)
        {
            request.Grant(RealtimeHubHarness.Placement("ticket-restore"));
            Frame();
            Assert.That(reconnect.State, Is.EqualTo(WorldReconnectState.Joining));
            harness.Transport.QueueConnected();
            Frame();
            harness.Queue(RealtimeProtocol.EncodeJoinAccepted(Accepted(RestoredSessionId, true)));
            Frame();
            Assert.That(harness.TakeFailures(), Is.Empty);
        }

        private static RealtimeJoinAccepted Accepted(string simulationSessionId, bool isReconnect)
        {
            return RealtimeHubMessages.JoinAccepted(
                GameSimulationCompatibility.Revision,
                RealtimeHubHarness.BakedCollisionRevision,
                isReconnect,
                null,
                simulationSessionId);
        }

        private void Frame()
        {
            harness.Frame();
            var started = routines.ToArray();
            reconnect.Tick();
            foreach (var routine in started)
            {
                if (!routine.Step())
                {
                    routines.Remove(routine);
                }
            }
        }

        // StartCoroutine runs a routine to its first wait at once.
        private void Run(IEnumerator routine)
        {
            var started = new TestRoutine(routine);
            if (started.Step())
            {
                routines.Add(started);
            }
        }
    }
}
