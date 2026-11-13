using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using NUnit.Framework;
using static Mmorpg.Client.Tests.EditMode.RealtimeHubMessages;

namespace Mmorpg.Client.Tests.EditMode
{
    // Pins, for every server message type, what RealtimeSimulationClient does with it in each
    // state today: the event it raises with the decoded content, the protocol failure code of
    // the wrong state and of a malformed payload, and the channel and delivery it requires.
    // A new message type fails EveryMessageTypeHasACase until it gets a case in ServerCases.
    public sealed class RealtimeHubDispatchTests
    {
        private RealtimeHubHarness harness;

        public enum Reaction
        {
            // Not given; EveryMessageTypeHasACase fails on it.
            Unset,
            // Raises the listed events and keeps the session.
            Raises,
            // Takes the protocol failure path with the case's code.
            Fails,
            // Drops the message without a failure.
            Ignored,
            // Has tests of its own in RealtimeHubConnectionTests.
            OwnTests
        }

        public enum HubState
        {
            Joining,
            Joined,
            Leaving
        }

        private static IEnumerable<ServerMessageCase> ServerCases
        {
            get
            {
                yield return new ServerMessageCase(
                    RealtimeMessageType.JoinAccepted,
                    () => RealtimeProtocol.EncodeJoinAccepted(RealtimeHubHarness.JoinAccepted()))
                    .WhileJoined(Reaction.Fails, "unexpected_join_response")
                    .BeforeTheJoin(Reaction.OwnTests)
                    .WhileLeaving(Reaction.Fails, "unexpected_join_response")
                    .Malformed("invalid_join_response", HubState.Joining);
                yield return new ServerMessageCase(
                    RealtimeMessageType.JoinRejected,
                    () => RealtimeProtocol.EncodeJoinRejected("join_ticket_expired", "The join ticket expired."))
                    .WhileJoined(Reaction.Fails, "unexpected_join_rejection")
                    .BeforeTheJoin(Reaction.OwnTests)
                    .WhileLeaving(Reaction.Fails, "unexpected_join_rejection")
                    .Malformed("invalid_join_rejection", HubState.Joining);
                yield return new ServerMessageCase(
                    RealtimeMessageType.LeaveAccepted,
                    () => RealtimeProtocol.EncodeLeaveAccepted(20))
                    .WhileJoined(Reaction.Fails, "unexpected_leave_response")
                    .BeforeTheJoin(Reaction.Fails, "unexpected_leave_response")
                    .WhileLeaving(Reaction.OwnTests)
                    .Malformed("invalid_leave_response", HubState.Leaving);
                yield return new ServerMessageCase(
                    RealtimeMessageType.LeaveRejected,
                    () => RealtimeProtocol.EncodeLeaveRejected("leave_not_joined", "No simulation session is active."))
                    .WhileJoined(Reaction.Fails, "unexpected_leave_rejection")
                    .BeforeTheJoin(Reaction.Fails, "unexpected_leave_rejection")
                    .WhileLeaving(Reaction.OwnTests)
                    .Malformed("invalid_leave_rejection", HubState.Leaving);
                yield return new ServerMessageCase(
                    RealtimeMessageType.ServerDisconnect,
                    () => RealtimeProtocol.EncodeServerDisconnect("server_shutdown", "The worker stops."))
                    .WhileJoined(Reaction.OwnTests)
                    .BeforeTheJoin(Reaction.OwnTests)
                    .WhileLeaving(Reaction.OwnTests)
                    .Malformed("invalid_disconnect_reason", HubState.Joined);
                yield return Feature(
                        RealtimeMessageType.EntitySpawn,
                        () => EntitySpawn(PlayerEntityId),
                        RealtimeProtocol.EncodeEntitySpawn,
                        "entity_spawn",
                        "EntitySpawned 11")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.EntityDespawn,
                        () => EntityDespawn(PlayerEntityId),
                        RealtimeProtocol.EncodeEntityDespawn,
                        "entity_despawn",
                        "EntityDespawned 11 out_of_interest")
                    .Prepared(PreparePlayer)
                    .WhileLeaving(Reaction.Ignored);
                yield return new ServerMessageCase(
                        RealtimeMessageType.CarryStateChanged,
                        () => RealtimeProtocol.EncodeCarryStateChanged(Carry(1)),
                        payload => RealtimeProtocol.EncodeCarryStateChanged(ToRealtime((PlayerCarryState)payload)))
                    .WhileJoined(Reaction.Raises, "CarryStateChanged 1")
                    .BeforeTheJoin(Reaction.Fails, "unexpected_carry_state")
                    .WhileLeaving(Reaction.Ignored)
                    .Malformed("invalid_carry_state", HubState.Joined);
                yield return Feature(
                        RealtimeMessageType.ItemOperationResult,
                        () => ItemOperationResult(Carry(1)),
                        RealtimeProtocol.EncodeItemOperationResult,
                        "item_operation_result",
                        "CarryStateChanged 1",
                        "ItemOperationCompleted")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.CorpsePresenceSnapshotChunk,
                        CorpsePresence,
                        RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk,
                        "corpse_presence",
                        "CorpsePresenceChunkReceived")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.CorpseInteractionResult,
                        () => CorpseInteractionResult(Carry(1)),
                        RealtimeProtocol.EncodeCorpseInteractionResult,
                        "corpse_operation_result",
                        "CarryStateChanged 1",
                        "CorpseInteractionCompleted")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.CorpseViewStateChunk,
                        CorpseViewState,
                        RealtimeProtocol.EncodeCorpseViewStateChunk,
                        "corpse_view_state",
                        "CorpseViewStateChunkReceived")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.CorpseViewClosed,
                        CorpseViewClosed,
                        RealtimeProtocol.EncodeCorpseViewClosed,
                        "corpse_view_closed",
                        "CorpseViewClosed")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.WorldActorSpawn,
                        () => WorldActorSpawn(),
                        RealtimeProtocol.EncodeWorldActorSpawn,
                        "world_actor_spawn",
                        "WorldActorSpawned 21")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.WorldActorState,
                        () => WorldActorState(),
                        RealtimeProtocol.EncodeWorldActorState,
                        "world_actor_state",
                        "WorldActorStateReceived 21 4")
                    .Prepared(PrepareWorldActor)
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.WorldActorDespawn,
                        () => WorldActorDespawn(),
                        RealtimeProtocol.EncodeWorldActorDespawn,
                        "world_actor_despawn",
                        "WorldActorDespawned 21 despawned")
                    .Prepared(PrepareWorldActor)
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.WorldInteractionOpened,
                        InteractionOpened,
                        RealtimeProtocol.EncodeWorldInteractionOpened,
                        "world_interaction_opened",
                        "WorldInteractionOpened")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.WorldInteractionResult,
                        InteractionResult,
                        RealtimeProtocol.EncodeWorldInteractionResult,
                        "world_interaction_result",
                        "WorldInteractionCompleted")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.WorldInteractionClosed,
                        InteractionClosed,
                        RealtimeProtocol.EncodeWorldInteractionClosed,
                        "world_interaction_closed",
                        "WorldInteractionClosed")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.GatheringState,
                        GatheringStarted,
                        RealtimeProtocol.EncodeGatheringState,
                        "gathering_state",
                        "GatheringStateReceived")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.CombatIntentResult,
                        CombatIntentResult,
                        RealtimeProtocol.EncodeCombatIntentResult,
                        "combat_intent_result",
                        "CombatIntentCompleted")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.CombatantState,
                        () => CombatantState(ControlledEntityId),
                        RealtimeProtocol.EncodeCombatantState,
                        "combatant_state",
                        "CombatantStateReceived 7")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.CombatEventBatch,
                        CombatEvents,
                        RealtimeProtocol.EncodeCombatEventBatch,
                        "combat_event_batch",
                        "CombatEventBatchReceived")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.OwnerCombatState,
                        OwnerCombatState,
                        RealtimeProtocol.EncodeOwnerCombatState,
                        "owner_combat_state",
                        "OwnerCombatStateReceived")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.OwnerItemQuantityChanged,
                        () => OwnerItemQuantityChanged(0, Carry(1)),
                        RealtimeProtocol.EncodeOwnerItemQuantityChanged,
                        "owner_item_quantity_changed",
                        "CarryStateChanged 1",
                        "OwnerItemQuantityChanged")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.OwnerItemStateChanged,
                        () => OwnerItemStateChanged(0),
                        RealtimeProtocol.EncodeOwnerItemStateChanged,
                        "owner_item_state_changed",
                        "CarryStateChanged 1",
                        "OwnerItemStateChanged")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.EntityAppearanceChanged,
                        () => AppearanceChanged(ControlledEntityId),
                        RealtimeProtocol.EncodeEntityAppearanceChanged,
                        "entity_appearance_changed",
                        "EntityAppearanceChanged 7")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.AppearanceIntentResult,
                        AppearanceIntentResult,
                        RealtimeProtocol.EncodeAppearanceIntentResult,
                        "appearance_intent_result",
                        "AppearanceIntentCompleted")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.AdminIntentResult,
                        AdminIntentResult,
                        RealtimeProtocol.EncodeAdminIntentResult,
                        "admin_intent_result",
                        "AdminIntentCompleted")
                    .WhileLeaving(Reaction.Ignored);
                // The event has no argument; the harness records the admin channel's AdminState with it.
                yield return Feature(
                        RealtimeMessageType.AdminStateChanged,
                        () => AdminState(),
                        RealtimeProtocol.EncodeAdminStateChanged,
                        "admin_state",
                        "AdminStateChanged")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.QuestIntentResult,
                        QuestIntentResult,
                        RealtimeProtocol.EncodeQuestIntentResult,
                        "quest_intent_result",
                        "QuestIntentCompleted")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.QuestLogChanged,
                        QuestLogChanged,
                        RealtimeProtocol.EncodeQuestLogChanged,
                        "quest_log_change",
                        "QuestLogChanged")
                    .WhileLeaving(Reaction.Ignored);
                yield return Feature(
                        RealtimeMessageType.QuestDropInCorpse,
                        QuestDrop,
                        RealtimeProtocol.EncodeQuestDropInCorpse,
                        "quest_drop",
                        "QuestDropInCorpse")
                    .WhileLeaving(Reaction.Ignored);
                // Snapshots outside the Joined state are dropped, not failures.
                yield return new ServerMessageCase(
                        RealtimeMessageType.SimulationSnapshot,
                        () => RealtimeProtocol.EncodeSimulationSnapshot(RemoteSnapshot(5, 100)),
                        payload => RealtimeProtocol.EncodeSimulationSnapshot((RealtimeSimulationSnapshot)payload))
                    .WhileJoined(Reaction.Raises, "SimulationSnapshotReceived 5/100")
                    .BeforeTheJoin(Reaction.Ignored)
                    .WhileLeaving(Reaction.Ignored)
                    .Malformed("invalid_simulation_snapshot", HubState.Joined);
                yield return new ServerMessageCase(
                        RealtimeMessageType.OwnerSimulationSnapshot,
                        () => RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(5, 100)),
                        payload => RealtimeProtocol.EncodeOwnerSimulationSnapshot((RealtimeOwnerSimulationSnapshot)payload))
                    .WhileJoined(Reaction.Raises, "OwnerSimulationSnapshotReceived 5/100")
                    .BeforeTheJoin(Reaction.Ignored)
                    .WhileLeaving(Reaction.Ignored)
                    .Malformed("invalid_owner_simulation_snapshot", HubState.Joined);
            }
        }

        // Messages a client sends; one that comes back from the server is invalid for clients.
        private static IEnumerable<TestCaseData> ClientCases
        {
            get
            {
                yield return Client(RealtimeProtocol.EncodeJoinRequest("ticket-1"));
                yield return Client(RealtimeProtocol.EncodeLeaveRequest(SessionId.ToString("D")));
                yield return Client(RealtimeProtocol.EncodeMovementInputBatch(MovementInputs()));
                yield return Client(RealtimeProtocol.EncodeItemOperationIntent(ItemIntent()));
                yield return Client(RealtimeProtocol.EncodeCorpseInteractionIntent(CorpseIntent()));
                yield return Client(RealtimeProtocol.EncodeWorldInteractionIntent(WorldIntent()));
                yield return Client(RealtimeProtocol.EncodeCombatIntent(CombatIntent()));
                yield return Client(RealtimeProtocol.EncodeAppearanceIntent(AppearanceIntent()));
                yield return Client(RealtimeProtocol.EncodeAdminIntent(AdminIntent()));
                yield return Client(RealtimeProtocol.EncodeQuestIntent(QuestIntent()));
            }
        }

        private static IEnumerable<ServerMessageCase> JoinedCases
        {
            get { return ServerCases.Where(item => item.Joined != Reaction.OwnTests); }
        }

        private static IEnumerable<ServerMessageCase> BeforeTheJoinCases
        {
            get { return ServerCases.Where(item => item.BeforeJoin != Reaction.OwnTests); }
        }

        private static IEnumerable<ServerMessageCase> LeavingCases
        {
            get { return ServerCases.Where(item => item.Leaving != Reaction.OwnTests); }
        }

        // Each message on two wrong combinations of channel and delivery.
        private static IEnumerable<TestCaseData> WrongDeliveryCases
        {
            get
            {
                foreach (var item in ServerCases)
                {
                    if (item.IsSnapshot)
                    {
                        yield return new TestCaseData(item, (byte)1, DeliveryMethod.Unreliable);
                        yield return new TestCaseData(item, RealtimeProtocol.UnreliableReceiveChannel, DeliveryMethod.ReliableOrdered);
                    }
                    else
                    {
                        yield return new TestCaseData(item, RealtimeProtocol.MovementInputChannel, DeliveryMethod.ReliableOrdered);
                        yield return new TestCaseData(item, RealtimeProtocol.ControlChannel, DeliveryMethod.ReliableUnordered);
                    }
                }
            }
        }

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
        public void EveryMessageTypeHasACase()
        {
            var types = Enum.GetValues(typeof(RealtimeMessageType)).Cast<RealtimeMessageType>()
                .Where(type => RealtimeProtocol.TryGetDescriptor(type, out _))
                .ToArray();
            var serverTypes = types.Where(type => Direction(type) == RealtimeMessageDirection.ServerToClient);
            var clientTypes = types.Where(type => Direction(type) == RealtimeMessageDirection.ClientToServer);

            Assert.That(ServerCases.Select(item => item.Type), Is.EquivalentTo(serverTypes));
            Assert.That(
                ServerCases.Where(item => item.Joined == Reaction.Unset
                    || item.BeforeJoin == Reaction.Unset
                    || item.Leaving == Reaction.Unset
                    || item.MalformedCode == null),
                Is.Empty,
                "Every case says what each state does.");
            Assert.That(ClientCases.Select(item => TypeOf((byte[])item.Arguments[0])), Is.EquivalentTo(clientTypes));
        }

        [TestCaseSource(nameof(JoinedCases))]
        public void WhileJoined(ServerMessageCase item)
        {
            harness.JoinSession();
            item.Prepare?.Invoke(harness);
            harness.Clear();
            var packet = item.Packet();

            harness.Deliver(packet);

            if (item.Joined == Reaction.Raises)
            {
                Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame" }.Concat(item.JoinedEvents)));
                Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joined));
                var featureEvent = item.JoinedEvents.Last().Split(' ')[0];
                Assert.That(
                    item.Content(harness.LastPayload(featureEvent)),
                    Is.EqualTo(packet),
                    "The event carries the decoded message.");
                Assert.That(harness.TakeFailures(), Is.Empty);
                return;
            }

            AssertSessionFailed(item.JoinedCode);
        }

        [TestCaseSource(nameof(BeforeTheJoinCases))]
        public void BeforeTheJoinCompletes(ServerMessageCase item)
        {
            harness.BeginJoinAndConnect();
            harness.Clear();

            harness.Deliver(item.Packet());

            if (item.BeforeJoin == Reaction.Ignored)
            {
                Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame" }));
                Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joining));
                Assert.That(harness.StepJoin(), Is.True);
                return;
            }

            AssertJoinFailed(item.BeforeJoinCode);
        }

        [TestCaseSource(nameof(LeavingCases))]
        public void WhileLeaving(ServerMessageCase item)
        {
            harness.JoinSession();
            item.Prepare?.Invoke(harness);
            Assert.That(harness.BeginLeave(), Is.True);
            harness.Clear();
            var received = harness.Hub.Diagnostics.RealtimePacketsReceived;

            harness.Deliver(item.Packet());

            if (item.Leaving == Reaction.Ignored)
            {
                Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame" }));
                Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Leaving));
                Assert.That(harness.Hub.Diagnostics.RealtimePacketsReceived, Is.EqualTo(received + 1), "An ignored packet is counted.");
                Assert.That(harness.StepLeave(), Is.True);
                return;
            }

            AssertLeaveFailed(item.LeavingCode);
        }

        // A trailing byte makes every message unreadable.
        [TestCaseSource(nameof(ServerCases))]
        public void AMalformedMessageFailsWithItsCode(ServerMessageCase item)
        {
            var packet = item.Packet().Concat(new byte[] { 0 }).ToArray();
            switch (item.MalformedIn)
            {
                case HubState.Joining:
                    harness.BeginJoinAndConnect();
                    harness.Clear();
                    harness.Deliver(packet);
                    AssertJoinFailed(item.MalformedCode);
                    break;
                case HubState.Leaving:
                    harness.JoinSession();
                    Assert.That(harness.BeginLeave(), Is.True);
                    harness.Clear();
                    harness.Deliver(packet);
                    AssertLeaveFailed(item.MalformedCode);
                    break;
                default:
                    harness.JoinSession();
                    harness.Deliver(packet);
                    AssertSessionFailed(item.MalformedCode);
                    break;
            }
        }

        // The delivery is checked before the state, so even a message the state would drop fails.
        [TestCaseSource(nameof(WrongDeliveryCases))]
        public void AMessageOnTheWrongChannelOrDeliveryFails(
            ServerMessageCase item,
            byte channel,
            DeliveryMethod deliveryMethod)
        {
            harness.BeginJoinAndConnect();
            harness.Clear();

            harness.Deliver(item.Packet(), channel, deliveryMethod);

            AssertJoinFailed(item.IsSnapshot ? "invalid_snapshot_delivery" : "invalid_control_delivery");
        }

        [Test]
        public void AMessageThatTheLeavingStateDropsStillFailsOnTheWrongChannel()
        {
            harness.JoinSession();
            Assert.That(harness.BeginLeave(), Is.True);
            harness.Clear();

            harness.Deliver(
                RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)),
                RealtimeProtocol.MovementInputChannel,
                DeliveryMethod.ReliableOrdered);

            AssertLeaveFailed("invalid_control_delivery");
        }

        [TestCaseSource(nameof(ClientCases))]
        public void AClientMessageFromTheServerIsUnexpected(byte[] packet)
        {
            harness.JoinSession();

            harness.Deliver(packet, RealtimeProtocol.ControlChannel, DeliveryMethod.ReliableOrdered);

            AssertSessionFailed("unexpected_message");
        }

        [Test]
        public void AClientMessageFromTheServerIsUnexpectedWhileLeavingToo()
        {
            harness.JoinSession();
            Assert.That(harness.BeginLeave(), Is.True);
            harness.Clear();

            harness.Deliver(
                RealtimeProtocol.EncodeCombatIntent(CombatIntent()),
                RealtimeProtocol.ControlChannel,
                DeliveryMethod.ReliableOrdered);

            AssertLeaveFailed("unexpected_message");
        }

        // A packet whose header cannot be read, here the first three bytes of a valid one.
        [Test]
        public void AnUnreadablePacketFailsBeforeTheJoinCompletes()
        {
            harness.BeginJoinAndConnect();
            harness.Clear();
            var packet = RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)).Take(3).ToArray();

            harness.Deliver(packet, RealtimeProtocol.ControlChannel, DeliveryMethod.ReliableOrdered);

            AssertJoinFailed("invalid_packet");
        }

        private static ServerMessageCase Feature<T>(
            RealtimeMessageType type,
            Func<T> model,
            Func<T, byte[]> encode,
            string codeSuffix,
            params string[] joinedEvents)
        {
            return new ServerMessageCase(type, () => encode(model()), payload => encode((T)payload))
                .WhileJoined(Reaction.Raises, joinedEvents)
                .BeforeTheJoin(Reaction.Fails, "unexpected_" + codeSuffix)
                .Malformed("invalid_" + codeSuffix, HubState.Joined);
        }

        private static TestCaseData Client(byte[] packet)
        {
            return new TestCaseData(packet).SetName("AClientMessageFromTheServerIsUnexpected(" + TypeOf(packet) + ")");
        }

        private static RealtimeMessageType TypeOf(byte[] packet)
        {
            RealtimeProtocol.TryReadMessageType(packet, out var type);
            return type;
        }

        private static RealtimeMessageDirection Direction(RealtimeMessageType type)
        {
            RealtimeProtocol.TryGetDescriptor(type, out var descriptor);
            return descriptor.Direction;
        }

        private static RealtimeCarryState ToRealtime(PlayerCarryState carry)
        {
            return new RealtimeCarryState(carry.ItemStateRevision, carry.CarriedWeight, carry.CarryCapacity);
        }

        private static void PreparePlayer(RealtimeHubHarness target)
        {
            target.Deliver(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)));
        }

        private static void PrepareWorldActor(RealtimeHubHarness target)
        {
            target.Deliver(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn()));
        }

        private void AssertSessionFailed(string code)
        {
            Assert.That(
                harness.Timeline.Where(entry => !entry.StartsWith("EntityDespawned", StringComparison.Ordinal)
                    && !entry.StartsWith("WorldActorDespawned", StringComparison.Ordinal)),
                Is.EqualTo(new[] { "frame", "Stop", "UnexpectedlyDisconnected " + code, "SessionEnded " + code }));
            Assert.That(
                ((RealtimeClientError)harness.LastPayload("SessionEnded")).Kind,
                Is.EqualTo(RealtimeClientErrorKind.Protocol));
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT " + code }));
        }

        private void AssertJoinFailed(string code)
        {
            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "Stop" }));
            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(
                (harness.JoinError.Kind, harness.JoinError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Protocol, code)));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT " + code }));
        }

        private void AssertLeaveFailed(string code)
        {
            Assert.That(
                harness.Timeline.Where(entry => !entry.StartsWith("EntityDespawned", StringComparison.Ordinal)
                    && !entry.StartsWith("WorldActorDespawned", StringComparison.Ordinal)),
                Is.EqualTo(new[] { "frame", "Stop", "SessionEnded " + code }),
                "A failure while leaving raises no UnexpectedlyDisconnected.");
            Assert.That(harness.StepLeave(), Is.False);
            Assert.That(
                (harness.LeaveError.Kind, harness.LeaveError.Code),
                Is.EqualTo((RealtimeClientErrorKind.Protocol, code)));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT " + code }));
        }

        // One server message type and what the hub does with it in each state.
        public sealed class ServerMessageCase
        {
            public ServerMessageCase(
                RealtimeMessageType type,
                Func<byte[]> packet,
                Func<object, byte[]> content = null)
            {
                Type = type;
                Packet = packet;
                Content = content;
            }

            public RealtimeMessageType Type { get; }

            // A valid packet for a fresh joined session after Prepare.
            public Func<byte[]> Packet { get; }

            // Encodes the payload of the feature event, which must give the packet again.
            public Func<object, byte[]> Content { get; }

            // Delivers the presence the message needs, in a joined session.
            internal Action<RealtimeHubHarness> Prepare { get; private set; }

            public Reaction Joined { get; private set; }

            public string[] JoinedEvents { get; private set; } = Array.Empty<string>();

            public string JoinedCode { get; private set; }

            public Reaction BeforeJoin { get; private set; }

            public string BeforeJoinCode { get; private set; }

            public Reaction Leaving { get; private set; }

            public string LeavingCode { get; private set; }

            public string MalformedCode { get; private set; }

            public HubState MalformedIn { get; private set; }

            public bool IsSnapshot
            {
                get
                {
                    return Type == RealtimeMessageType.SimulationSnapshot
                        || Type == RealtimeMessageType.OwnerSimulationSnapshot;
                }
            }

            internal ServerMessageCase Prepared(Action<RealtimeHubHarness> prepare)
            {
                Prepare = prepare;
                return this;
            }

            public ServerMessageCase WhileJoined(Reaction reaction, params string[] codeOrEvents)
            {
                Joined = reaction;
                if (reaction == Reaction.Raises)
                {
                    JoinedEvents = codeOrEvents;
                }
                else
                {
                    JoinedCode = codeOrEvents.FirstOrDefault();
                }

                return this;
            }

            public ServerMessageCase BeforeTheJoin(Reaction reaction, string code = null)
            {
                BeforeJoin = reaction;
                BeforeJoinCode = code;
                return this;
            }

            public ServerMessageCase WhileLeaving(Reaction reaction, string code = null)
            {
                Leaving = reaction;
                LeavingCode = code;
                return this;
            }

            public ServerMessageCase Malformed(string code, HubState state)
            {
                MalformedCode = code;
                MalformedIn = state;
                return this;
            }

            public override string ToString()
            {
                return Type.ToString();
            }
        }
    }
}
