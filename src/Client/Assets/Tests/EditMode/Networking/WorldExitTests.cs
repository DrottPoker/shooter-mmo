using System.Collections;
using System.Collections.Generic;
using LiteNetLib;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // Every way out of a World leaves the client session's World by the exact simulation session,
    // on the hub of the harness. The tests run outside a World scene, so no exit loads a scene.
    public sealed class WorldExitTests
    {
        private RealtimeHubHarness harness;
        private ClientSession session;
        private WorldExit exit;
        private int worldsLeft;

        [SetUp]
        public void SetUp()
        {
            harness = new RealtimeHubHarness();
            session = new ClientSession(RealtimeHubHarness.Settings);
            exit = new WorldExit(session, harness.Hub.Session, new ClientSessionRecovery(session, harness.Hub.Session));
            worldsLeft = 0;
            session.WorldLeft += () => worldsLeft++;
        }

        [TearDown]
        public void TearDown()
        {
            exit.Dispose();
            harness.Dispose();
        }

        [Test]
        public void ALostConnectionLeavesTheWorldOfItsSession()
        {
            harness.JoinSession();
            session.EnterWorld(harness.JoinedSession);
            harness.Transport.QueueDisconnected(DisconnectReason.Timeout);

            harness.Frame();

            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(worldsLeft, Is.EqualTo(1));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT connection_lost" }));
        }

        // A World that a scene change abandoned is left gracefully. The release names its own
        // session, so a newer World that the client entered meanwhile stays.
        [Test]
        public void AReleaseLeavesGracefullyAndNeverClearsANewerWorld()
        {
            harness.JoinSession();
            var abandoned = harness.JoinedSession;
            session.EnterWorld(abandoned);
            var release = Routine(exit.Release(abandoned));

            Assert.That(Step(release), Is.True);
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Leaving));
            Assert.That(
                harness.Transport.Sent[harness.Transport.Sent.Count - 1].Packet,
                Is.EqualTo(RealtimeProtocol.EncodeLeaveRequest(abandoned.simulationSessionId)));
            var newer = World("c0000000-0000-4000-8000-0000000000aa");
            session.EnterWorld(newer);

            harness.Deliver(RealtimeProtocol.EncodeLeaveAccepted(20));

            Assert.That(Step(release), Is.False);
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(session.ActiveSimulationSession, Is.SameAs(newer));
            Assert.That(worldsLeft, Is.Zero);
        }

        // A release names its session for the connection too: a connection joined to another
        // session stays.
        [Test]
        public void AReleaseLeavesAConnectionOfAnotherSessionAlone()
        {
            harness.JoinSession();
            var world = World("c0000000-0000-4000-8000-0000000000cc");
            session.EnterWorld(world);

            Assert.That(Step(Routine(exit.Release(world))), Is.False);

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

            Assert.That(Step(Routine(exit.Release(world))), Is.False);

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
            var release = Routine(exit.Release(world));
            Assert.That(Step(release), Is.True);

            harness.Deliver(RealtimeProtocol.EncodeLeaveRejected("leave_not_joined", "No simulation session is active."));

            Assert.That(Step(release), Is.False);
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.Transport.IsStarted, Is.False);
            Assert.That(session.ActiveSimulationSession, Is.Null);
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "SIMULATION leave_not_joined" }));
        }

        private static Stack<IEnumerator> Routine(IEnumerator routine)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            return stack;
        }

        // Runs a routine to its next wait as Unity does, with a nested routine at once; false once
        // it ended.
        private static bool Step(Stack<IEnumerator> routine)
        {
            while (routine.Count > 0)
            {
                var current = routine.Peek();
                if (!current.MoveNext())
                {
                    routine.Pop();
                    continue;
                }

                if (current.Current is IEnumerator nested)
                {
                    routine.Push(nested);
                    continue;
                }

                return true;
            }

            return false;
        }

        private static ActiveSimulationSessionResponse World(string simulationSessionId)
        {
            return new ActiveSimulationSessionResponse
            {
                simulationSessionId = simulationSessionId,
                shardId = RealtimeHubMessages.ShardId,
                worldId = RealtimeHubMessages.WorldId
            };
        }
    }
}
