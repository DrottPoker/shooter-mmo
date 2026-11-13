using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using LiteNetLib;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using NUnit.Framework;
using static Mmorpg.Client.Tests.EditMode.RealtimeHubMessages;

namespace Mmorpg.Client.Tests.EditMode
{
    // Pins how RealtimeSimulationClient joins, leaves, and ends a session, through a fake
    // transport: the state after each step, the events, their order, and the frame they fire on.
    // These pin the hub as it is, including rules that look odd; a change here is a behavior
    // change of the client.
    public sealed class RealtimeHubConnectionTests
    {
        private RealtimeHubHarness harness;

        [SetUp]
        public void SetUp()
        {
            harness = new RealtimeHubHarness();
        }

        [TearDown]
        public void TearDown()
        {
            harness.Dispose();
        }

        [Test]
        public void AJoinStartsTheTransportAndConnectsToTheTrimmedEndpoint()
        {
            harness.Transport.ReportedRoundTripTime = 40;

            Assert.That(harness.BeginJoin(RealtimeHubHarness.Placement(host: " 127.0.0.1 ")), Is.True);

            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Connecting));
            Assert.That(harness.Timeline, Is.EqualTo(new[] { "Start", "Connect" }));
            Assert.That(
                harness.Transport.DisconnectTimeoutMilliseconds,
                Is.EqualTo(ClientSession.RealtimeTimeoutSeconds * 1000));
            Assert.That(
                harness.Transport.ConnectedEndpoint,
                Is.EqualTo("127.0.0.1:27015 " + RealtimeProtocol.ConnectionKey));
            Assert.That((harness.Hub.ConnectedHost, harness.Hub.ConnectedPort), Is.EqualTo(("127.0.0.1", 27015)));
            Assert.That(harness.Transport.Sent, Is.Empty);
            Assert.That(harness.Hub.RoundTripTimeMilliseconds, Is.Zero, "No round trip before the connection.");
        }

        [Test]
        public void TheConnectionSendsTheTrimmedJoinTicketOnTheReliableControlChannel()
        {
            harness.Transport.ReportedRoundTripTime = 40;
            harness.BeginJoin(RealtimeHubHarness.Placement(joinTicket: " ticket-1 "));
            harness.Transport.QueueConnected();

            harness.Frame();

            var request = RealtimeProtocol.EncodeJoinRequest("ticket-1");
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Joining));
            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[] { "Start", "Connect", "frame", "Send JoinRequest on 0 ReliableOrdered" }));
            Assert.That(harness.Transport.Sent.Single().Packet, Is.EqualTo(request));
            Assert.That(
                (harness.Hub.RealtimePacketsSent, harness.Hub.RealtimeBytesSent),
                Is.EqualTo((1L, (long)request.Length)));
            Assert.That(harness.Hub.RoundTripTimeMilliseconds, Is.EqualTo(40));
            Assert.That(harness.StepJoin(), Is.True, "The join waits for its answer.");
        }

        [Test]
        public void AConnectionOutsideTheConnectingStateIsIgnored()
        {
            harness.BeginJoinAndConnect();
            harness.Clear();
            harness.Transport.QueueConnected();

            harness.Frame();

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame" }));
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Joining));
        }

        [Test]
        public void AnAcceptedJoinRaisesSessionJoinedInItsFrameAndCompletesTheJoinOnTheNextStep()
        {
            harness.BeginJoinAndConnect();
            harness.Queue(RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(1, 10)));
            harness.Frame();
            Assert.That(harness.Hub.RealtimePacketsReceived, Is.EqualTo(1), "A snapshot before the join is counted.");
            harness.Clear();
            var seenAtJoin = new List<(RealtimeConnectionState, bool, bool)>();
            harness.Hub.SessionJoined += () => seenAtJoin.Add((
                harness.Hub.State,
                harness.Hub.MovementSession != null,
                harness.Hub.ShardRules != null));

            harness.Deliver(RealtimeProtocol.EncodeJoinAccepted(RealtimeHubHarness.JoinAccepted()));

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "SessionJoined" }));
            Assert.That(seenAtJoin, Is.EqualTo(new[] { (RealtimeConnectionState.Joined, true, true) }));
            Assert.That(harness.Hub.IsJoined, Is.True);
            Assert.That(harness.JoinedSession, Is.Null, "The routine reports the join on its next step.");
            Assert.That(harness.StepJoin(), Is.False);
            var session = harness.JoinedSession;
            Assert.That(
                new[]
                {
                    session.simulationSessionId, session.accountId, session.characterId, session.characterName,
                    session.shardId, session.worldId, session.workerId, session.workerRuntimeId,
                    session.joinedAt, session.sessionExpiresAt
                },
                Is.EqualTo(new[]
                {
                    SessionId.ToString("D"), AccountId.ToString("D"), CharacterId.ToString("D"), CharacterName,
                    ShardId, WorldId, "worker-1", "runtime-1",
                    "2026-11-13T10:00:00.0000000Z", "2026-11-13T22:00:00.0000000Z"
                }));
            Assert.That(session.isReconnect, Is.False);
            Assert.That(harness.Hub.MovementSession.ControlledEntityId, Is.EqualTo(ControlledEntityId));
            Assert.That(harness.Hub.MovementSession.CharacterId, Is.EqualTo(CharacterId.ToString("D")));
            Assert.That(harness.Hub.CarryItemStateRevision, Is.Zero);
            Assert.That(harness.Hub.ShardRules.PlayerVersusPlayer, Is.False);
            Assert.That(harness.Hub.AdminState, Is.Null);
            Assert.That(
                new[]
                {
                    harness.Hub.RealtimePacketsSent, harness.Hub.RealtimeBytesSent,
                    harness.Hub.RealtimePacketsReceived, harness.Hub.RealtimeBytesReceived
                },
                Is.All.Zero,
                "The join resets the counters, so neither the join request nor the acceptance counts.");
        }

        // A join of a character whose body still stands in the world comes back as a reconnect.
        // The hub joins it like any session; the flag reaches only the joined session, and it
        // comes from the worker's acceptance, never from the placement.
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void AReconnectIsJoinedLikeAFreshSessionAndOnlyMarksTheSession(
            bool acceptedAsReconnect,
            bool placedAsReconnect)
        {
            var placement = RealtimeHubHarness.Placement();
            placement.isReconnect = placedAsReconnect;
            Assert.That(harness.BeginJoin(placement), Is.True);
            harness.Transport.QueueConnected();
            harness.Frame();
            harness.Clear();

            harness.Deliver(RealtimeProtocol.EncodeJoinAccepted(RealtimeHubHarness.JoinAccepted(acceptedAsReconnect)));
            Assert.That(harness.StepJoin(), Is.False);

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "SessionJoined" }));
            Assert.That(harness.JoinedSession.isReconnect, Is.EqualTo(acceptedAsReconnect));
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Joined));
        }

        [Test]
        public void AReconnectAfterALostConnectionStartsTheTransportAgainWithEmptyRegistries()
        {
            JoinWithPresence();
            harness.Transport.QueueDisconnected(DisconnectReason.Timeout);
            harness.Frame();
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT connection_lost" }));
            harness.Clear();

            harness.BeginJoinAndConnect();
            harness.Deliver(RealtimeProtocol.EncodeJoinAccepted(RealtimeHubHarness.JoinAccepted(true)));
            Assert.That(harness.StepJoin(), Is.False);

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "Start", "Connect", "frame", "Send JoinRequest on 0 ReliableOrdered", "frame", "SessionJoined"
                }));
            Assert.That(harness.JoinedSession.isReconnect, Is.True);
            Assert.That(harness.Hub.SpawnedEntityCount, Is.Zero);
            Assert.That(harness.Hub.SpawnedWorldActors, Is.Empty);
        }

        [TestCase("another character", "placement_mismatch")]
        [TestCase("another simulation revision", "placement_mismatch")]
        [TestCase("a session id that is no GUID", "invalid_join_response")]
        [TestCase("a collision revision this client lacks", "invalid_movement_configuration")]
        public void AJoinAcceptanceThatTheClientCannotUseFailsTheJoin(string acceptance, string code)
        {
            var placement = RealtimeHubHarness.Placement();
            var accepted = RealtimeHubHarness.JoinAccepted();
            switch (acceptance)
            {
                case "another character":
                    accepted = JoinAccepted(
                        GameSimulationCompatibility.Revision,
                        RealtimeHubHarness.BakedCollisionRevision,
                        characterId: G(9).ToString("D"));
                    break;
                case "another simulation revision":
                    accepted = JoinAccepted("another-simulation", RealtimeHubHarness.BakedCollisionRevision);
                    break;
                case "a session id that is no GUID":
                    accepted = JoinAccepted(
                        GameSimulationCompatibility.Revision,
                        RealtimeHubHarness.BakedCollisionRevision,
                        simulationSessionId: "session-1");
                    break;
                default:
                    placement.endpoint.collisionRevision = "another-collision";
                    accepted = JoinAccepted(GameSimulationCompatibility.Revision, "another-collision");
                    break;
            }

            Assert.That(harness.BeginJoin(placement), Is.True);
            harness.Transport.QueueConnected();
            harness.Frame();
            harness.Clear();

            harness.Deliver(RealtimeProtocol.EncodeJoinAccepted(accepted));

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "Stop" }));
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.Hub.MovementSession, Is.Null);
            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(harness.JoinedSession, Is.Null);
            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Protocol, code)));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT " + code }));
        }

        [Test]
        public void AJoinRejectionFailsTheJoinWithTheServerCode()
        {
            harness.BeginJoinAndConnect();
            harness.Clear();

            harness.Deliver(RealtimeProtocol.EncodeJoinRejected("join_ticket_expired", "The join ticket expired."));

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "Stop" }));
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code, harness.JoinError.Message),
                Is.EqualTo((RealtimeClientErrorKind.Server, "join_ticket_expired", "The join ticket expired.")));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "SIMULATION join_ticket_expired" }));
        }

        // Before the transport connected, even the join's own answers arrive in the wrong state.
        [TestCase(RealtimeMessageType.JoinAccepted, "unexpected_join_response")]
        [TestCase(RealtimeMessageType.JoinRejected, "unexpected_join_rejection")]
        [TestCase(RealtimeMessageType.EntitySpawn, "unexpected_entity_spawn")]
        public void AMessageBeforeTheTransportConnectedFailsTheJoin(RealtimeMessageType type, string code)
        {
            Assert.That(harness.BeginJoin(), Is.True);
            harness.Clear();
            var packet = type == RealtimeMessageType.JoinAccepted
                ? RealtimeProtocol.EncodeJoinAccepted(RealtimeHubHarness.JoinAccepted())
                : type == RealtimeMessageType.JoinRejected
                    ? RealtimeProtocol.EncodeJoinRejected("join_ticket_expired", "The join ticket expired.")
                    : RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId));

            harness.Deliver(packet);

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "Stop" }));
            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Protocol, code)));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT " + code }));
        }

        [TestCase("no placement", "invalid_join_parameters")]
        [TestCase("a placement without a ticket", "invalid_join_parameters")]
        [TestCase("an expired ticket", "join_ticket_expired")]
        [TestCase("another protocol version", "incompatible_protocol")]
        [TestCase("another simulation revision", "incompatible_simulation")]
        public void AJoinThatCannotStartFailsBeforeTheTransportStarts(string placementCase, string code)
        {
            var placement = RealtimeHubHarness.Placement();
            switch (placementCase)
            {
                case "no placement":
                    placement = null;
                    break;
                case "a placement without a ticket":
                    placement.joinTicket = " ";
                    break;
                case "an expired ticket":
                    placement.expiresAt = DateTime.UtcNow.AddMinutes(-1).ToString("O");
                    break;
                case "another protocol version":
                    placement.endpoint.protocolVersion = RealtimeProtocol.Version - 1;
                    break;
                default:
                    placement.endpoint.simulationRevision = "another-simulation";
                    break;
            }

            Assert.That(harness.BeginJoin(placement), Is.False);

            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Validation, code)));
            Assert.That(harness.Timeline, Is.Empty);
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT " + code }));
        }

        [Test]
        public void ASecondJoinWhileTheFirstRunsIsBusyAndLeavesTheFirstAlone()
        {
            Assert.That(harness.BeginJoin(), Is.True);
            RealtimeClientError busy = null;

            var second = harness.Hub.Join(RealtimeHubHarness.Placement(), _ => { }, error => busy = error);

            Assert.That(second.MoveNext(), Is.False);
            Assert.That((busy.Kind, busy.Code), Is.EqualTo((RealtimeClientErrorKind.Busy, "realtime_busy")));
            Assert.That(harness.Timeline, Is.EqualTo(new[] { "Start", "Connect" }));
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Connecting));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT realtime_busy" }));
        }

        [Test]
        public void ATransportThatCannotStartFailsTheJoin()
        {
            harness.Transport.StartSucceeds = false;

            Assert.That(harness.BeginJoin(), Is.False);

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "Start" }));
            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Network, "udp_start_failed")));
            Assert.That((harness.Hub.ConnectedHost, harness.Hub.ConnectedPort), Is.EqualTo((string.Empty, 0)));
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT udp_start_failed" }));
        }

        // A throwing start fails as a connect failure, not as a start failure.
        [Test]
        public void AThrowingTransportStartFailsTheJoinAsAConnectFailure()
        {
            harness.Transport.StartException = new InvalidOperationException("The socket is gone.");

            Assert.That(harness.BeginJoin(), Is.False);

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "Start" }));
            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Network, "udp_connect_failed")));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT udp_connect_failed" }));
        }

        [Test]
        public void AThrowingConnectStopsTheTransportAndFailsTheJoin()
        {
            harness.Transport.ConnectException = new InvalidOperationException("The host is unknown.");

            Assert.That(harness.BeginJoin(), Is.False);

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "Start", "Connect", "Stop" }));
            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Network, "udp_connect_failed")));
            Assert.That((harness.Hub.ConnectedHost, harness.Hub.ConnectedPort), Is.EqualTo((string.Empty, 0)));
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT udp_connect_failed" }));
        }

        // The deadline is set when the transport starts; the connection does not move it.
        [Test]
        public void AJoinTimesOutAtTheDeadlineThatItsTransportStartSet()
        {
            harness.Now = 100f;
            Assert.That(harness.BeginJoin(), Is.True);
            harness.Now = 100f + RealtimeHubHarness.TimeoutSeconds - 0.5f;
            harness.Transport.QueueConnected();
            harness.Frame();
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Joining));
            harness.Clear();

            harness.Now = 100f + RealtimeHubHarness.TimeoutSeconds;
            harness.Frame();

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "Stop" }));
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Timeout, "realtime_timeout")));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT realtime_timeout" }));
        }

        [Test]
        public void ALostConnectionDuringTheJoinFailsItWithTheTransportReason()
        {
            harness.BeginJoinAndConnect();
            harness.Clear();
            harness.Transport.QueueDisconnected(DisconnectReason.RemoteConnectionClose);

            harness.Frame();

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "Stop" }));
            Assert.That(harness.Hub.RoundTripTimeMilliseconds, Is.Zero);
            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code, harness.JoinError.Message),
                Is.EqualTo((
                    RealtimeClientErrorKind.Network,
                    "connection_lost",
                    "The realtime connection closed: RemoteConnectionClose.")));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT connection_lost" }));
        }

        [Test]
        public void ASocketErrorDuringTheJoinFailsIt()
        {
            Assert.That(harness.BeginJoin(), Is.True);
            harness.Clear();
            harness.Transport.QueueNetworkError(SocketError.ConnectionReset);

            harness.Frame();

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "Stop" }));
            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code, harness.JoinError.Message),
                Is.EqualTo((RealtimeClientErrorKind.Network, "udp_socket_error", "UDP transport error: ConnectionReset.")));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT udp_socket_error" }));
        }

        [Test]
        public void AServerDisconnectDuringTheJoinFailsItWithTheServerCode()
        {
            harness.BeginJoinAndConnect();
            harness.Clear();

            harness.Deliver(RealtimeProtocol.EncodeServerDisconnect("server_shutdown", "The worker stops."));

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "Stop" }));
            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Server, "server_shutdown")));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "SIMULATION server_shutdown" }));
        }

        // The leave request leaves at once, outside a frame. BodySeconds, how long the body stays
        // in the world, is decoded and not used.
        [TestCase((ushort)0)]
        [TestCase((ushort)20)]
        [TestCase((ushort)3600)]
        public void AnAcceptedLeaveEndsTheSessionInItsFrameWithoutAnUnexpectedDisconnect(ushort bodySeconds)
        {
            JoinWithPresence();

            Assert.That(harness.BeginLeave(" " + SessionId.ToString("D") + " "), Is.True);

            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Leaving));
            Assert.That(harness.Timeline, Is.EqualTo(new[] { "Send LeaveRequest on 0 ReliableOrdered" }));
            Assert.That(
                harness.Transport.Sent.Single().Packet,
                Is.EqualTo(RealtimeProtocol.EncodeLeaveRequest(SessionId.ToString("D"))));
            harness.Clear();

            harness.Deliver(RealtimeProtocol.EncodeLeaveAccepted(bodySeconds));

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "frame",
                    "EntityDespawned 11 left_shard",
                    "WorldActorDespawned 21 left_shard",
                    "Stop",
                    "SessionEnded null"
                }));
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.Hub.MovementSession, Is.Null);
            Assert.That(harness.Hub.ShardRules, Is.Null);
            Assert.That(harness.Hub.CarryItemStateRevision, Is.EqualTo(-1));
            Assert.That(harness.StepLeave(), Is.False);
            Assert.That(harness.LeaveSucceeded, Is.True);
            Assert.That(harness.LeaveError, Is.Null);
        }

        [Test]
        public void ALeaveRejectionKeepsTheSessionJoined()
        {
            JoinWithPresence();
            Assert.That(harness.BeginLeave(), Is.True);
            harness.Clear();

            harness.Deliver(RealtimeProtocol.EncodeLeaveRejected("leave_not_joined", "No simulation session is active."));

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame" }));
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Joined));
            Assert.That(harness.Hub.MovementSession, Is.Not.Null);
            Assert.That(harness.Hub.SpawnedEntityCount, Is.EqualTo(1));
            Assert.That(harness.Transport.IsStarted, Is.True);
            Assert.That(harness.StepLeave(), Is.False);
            Assert.That(harness.LeaveSucceeded, Is.False);
            Assert.That(
                (harness.LeaveError.Kind, harness.LeaveError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Server, "leave_not_joined")));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "SIMULATION leave_not_joined" }));
        }

        // Surprising but pinned: what the Leaving state dropped stays dropped when the leave is
        // rejected, so the joined session goes on without that presence.
        [Test]
        public void ALeaveRejectionDoesNotBringBackWhatTheLeavingStateDropped()
        {
            harness.JoinSession();
            Assert.That(harness.BeginLeave(), Is.True);
            harness.Deliver(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)));
            harness.Deliver(RealtimeProtocol.EncodeLeaveRejected("leave_not_joined", "No simulation session is active."));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "SIMULATION leave_not_joined" }));
            harness.Clear();

            harness.Deliver(RealtimeProtocol.EncodeCombatantState(CombatantState(PlayerEntityId)));

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "frame",
                    "Stop",
                    "UnexpectedlyDisconnected combatant_state_without_spawn",
                    "SessionEnded combatant_state_without_spawn"
                }));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT combatant_state_without_spawn" }));
        }

        // Unlike a failure while joined, a leave that times out raises no UnexpectedlyDisconnected.
        [Test]
        public void ALeaveTimesOutAtItsDeadlineAndEndsTheSession()
        {
            JoinWithPresence();
            harness.Now = 200f;
            Assert.That(harness.BeginLeave(), Is.True);
            harness.Clear();
            harness.Now = 200f + RealtimeHubHarness.TimeoutSeconds - 0.5f;
            harness.Frame();
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Leaving));

            harness.Now = 200f + RealtimeHubHarness.TimeoutSeconds;
            harness.Frame();

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "frame",
                    "frame",
                    "EntityDespawned 11 realtime_timeout",
                    "WorldActorDespawned 21 realtime_timeout",
                    "Stop",
                    "SessionEnded realtime_timeout"
                }));
            Assert.That(harness.StepLeave(), Is.False);
            Assert.That(
                (harness.LeaveError.Kind, harness.LeaveError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Timeout, "realtime_timeout")));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT realtime_timeout" }));
        }

        [Test]
        public void ALeaveOutsideAJoinedSessionIsRefused()
        {
            Assert.That(harness.BeginLeave(), Is.False);

            Assert.That(
                (harness.LeaveError.Kind, harness.LeaveError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Validation, "not_in_simulation")));
            Assert.That(harness.Timeline, Is.Empty);
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT not_in_simulation" }));
        }

        [Test]
        public void ALeaveWithoutASessionIdIsRefusedAndKeepsTheSession()
        {
            harness.JoinSession();

            Assert.That(harness.BeginLeave("  "), Is.False);

            Assert.That(
                (harness.LeaveError.Kind, harness.LeaveError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Validation, "missing_simulation_session")));
            Assert.That(harness.Timeline, Is.Empty);
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Joined));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT missing_simulation_session" }));
        }

        [Test]
        public void AServerDisconnectWhileLeavingFailsTheLeaveWithoutAnUnexpectedDisconnect()
        {
            JoinWithPresence();
            Assert.That(harness.BeginLeave(), Is.True);
            harness.Clear();

            harness.Deliver(RealtimeProtocol.EncodeServerDisconnect("server_shutdown", "The worker stops."));

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "frame",
                    "EntityDespawned 11 server_shutdown",
                    "WorldActorDespawned 21 server_shutdown",
                    "Stop",
                    "SessionEnded server_shutdown"
                }));
            Assert.That(harness.StepLeave(), Is.False);
            Assert.That(
                (harness.LeaveError.Kind, harness.LeaveError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Server, "server_shutdown")));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "SIMULATION server_shutdown" }));
        }

        // A failure that the frame's poll finds clears the registries at once, with their despawn
        // events; the session events follow at the end of the same frame, after the transport stops.
        [TestCase("a server disconnect", "server_shutdown", "SIMULATION")]
        [TestCase("a lost connection", "connection_lost", "CLIENT")]
        [TestCase("a socket error", "udp_socket_error", "CLIENT")]
        [TestCase("an unreadable packet", "invalid_packet", "CLIENT")]
        public void AFailureWhileJoinedEndsTheSessionInTheFrameThatFoundIt(
            string failure,
            string code,
            string logCategory)
        {
            JoinWithPresence();

            EndSession(failure);

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "frame",
                    "EntityDespawned 11 " + code,
                    "WorldActorDespawned 21 " + code,
                    "Stop",
                    "UnexpectedlyDisconnected " + code,
                    "SessionEnded " + code
                }));
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.LastPayload("SessionEnded"), Is.SameAs(harness.LastPayload("UnexpectedlyDisconnected")));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { logCategory + " " + code }));
        }

        // What arrives after a failure in the same poll is counted and dropped, so the first
        // failure reason is the one that ends the session.
        [Test]
        public void LaterEventsInThePollThatEndedTheSessionAreDropped()
        {
            JoinWithPresence();
            harness.Queue(RealtimeProtocol.EncodeJoinRejected("join_ticket_expired", "The join ticket expired."));
            harness.Queue(RealtimeProtocol.EncodeServerDisconnect("server_shutdown", "The worker stops."));
            harness.Transport.QueueNetworkError(SocketError.ConnectionReset);
            harness.Transport.QueueDisconnected(DisconnectReason.Timeout);

            harness.Frame();

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "frame",
                    "EntityDespawned 11 unexpected_join_rejection",
                    "WorldActorDespawned 21 unexpected_join_rejection",
                    "Stop",
                    "UnexpectedlyDisconnected unexpected_join_rejection",
                    "SessionEnded unexpected_join_rejection"
                }));
            Assert.That(harness.Hub.RealtimePacketsReceived, Is.EqualTo(1), "The failure reset the counters.");
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT unexpected_join_rejection" }));
        }

        // A failure that a feature reports outside the frame ends the session at once, but the
        // next frame still polls the running transport, counts and drops what arrived, then stops
        // the transport and raises the session events.
        [Test]
        public void AClientFailureOutsideAFrameDefersTheSessionEventsToTheNextFrame()
        {
            JoinWithPresence();

            harness.Hub.DisconnectForClientFailure("combat_state_conflict", "The combat state conflicts.");

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "EntityDespawned 11 combat_state_conflict",
                    "WorldActorDespawned 21 combat_state_conflict"
                }));
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.Transport.IsStarted, Is.True);
            harness.Clear();
            harness.Queue(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(OtherPlayerEntityId)));

            harness.Frame();

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "frame",
                    "Stop",
                    "UnexpectedlyDisconnected combat_state_conflict",
                    "SessionEnded combat_state_conflict"
                }));
            var error = (RealtimeClientError)harness.LastPayload("SessionEnded");
            Assert.That(error.Kind, Is.EqualTo(RealtimeClientErrorKind.Protocol));
            Assert.That(harness.Hub.RealtimePacketsReceived, Is.EqualTo(1));
            Assert.That(harness.Hub.SpawnedEntityCount, Is.Zero);
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT combat_state_conflict" }));
        }

        [Test]
        public void AClientFailureWithoutAConnectionDoesNothing()
        {
            harness.Hub.DisconnectForClientFailure("combat_state_conflict", "The combat state conflicts.");
            harness.Frame();

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame" }));
        }

        [Test]
        public void AbortEndsAJoinedSessionAtOnceWithoutAnUnexpectedDisconnect()
        {
            JoinWithPresence();

            harness.Hub.Abort();
            harness.Frame();

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "EntityDespawned 11 client_aborted",
                    "WorldActorDespawned 21 client_aborted",
                    "Stop",
                    "SessionEnded null",
                    "frame"
                }));
            Assert.That(harness.Hub.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
        }

        // The join routine finds no error of the hub and reports its own.
        [Test]
        public void AbortDuringAJoinEndsTheJoinWithAClosedConnection()
        {
            harness.BeginJoinAndConnect();
            harness.Clear();

            harness.Hub.Abort();

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "Stop" }));
            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Network, "connection_closed")));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT connection_closed" }));
        }

        // Surprising but pinned: Abort clears the session events that a failure in the same
        // frame deferred, so neither UnexpectedlyDisconnected nor SessionEnded ever fires.
        [Test]
        public void AbortAfterAFailureBeforeTheNextFrameDropsTheDeferredSessionEvents()
        {
            JoinWithPresence();
            harness.Hub.DisconnectForClientFailure("combat_state_conflict", "The combat state conflicts.");

            harness.Hub.Abort();
            harness.Frame();

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "EntityDespawned 11 combat_state_conflict",
                    "WorldActorDespawned 21 combat_state_conflict",
                    "Stop",
                    "frame"
                }));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT combat_state_conflict" }));
        }

        // Surprising but pinned: a join that starts after a failure, before the frame that stops
        // the failed transport, starts the still running transport, which refuses, and stops it.
        // The join fails, UnexpectedlyDisconnected of the failure is dropped, SessionEnded is not.
        [Test]
        public void AJoinBeforeTheFrameAfterAFailureFailsAndDropsTheUnexpectedDisconnect()
        {
            JoinWithPresence();
            harness.Hub.DisconnectForClientFailure("combat_state_conflict", "The combat state conflicts.");
            harness.Clear();

            Assert.That(harness.BeginJoin(), Is.False);
            harness.Frame();

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "Start", "Stop", "frame", "SessionEnded combat_state_conflict" }));
            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Network, "udp_start_failed")));
            Assert.That(
                harness.TakeFailures(),
                Is.EqualTo(new[] { "CLIENT combat_state_conflict", "CLIENT udp_start_failed" }));
        }

        // Surprising but pinned: what a despawn subscriber reads of the hub depends on where the
        // session ended. Some paths clear the registries before they leave the state, others after.
        [TestCase("a lost connection", RealtimeConnectionState.Disconnected, false)]
        [TestCase("an unreadable packet", RealtimeConnectionState.Joined, true)]
        [TestCase("a server disconnect", RealtimeConnectionState.Joined, true)]
        [TestCase("a client failure", RealtimeConnectionState.Joined, true)]
        [TestCase("an accepted leave", RealtimeConnectionState.Leaving, true)]
        [TestCase("an abort", RealtimeConnectionState.Disconnected, false)]
        public void DespawnsAtTheEndOfASessionSeeTheStateOfWhereItEnded(
            string ending,
            RealtimeConnectionState state,
            bool hasMovementSession)
        {
            JoinWithPresence();
            var seen = new List<(RealtimeConnectionState, bool)>();
            harness.Hub.EntityDespawned += _ => seen.Add((harness.Hub.State, harness.Hub.MovementSession != null));
            harness.Hub.WorldActorDespawned += _ => seen.Add((harness.Hub.State, harness.Hub.MovementSession != null));

            EndSession(ending);

            Assert.That(seen, Is.EqualTo(new[] { (state, hasMovementSession), (state, hasMovementSession) }));
            harness.TakeFailures();
        }

        [TestCase("a lost connection")]
        [TestCase("a server disconnect")]
        [TestCase("an accepted leave")]
        [TestCase("an abort")]
        public void TheRegistriesAreEmptyWhenSessionEndedFires(string ending)
        {
            JoinWithPresence();
            var seen = new List<(int, int, bool)>();
            harness.Hub.SessionEnded += error => seen.Add((
                harness.Hub.SpawnedEntityCount,
                harness.Hub.SpawnedWorldActors.Count,
                harness.Hub.TryGetAppearance(PlayerEntityId, out _)));

            EndSession(ending);

            Assert.That(seen, Is.EqualTo(new[] { (0, 0, false) }));
            harness.TakeFailures();
        }

        // Joins with one spawned player and one spawned world actor, then clears the timeline.
        private void JoinWithPresence()
        {
            harness.JoinSession();
            harness.Queue(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)));
            harness.Queue(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn()));
            harness.Frame();
            Assert.That(harness.Hub.SpawnedEntityCount, Is.EqualTo(1));
            harness.Clear();
        }

        private void EndSession(string ending)
        {
            switch (ending)
            {
                case "a server disconnect":
                    harness.Deliver(RealtimeProtocol.EncodeServerDisconnect("server_shutdown", "The worker stops."));
                    break;
                case "a lost connection":
                    harness.Transport.QueueDisconnected(DisconnectReason.Timeout);
                    harness.Frame();
                    break;
                case "a socket error":
                    harness.Transport.QueueNetworkError(SocketError.ConnectionReset);
                    harness.Frame();
                    break;
                case "an unreadable packet":
                    // Three bytes of a valid packet are too short for its header.
                    var packet = RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(OtherPlayerEntityId));
                    harness.Deliver(packet.Take(3).ToArray(), RealtimeProtocol.ControlChannel, DeliveryMethod.ReliableOrdered);
                    break;
                case "a client failure":
                    harness.Hub.DisconnectForClientFailure("combat_state_conflict", "The combat state conflicts.");
                    harness.Frame();
                    break;
                case "an accepted leave":
                    Assert.That(harness.BeginLeave(), Is.True);
                    harness.Deliver(RealtimeProtocol.EncodeLeaveAccepted(20));
                    break;
                case "an abort":
                    harness.Hub.Abort();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ending), ending, null);
            }
        }
    }
}
