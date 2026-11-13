using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // Every way out of a World leaves the client session's World by the exact simulation session,
    // on the hub of the harness, and tells the next entry screen why. The tests run outside a
    // World scene, so no exit loads a scene. How a lost connection reaches the exit is in
    // WorldReconnectTests.
    public sealed class WorldExitTests
    {
        private RealtimeHubHarness harness;
        private ClientSession session;
        private ClientNotices notices;
        private WorldExit exit;
        private int worldsLeft;

        [SetUp]
        public void SetUp()
        {
            harness = new RealtimeHubHarness();
            session = new ClientSession(RealtimeHubHarness.Settings);
            notices = new ClientNotices(() => harness.Now);
            exit = new WorldExit(
                session,
                harness.Hub.Session,
                new ClientSessionRecovery(session, harness.Hub.Session, notices),
                notices);
            worldsLeft = 0;
            session.WorldLeft += () => worldsLeft++;
        }

        [TearDown]
        public void TearDown()
        {
            harness.Dispose();
        }

        // An end that does not reconnect leaves the World of its session, and the next entry
        // screen shows the text of its code.
        [Test]
        public void AnEndedConnectionLeavesTheWorldOfItsSessionWithItsCode()
        {
            harness.JoinSession();
            session.EnterWorld(harness.JoinedSession);

            exit.LeaveEndedWorld(new RealtimeClientError(RealtimeClientErrorKind.Server, "session_revoked", "Ended."));

            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(worldsLeft, Is.EqualTo(1));
            Assert.That(notices.Take(), Is.EqualTo("session_revoked"));
            Assert.That(notices.Take(), Is.Empty, "A notice shows once.");
        }

        // A lost connection that the reconnect gave up on names its session, so a newer World
        // stays; the notice says that the connection was not restored.
        [Test]
        public void ALostWorldIsLeftOnlyByItsOwnSession()
        {
            var newer = World("c0000000-0000-4000-8000-0000000000aa");
            session.EnterWorld(newer);

            exit.LeaveLostWorld("c0000000-0000-4000-8000-0000000000bb");

            Assert.That(session.ActiveSimulationSession, Is.SameAs(newer));
            Assert.That(worldsLeft, Is.Zero);
            Assert.That(notices.PendingCode, Is.EqualTo(WorldExit.ReconnectFailedCode));
        }

        // Rule 7: an accepted leave says how long the body stays, which CharacterSelect counts down.
        [Test]
        public void ALeaveRemembersHowLongTheBodyStays()
        {
            harness.JoinSession();
            var world = harness.JoinedSession;
            session.EnterWorld(world);
            var left = false;
            var leave = new TestRoutine(exit.Leave(_ => Assert.Fail("The leave was accepted."), () => left = true));

            Assert.That(leave.Step(), Is.True);
            harness.Deliver(RealtimeProtocol.EncodeLeaveAccepted(20));
            Assert.That(leave.Step(), Is.False);

            Assert.That(left, Is.True);
            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(notices.TryGetBody(out var characterId, out var secondsLeft), Is.True);
            Assert.That((characterId, secondsLeft), Is.EqualTo((world.characterId, 20)));
            Assert.That(notices.PendingCode, Is.Empty, "A leave needs no notice.");
        }

        // A leave without a connection joined to the World's session, as while a lost connection
        // is restored, sends nothing and leaves the World at once.
        [Test]
        public void ALeaveWithoutAJoinedConnectionLeavesTheWorldAtOnce()
        {
            var world = World("c0000000-0000-4000-8000-0000000000dd");
            session.EnterWorld(world);
            var left = false;

            Assert.That(new TestRoutine(exit.Leave(_ => Assert.Fail("No leave is sent."), () => left = true)).Step(), Is.False);

            Assert.That(left, Is.True);
            Assert.That(harness.Transport.Sent, Is.Empty);
            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(notices.TryGetBody(out _, out _), Is.False);
        }

        // A World that a scene change abandoned is left gracefully. The release names its own
        // session, so a newer World that the client entered meanwhile stays.
        [Test]
        public void AReleaseLeavesGracefullyAndNeverClearsANewerWorld()
        {
            harness.JoinSession();
            var abandoned = harness.JoinedSession;
            session.EnterWorld(abandoned);
            var release = new TestRoutine(exit.Release(abandoned));

            Assert.That(release.Step(), Is.True);
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Leaving));
            Assert.That(
                harness.Transport.Sent[harness.Transport.Sent.Count - 1].Packet,
                Is.EqualTo(RealtimeProtocol.EncodeLeaveRequest(abandoned.simulationSessionId)));
            var newer = World("c0000000-0000-4000-8000-0000000000aa");
            session.EnterWorld(newer);

            harness.Deliver(RealtimeProtocol.EncodeLeaveAccepted(20));

            Assert.That(release.Step(), Is.False);
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(session.ActiveSimulationSession, Is.SameAs(newer));
            Assert.That(worldsLeft, Is.Zero);
            Assert.That(notices.TryGetBody(out var characterId, out var secondsLeft), Is.True);
            Assert.That((characterId, secondsLeft), Is.EqualTo((abandoned.characterId, 20)));
        }

        // A release names its session for the connection too: a connection joined to another
        // session stays.
        [Test]
        public void AReleaseLeavesAConnectionOfAnotherSessionAlone()
        {
            harness.JoinSession();
            var world = World("c0000000-0000-4000-8000-0000000000cc");
            session.EnterWorld(world);

            Assert.That(new TestRoutine(exit.Release(world)).Step(), Is.False);

            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joined));
            Assert.That(harness.Transport.Sent, Is.Empty);
            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(worldsLeft, Is.EqualTo(1));
        }

        [Test]
        public void AReleaseWithoutAJoinedConnectionOnlyLeavesTheWorld()
        {
            var world = World("c0000000-0000-4000-8000-0000000000bb");
            session.EnterWorld(world);

            Assert.That(new TestRoutine(exit.Release(world)).Step(), Is.False);

            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(worldsLeft, Is.EqualTo(1));
            Assert.That(harness.Transport.Sent, Is.Empty);
        }

        // A refused leave closes UDP, so the worker runs its disconnect cleanup.
        [Test]
        public void ARefusedReleaseClosesTheConnection()
        {
            harness.JoinSession();
            var world = harness.JoinedSession;
            session.EnterWorld(world);
            var release = new TestRoutine(exit.Release(world));
            Assert.That(release.Step(), Is.True);

            harness.Deliver(RealtimeProtocol.EncodeLeaveRejected("leave_not_joined", "No simulation session is active."));

            Assert.That(release.Step(), Is.False);
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.Transport.IsStarted, Is.False);
            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(notices.TryGetBody(out _, out _), Is.False);
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "SIMULATION leave_not_joined" }));
        }

        private static ActiveSimulationSessionResponse World(string simulationSessionId)
        {
            return new ActiveSimulationSessionResponse
            {
                simulationSessionId = simulationSessionId,
                characterId = RealtimeHubMessages.CharacterId.ToString("D"),
                shardId = RealtimeHubMessages.ShardId,
                worldId = RealtimeHubMessages.WorldId
            };
        }
    }
}
