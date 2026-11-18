using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using LiteNetLib;
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

            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Connecting));
            Assert.That(harness.Timeline, Is.EqualTo(new[] { "Start", "Connect" }));
            Assert.That(
                harness.Transport.DisconnectTimeoutMilliseconds,
                Is.EqualTo(RealtimeHubHarness.Settings.RealtimeTimeoutSeconds * 1000));
            Assert.That(
                harness.Transport.ConnectedEndpoint,
                Is.EqualTo("127.0.0.1:27015 " + RealtimeProtocol.ConnectionKey));
            Assert.That((harness.Hub.Diagnostics.ConnectedHost, harness.Hub.Diagnostics.ConnectedPort), Is.EqualTo(("127.0.0.1", 27015)));
            Assert.That(harness.Transport.Sent, Is.Empty);
            Assert.That(harness.Hub.Diagnostics.RoundTripTimeMilliseconds, Is.Zero, "No round trip before the connection.");
        }

        // The join request carries the ticket id and a fresh nonce with the proof of the ticket's
        // key, never the key, and the transport got the keys of the datagrams before it connected.
        [Test]
        public void TheConnectionSendsTheJoinRequestWithTheTicketsProofOnTheReliableSessionChannel()
        {
            harness.Transport.ReportedRoundTripTime = 40;
            harness.BeginJoin(RealtimeHubHarness.Placement(joinTicket: "ticket-1"));
            harness.Transport.QueueConnected();

            harness.Frame();

            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joining));
            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[] { "Start", "Connect", "frame", "Send JoinRequest on 0 ReliableOrdered" }));
            var packet = harness.Transport.Sent.Single().Packet;
            Assert.That(RealtimeProtocol.TryDecodeJoinRequest(packet, out var request, out var error), Is.True, error);
            Assert.That(request.TicketId, Is.EqualTo(RealtimeHubHarness.TicketIdOf("ticket-1")));
            using (var keys = RealtimeTransportKeys.Derive(RealtimeHubHarness.TransportKeyOf("ticket-1")))
            {
                Assert.That(
                    RealtimeJoinProof.Verify(
                        keys.ProofKey,
                        RealtimeProtocol.Version,
                        request.TicketId,
                        RealtimeHubMessages.ShardId,
                        request.ClientNonce,
                        request.Proof),
                    Is.True);
                Assert.That(harness.Transport.ConnectedClientToWorkerKey, Is.EqualTo(keys.ClientToWorkerKey));
                Assert.That(harness.Transport.ConnectedWorkerToClientKey, Is.EqualTo(keys.WorkerToClientKey));
            }

            Assert.That(
                (harness.Hub.Diagnostics.RealtimePacketsSent, harness.Hub.Diagnostics.RealtimeBytesSent),
                Is.EqualTo((1L, (long)packet.Length)));
            Assert.That(harness.Hub.Diagnostics.RoundTripTimeMilliseconds, Is.EqualTo(40));
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
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joining));
        }

        [Test]
        public void AnAcceptedJoinRaisesSessionJoinedInItsFrameAndCompletesTheJoinOnTheNextStep()
        {
            harness.BeginJoinAndConnect();
            harness.Queue(RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(1, 10)));
            harness.Frame();
            Assert.That(harness.Hub.Diagnostics.RealtimePacketsReceived, Is.EqualTo(1), "A snapshot before the join is counted.");
            harness.Clear();
            var seenAtJoin = new List<(RealtimeConnectionState, bool, bool)>();
            harness.Hub.Session.SessionJoined += () => seenAtJoin.Add((
                harness.Hub.Session.State,
                harness.Hub.Movement.MovementSession != null,
                harness.Hub.Session.ShardRules != null));

            harness.Deliver(RealtimeProtocol.EncodeJoinAccepted(RealtimeHubHarness.JoinAccepted()));

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "SessionJoined" }));
            Assert.That(seenAtJoin, Is.EqualTo(new[] { (RealtimeConnectionState.Joined, true, true) }));
            Assert.That(harness.Hub.Session.IsJoined, Is.True);
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
            Assert.That(harness.Hub.Movement.MovementSession.ControlledEntityId, Is.EqualTo(ControlledEntityId));
            Assert.That(harness.Hub.Movement.MovementSession.CharacterId, Is.EqualTo(CharacterId.ToString("D")));
            Assert.That(harness.Hub.Items.CarryItemStateRevision, Is.Zero);
            Assert.That(harness.Hub.Session.ShardRules.PlayerVersusPlayer, Is.False);
            Assert.That(harness.Hub.Admin.AdminState, Is.Null);
            Assert.That(
                new[]
                {
                    harness.Hub.Diagnostics.RealtimePacketsSent, harness.Hub.Diagnostics.RealtimeBytesSent,
                    harness.Hub.Diagnostics.RealtimePacketsReceived, harness.Hub.Diagnostics.RealtimeBytesReceived
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
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joined));
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
            Assert.That(harness.Hub.Players.SpawnedEntityCount, Is.Zero);
            Assert.That(harness.Hub.WorldActors.SpawnedWorldActors, Is.Empty);
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
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.Hub.Movement.MovementSession, Is.Null);
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
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
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
        [TestCase("a placement without a transport key", "invalid_join_parameters")]
        [TestCase("an expired ticket", "join_ticket_expired")]
        [TestCase("a newer protocol version", "incompatible_protocol")]
        [TestCase("an older protocol version", "shard_not_updated")]
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
                    placement.joinTicketId = " ";
                    break;
                case "a placement without a transport key":
                    placement.transportKey = Convert.ToBase64String(new byte[16]);
                    break;
                case "an expired ticket":
                    placement.expiresAt = DateTime.UtcNow.AddMinutes(-1).ToString("O");
                    break;
                case "a newer protocol version":
                    placement.endpoint.protocolVersion = RealtimeProtocol.Version + 1;
                    break;
                case "an older protocol version":
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
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT " + code }));
        }

        [Test]
        public void ASecondJoinWhileTheFirstRunsIsBusyAndLeavesTheFirstAlone()
        {
            Assert.That(harness.BeginJoin(), Is.True);
            RealtimeClientError busy = null;

            var second = harness.Hub.Session.Join(RealtimeHubHarness.Placement(), _ => { }, error => busy = error);

            Assert.That(second.MoveNext(), Is.False);
            Assert.That((busy.Kind, busy.Code), Is.EqualTo((RealtimeClientErrorKind.Busy, "realtime_busy")));
            Assert.That(harness.Timeline, Is.EqualTo(new[] { "Start", "Connect" }));
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Connecting));
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
            Assert.That((harness.Hub.Diagnostics.ConnectedHost, harness.Hub.Diagnostics.ConnectedPort), Is.EqualTo((string.Empty, 0)));
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
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
            Assert.That((harness.Hub.Diagnostics.ConnectedHost, harness.Hub.Diagnostics.ConnectedPort), Is.EqualTo((string.Empty, 0)));
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
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
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joining));
            harness.Clear();

            harness.Now = 100f + RealtimeHubHarness.TimeoutSeconds;
            harness.Frame();

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "Stop" }));
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
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
            Assert.That(harness.Hub.Diagnostics.RoundTripTimeMilliseconds, Is.Zero);
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
        // in the world, reaches the caller of the leave.
        [TestCase((ushort)0)]
        [TestCase((ushort)20)]
        [TestCase((ushort)3600)]
        public void AnAcceptedLeaveEndsTheSessionInItsFrameWithoutAnUnexpectedDisconnect(ushort bodySeconds)
        {
            JoinWithPresence();

            Assert.That(harness.BeginLeave(" " + SessionId.ToString("D") + " "), Is.True);

            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Leaving));
            Assert.That(harness.Timeline, Is.EqualTo(new[] { "Send LeaveRequest on 3 ReliableOrdered" }));
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
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.Hub.Movement.MovementSession, Is.Null);
            Assert.That(harness.Hub.Session.ShardRules, Is.Null);
            Assert.That(harness.Hub.Items.CarryItemStateRevision, Is.EqualTo(-1));
            Assert.That(harness.StepLeave(), Is.False);
            Assert.That(harness.LeaveSucceeded, Is.True);
            Assert.That(harness.LeaveBodySeconds, Is.EqualTo(bodySeconds));
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
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joined));
            Assert.That(harness.Hub.Movement.MovementSession, Is.Not.Null);
            Assert.That(harness.Hub.Players.SpawnedEntityCount, Is.EqualTo(1));
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
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Leaving));

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
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joined));
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

        // The world and owner channels are not ordered against the session channel, so a lost
        // JoinAccepted lets their first messages arrive before it. They wait for it and follow it
        // in the order they arrived, after SessionJoined reset the session's parts.
        [Test]
        public void MessagesOfOtherChannelsThatOvertakeTheJoinAnswerWaitForIt()
        {
            harness.BeginJoinAndConnect();
            harness.Clear();
            harness.Queue(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)));
            harness.Queue(RealtimeProtocol.EncodeGameServiceAvailabilityChanged(false));
            harness.Queue(RealtimeProtocol.EncodeCombatantState(CombatantState(PlayerEntityId)));
            harness.Frame();
            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame" }));
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joining));

            harness.Deliver(RealtimeProtocol.EncodeJoinAccepted(RealtimeHubHarness.JoinAccepted()));

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "frame",
                    "frame",
                    "SessionJoined",
                    "EntitySpawned " + PlayerEntityId,
                    "GameServiceAvailabilityChanged unavailable",
                    "CombatantStateReceived " + PlayerEntityId
                }));
            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(harness.JoinError, Is.Null);
            Assert.That(harness.TakeFailures(), Is.Empty);
        }

        // What waited for the answer of a join that failed never reaches the next session.
        [Test]
        public void MessagesThatWaitedForAFailedJoinNeverReachTheNextSession()
        {
            harness.BeginJoinAndConnect();
            harness.Queue(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)));
            harness.Deliver(RealtimeProtocol.EncodeJoinRejected("join_ticket_consumed", "The join ticket was already used."));
            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "SIMULATION join_ticket_consumed" }));

            harness.JoinSession();

            Assert.That(harness.Hub.Players.SpawnedEntityCount, Is.Zero);
            Assert.That(harness.TakeFailures(), Is.Empty);
        }

        // A join that collects more waiting messages than the client holds fails instead of
        // growing without bound.
        [Test]
        public void AJoinAnswerThatTooManyMessagesOvertakeFailsTheJoin()
        {
            harness.BeginJoinAndConnect();
            harness.Clear();
            var spawn = RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId));
            for (var index = 0; index <= RealtimeMessageTable.MaximumHeldBeforeJoin; index++)
            {
                harness.Queue(spawn);
            }

            harness.Frame();

            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(harness.JoinError.Code, Is.EqualTo("join_backlog_overflow"));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT join_backlog_overflow" }));
        }

        // A lossy line can lose the leave's answer while the disconnect after it arrives. The
        // disconnect carries the answer as its data, and the leave ends as cleanly as with the
        // answer on the channel, with the body's seconds.
        [Test]
        public void ALeaveAnsweredOnlyByTheDisconnectDataEndsCleanly()
        {
            JoinWithPresence();
            Assert.That(harness.BeginLeave(), Is.True);
            harness.Clear();

            harness.Transport.QueueDisconnected(
                DisconnectReason.RemoteConnectionClose,
                RealtimeProtocol.EncodeLeaveAccepted(20));
            harness.Frame();

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
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.StepLeave(), Is.False);
            Assert.That(harness.LeaveSucceeded, Is.True);
            Assert.That(harness.LeaveBodySeconds, Is.EqualTo(20));
            Assert.That(harness.LeaveError, Is.Null);
            Assert.That(harness.TakeFailures(), Is.Empty);
        }

        // The answer that came on the channel ends the leave first, so the same answer in the
        // disconnect after it is not handled again.
        [Test]
        public void ALeaveAnswerInTheDisconnectDataAfterTheChannelOneIsNotHandledTwice()
        {
            JoinWithPresence();
            Assert.That(harness.BeginLeave(), Is.True);
            harness.Clear();

            harness.Queue(RealtimeProtocol.EncodeLeaveAccepted(20));
            harness.Transport.QueueDisconnected(
                DisconnectReason.RemoteConnectionClose,
                RealtimeProtocol.EncodeLeaveAccepted(20));
            harness.Frame();

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
            Assert.That(harness.StepLeave(), Is.False);
            Assert.That(harness.LeaveSucceeded, Is.True);
            Assert.That(harness.TakeFailures(), Is.Empty);
        }

        // A worker that ends the connection, such as for a protocol violation or a session that
        // GameService revoked, tells the client why in the disconnect too, so a client that lost
        // the reliable message still shows the reason.
        [TestCase("session_revoked")]
        [TestCase("udp_rate_limited")]
        public void AServerDisconnectOnlyInTheDisconnectDataEndsTheSessionWithItsCode(string code)
        {
            JoinWithPresence();

            harness.Transport.QueueDisconnected(
                DisconnectReason.RemoteConnectionClose,
                RealtimeProtocol.EncodeServerDisconnect(code, "The worker ends the connection."));
            harness.Frame();

            AssertEndedByTheServer(code);
        }

        // The stop notice of a stopping worker is a ServerDisconnect that also travels as the data
        // of the disconnect that every peer gets before the transport stops.
        [TestCase("server_shutdown")]
        [TestCase("game_service_outage")]
        public void AStopNoticeOnlyInTheDisconnectDataEndsTheSessionLikeTheReliableNotice(string code)
        {
            JoinWithPresence();

            harness.Transport.QueueDisconnected(
                DisconnectReason.RemoteConnectionClose,
                RealtimeProtocol.EncodeServerDisconnect(code, "The worker stops."));
            harness.Frame();

            AssertEndedByTheServer(code);
        }

        [Test]
        public void AJoinRejectionOnlyInTheDisconnectDataFailsTheJoinWithItsCode()
        {
            harness.BeginJoinAndConnect();
            harness.Clear();

            harness.Transport.QueueDisconnected(
                DisconnectReason.RemoteConnectionClose,
                RealtimeProtocol.EncodeJoinRejected("join_ticket_consumed", "The join ticket was already used."));
            harness.Frame();

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "Stop" }));
            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Server, "join_ticket_consumed")));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "SIMULATION join_ticket_consumed" }));
        }

        // Data that is no session end, such as the zeros LiteNetLib sends for data above its limit,
        // or another message, leaves the disconnect a lost connection, as without data.
        [TestCase("zeros")]
        [TestCase("an entity spawn")]
        public void ADisconnectWithOtherDataIsALostConnection(string data)
        {
            JoinWithPresence();

            harness.Transport.QueueDisconnected(
                DisconnectReason.RemoteConnectionClose,
                data == "zeros"
                    ? new byte[1015]
                    : RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(OtherPlayerEntityId)));
            harness.Frame();

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "frame",
                    "EntityDespawned 11 connection_lost",
                    "WorldActorDespawned 21 connection_lost",
                    "Stop",
                    "UnexpectedlyDisconnected connection_lost",
                    "SessionEnded connection_lost"
                }));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT connection_lost" }));
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
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
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
            Assert.That(harness.Hub.Diagnostics.RealtimePacketsReceived, Is.EqualTo(1), "The failure reset the counters.");
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT unexpected_join_rejection" }));
        }

        // A failure that a feature reports outside the frame ends the session at once, but the
        // next frame still polls the running transport, counts and drops what arrived, then stops
        // the transport and raises the session events.
        [Test]
        public void AClientFailureOutsideAFrameDefersTheSessionEventsToTheNextFrame()
        {
            JoinWithPresence();

            harness.Hub.Session.DisconnectForClientFailure("combat_state_conflict", "The combat state conflicts.");

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "EntityDespawned 11 combat_state_conflict",
                    "WorldActorDespawned 21 combat_state_conflict"
                }));
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
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
            Assert.That(harness.Hub.Diagnostics.RealtimePacketsReceived, Is.EqualTo(1));
            Assert.That(harness.Hub.Players.SpawnedEntityCount, Is.Zero);
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT combat_state_conflict" }));
        }

        [Test]
        public void AClientFailureWithoutAConnectionDoesNothing()
        {
            harness.Hub.Session.DisconnectForClientFailure("combat_state_conflict", "The combat state conflicts.");
            harness.Frame();

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame" }));
        }

        [Test]
        public void AbortEndsAJoinedSessionAtOnceWithoutAnUnexpectedDisconnect()
        {
            JoinWithPresence();

            harness.Hub.Session.Abort();
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
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
        }

        // The join routine finds no error of the hub and reports its own.
        [Test]
        public void AbortDuringAJoinEndsTheJoinWithAClosedConnection()
        {
            harness.BeginJoinAndConnect();
            harness.Clear();

            harness.Hub.Session.Abort();

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "Stop" }));
            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Network, "connection_closed")));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT connection_closed" }));
        }

        // An abort after a failure that waits for its frame stops the transport and raises the
        // events that the failure owes at once, in order, and no SessionEnded of its own.
        [Test]
        public void AbortAfterAFailureBeforeTheNextFrameRaisesTheOwedSessionEventsAtOnce()
        {
            JoinWithPresence();
            harness.Hub.Session.DisconnectForClientFailure("combat_state_conflict", "The combat state conflicts.");

            harness.Hub.Session.Abort();
            harness.Frame();

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "EntityDespawned 11 combat_state_conflict",
                    "WorldActorDespawned 21 combat_state_conflict",
                    "Stop",
                    "UnexpectedlyDisconnected combat_state_conflict",
                    "SessionEnded combat_state_conflict",
                    "frame"
                }));
            Assert.That(harness.LastPayload("SessionEnded"), Is.SameAs(harness.LastPayload("UnexpectedlyDisconnected")));
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT combat_state_conflict" }));
        }

        // A join that starts after a failure, before the frame that would stop the failed
        // transport, first finishes the failed session: it stops the transport and raises the
        // events that the failure owes, in order. Then it starts on the stopped transport and joins.
        [Test]
        public void AJoinBeforeTheFrameAfterAFailureFinishesTheFailedSessionAndJoins()
        {
            JoinWithPresence();
            harness.Hub.Session.DisconnectForClientFailure("combat_state_conflict", "The combat state conflicts.");
            harness.Clear();

            Assert.That(harness.BeginJoin(), Is.True);

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "Stop",
                    "UnexpectedlyDisconnected combat_state_conflict",
                    "SessionEnded combat_state_conflict",
                    "Start",
                    "Connect"
                }));
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Connecting));
            harness.Clear();
            harness.Transport.QueueConnected();
            harness.Frame();
            harness.Deliver(RealtimeProtocol.EncodeJoinAccepted(RealtimeHubHarness.JoinAccepted(true)));
            Assert.That(harness.StepJoin(), Is.False);

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[] { "frame", "Send JoinRequest on 0 ReliableOrdered", "frame", "SessionJoined" }));
            Assert.That(harness.JoinError, Is.Null);
            Assert.That(harness.JoinedSession.isReconnect, Is.True);
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT combat_state_conflict" }));
        }

        // A listener that joins again from UnexpectedlyDisconnected, as a reconnect does, sees
        // SessionEnded of the lost session before its new transport starts.
        [Test]
        public void AJoinFromTheUnexpectedDisconnectStartsAfterTheLostSessionEnded()
        {
            JoinWithPresence();
            harness.Hub.Session.UnexpectedlyDisconnected += _ => harness.BeginJoin();
            harness.Transport.QueueDisconnected(DisconnectReason.Timeout);

            harness.Frame();

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "frame",
                    "EntityDespawned 11 connection_lost",
                    "WorldActorDespawned 21 connection_lost",
                    "Stop",
                    "UnexpectedlyDisconnected connection_lost",
                    "SessionEnded connection_lost",
                    "Start",
                    "Connect"
                }));
            harness.Clear();
            harness.Transport.QueueConnected();
            harness.Frame();
            harness.Deliver(RealtimeProtocol.EncodeJoinAccepted(RealtimeHubHarness.JoinAccepted(true)));
            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(harness.JoinError, Is.Null);
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joined));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT connection_lost" }));
        }

        // Whatever is called after a joined session ended, before or after its frame, the session
        // ends with exactly one SessionEnded, and an unexpected end raises exactly one
        // UnexpectedlyDisconnected before it.
        [Test]
        public void AJoinedSessionEndsWithOneSessionEndedWhateverFollows(
            [Values("a lost connection", "a server disconnect", "a client failure", "an accepted leave", "an abort")]
            string ending,
            [Values("nothing", "an abort", "a join", "a client failure", "a leave")]
            string next)
        {
            JoinWithPresence();

            EndSessionBeforeItsFrame(ending);
            Follow(next);
            harness.Frame();

            var ended = harness.Timeline.Where(entry => entry.StartsWith("SessionEnded ", StringComparison.Ordinal)).ToArray();
            var unexpected = harness.Timeline
                .Where(entry => entry.StartsWith("UnexpectedlyDisconnected ", StringComparison.Ordinal))
                .ToArray();
            var endsUnexpectedly = ending != "an accepted leave" && ending != "an abort";
            Assert.That(ended.Length, Is.EqualTo(1));
            Assert.That(unexpected.Length, Is.EqualTo(endsUnexpectedly ? 1 : 0));
            if (endsUnexpectedly)
            {
                Assert.That(harness.Timeline.IndexOf(unexpected[0]), Is.LessThan(harness.Timeline.IndexOf(ended[0])));
                Assert.That(harness.LastPayload("SessionEnded"), Is.SameAs(harness.LastPayload("UnexpectedlyDisconnected")));
            }

            harness.TakeFailures();
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
            harness.Hub.Players.EntityDespawned += _ => seen.Add((harness.Hub.Session.State, harness.Hub.Movement.MovementSession != null));
            harness.Hub.WorldActors.WorldActorDespawned += _ => seen.Add((harness.Hub.Session.State, harness.Hub.Movement.MovementSession != null));

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
            harness.Hub.Session.SessionEnded += error => seen.Add((
                harness.Hub.Players.SpawnedEntityCount,
                harness.Hub.WorldActors.SpawnedWorldActors.Count,
                harness.Hub.Looks.TryGetAppearance(PlayerEntityId, out _)));

            EndSession(ending);

            Assert.That(seen, Is.EqualTo(new[] { (0, 0, false) }));
            harness.TakeFailures();
        }

        // The session that JoinWithPresence joined ended in this frame as a ServerDisconnect does.
        private void AssertEndedByTheServer(string code)
        {
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
            var error = (RealtimeClientError)harness.LastPayload("UnexpectedlyDisconnected");
            Assert.That((error.Kind, error.Code), Is.EqualTo((RealtimeClientErrorKind.Server, code)));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "SIMULATION " + code }));
        }

        // Joins with one spawned player and one spawned world actor, then clears the timeline.
        private void JoinWithPresence()
        {
            harness.JoinSession();
            harness.Queue(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)));
            harness.Queue(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn()));
            harness.Frame();
            Assert.That(harness.Hub.Players.SpawnedEntityCount, Is.EqualTo(1));
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
                    harness.Deliver(packet.Take(3).ToArray(), RealtimeProtocol.SessionChannel, DeliveryMethod.ReliableOrdered);
                    break;
                case "a client failure":
                    harness.Hub.Session.DisconnectForClientFailure("combat_state_conflict", "The combat state conflicts.");
                    harness.Frame();
                    break;
                case "an accepted leave":
                    Assert.That(harness.BeginLeave(), Is.True);
                    harness.Deliver(RealtimeProtocol.EncodeLeaveAccepted(20));
                    break;
                case "an abort":
                    harness.Hub.Session.Abort();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ending), ending, null);
            }
        }

        // Like EndSession, but a client failure stops before the frame that raises its events.
        private void EndSessionBeforeItsFrame(string ending)
        {
            if (ending == "a client failure")
            {
                harness.Hub.Session.DisconnectForClientFailure("combat_state_conflict", "The combat state conflicts.");
                return;
            }

            EndSession(ending);
        }

        private void Follow(string next)
        {
            switch (next)
            {
                case "nothing":
                    break;
                case "an abort":
                    harness.Hub.Session.Abort();
                    break;
                case "a join":
                    Assert.That(harness.BeginJoin(), Is.True);
                    break;
                case "a client failure":
                    harness.Hub.Session.DisconnectForClientFailure("inventory_state_conflict", "The inventory conflicts.");
                    break;
                case "a leave":
                    Assert.That(harness.BeginLeave(), Is.False);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(next), next, null);
            }
        }
    }
}
