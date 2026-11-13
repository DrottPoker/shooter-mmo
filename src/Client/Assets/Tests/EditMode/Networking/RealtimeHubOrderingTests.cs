using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using static Mmorpg.Client.Tests.EditMode.RealtimeHubMessages;

namespace Mmorpg.Client.Tests.EditMode
{
    // Pins the rules between the handlers of RealtimeSimulationClient: which presence a message
    // needs, which revisions it drops, what one handler leaves for the next, and in which order
    // the hub raises events within one poll.
    public sealed class RealtimeHubOrderingTests
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

        // The join, its first presence, and its first snapshot can arrive in one poll; a snapshot
        // that overtook the join is dropped.
        [Test]
        public void SessionJoinedFiresBeforeAnyLaterMessageOfTheSession()
        {
            harness.BeginJoinAndConnect();
            harness.Clear();
            harness.Queue(RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(1, 9)));
            harness.Queue(RealtimeProtocol.EncodeJoinAccepted(RealtimeHubHarness.JoinAccepted()));
            harness.Queue(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)));
            harness.Queue(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn()));
            harness.Queue(RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(2, 10)));

            harness.Frame();

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "frame",
                    "SessionJoined",
                    "EntitySpawned 11",
                    "WorldActorSpawned 21",
                    "OwnerSimulationSnapshotReceived 2/10"
                }));
        }

        [Test]
        public void AWorldActorStateWithoutItsSpawnFails()
        {
            harness.JoinSession();

            harness.Deliver(RealtimeProtocol.EncodeWorldActorState(WorldActorState()));

            AssertSessionFailed("world_actor_state_without_spawn");
        }

        [Test]
        public void AWorldActorStateOlderThanTheLatestRevisionIsDropped()
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn(stateRevision: 3)));
            harness.Clear();
            harness.Queue(RealtimeProtocol.EncodeWorldActorState(WorldActorState(stateRevision: 2)));
            harness.Queue(RealtimeProtocol.EncodeWorldActorState(WorldActorState(stateRevision: 3)));
            harness.Queue(RealtimeProtocol.EncodeWorldActorState(WorldActorState(stateRevision: 5)));
            harness.Queue(RealtimeProtocol.EncodeWorldActorState(WorldActorState(stateRevision: 4)));

            harness.Frame();

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[] { "frame", "WorldActorStateReceived 21 3", "WorldActorStateReceived 21 5" }));
        }

        // A spawn of the same actor replaces it only when it is not older than its latest state.
        [Test]
        public void AWorldActorSpawnOlderThanItsLatestStateIsDropped()
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn(stateRevision: 3)));
            harness.Deliver(RealtimeProtocol.EncodeWorldActorState(WorldActorState(stateRevision: 6)));
            harness.Clear();
            harness.Queue(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn(stateRevision: 5)));
            harness.Queue(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn(stateRevision: 6)));

            harness.Frame();

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "WorldActorSpawned 21" }));
            Assert.That(harness.Hub.WorldActors.SpawnedWorldActors.Single().StateRevision, Is.EqualTo(6));
        }

        [TestCase("another runtime actor")]
        [TestCase("another level")]
        public void AWorldActorSpawnThatReusesAnActiveIdForAnotherActorConflicts(string difference)
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn()));
            var other = difference == "another level"
                ? WorldActorSpawn(level: 2)
                : WorldActorSpawn(runtimeActorId: OtherActorRuntimeId);

            harness.Deliver(RealtimeProtocol.EncodeWorldActorSpawn(other));

            AssertSessionFailed("world_actor_entity_conflict");
        }

        [Test]
        public void AWorldActorDespawnThatNamesAnotherRuntimeActorConflicts()
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn()));

            harness.Deliver(RealtimeProtocol.EncodeWorldActorDespawn(
                WorldActorDespawn(runtimeActorId: OtherActorRuntimeId)));

            AssertSessionFailed("world_actor_despawn_conflict");
        }

        [Test]
        public void AWorldActorDespawnOfAnUnknownActorIsRaised()
        {
            harness.JoinSession();

            harness.Deliver(RealtimeProtocol.EncodeWorldActorDespawn(WorldActorDespawn(OtherActorEntityId)));

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "WorldActorDespawned 22 despawned" }));
        }

        // A player and a world actor never share a network entity id.
        [TestCase(RealtimeMessageType.EntitySpawn, "entity_scope_conflict")]
        [TestCase(RealtimeMessageType.WorldActorSpawn, "world_actor_entity_scope_conflict")]
        public void APlayerAndAWorldActorCannotShareAnId(RealtimeMessageType second, string code)
        {
            harness.JoinSession();
            var player = RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(ActorEntityId));
            var actor = RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn(ActorEntityId));
            harness.Deliver(second == RealtimeMessageType.EntitySpawn ? actor : player);

            harness.Deliver(second == RealtimeMessageType.EntitySpawn ? player : actor);

            AssertSessionFailed(code);
        }

        [TestCase("another persistent id")]
        [TestCase("another archetype")]
        public void APlayerSpawnThatReusesAnActiveIdForAnotherPlayerConflicts(string difference)
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)));
            var other = difference == "another archetype"
                ? EntitySpawn(PlayerEntityId, archetypeId: "player-other")
                : EntitySpawn(PlayerEntityId, persistentId: "9c3d2e1f-0b4a-4c5d-9e6f-a1b2c3d4e5f6");

            harness.Deliver(RealtimeProtocol.EncodeEntitySpawn(other));

            AssertSessionFailed("entity_id_conflict");
        }

        // The persistent id compares without case, and a repeated spawn replaces the entry.
        [Test]
        public void ARepeatedSpawnOfThePlayerReplacesItsEntryAndLook()
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)));
            harness.Clear();

            harness.Deliver(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(
                PlayerEntityId,
                persistentId: "8C3D2E1F-0B4A-4C5D-9E6F-A1B2C3D4E5F6",
                bodyModel: "Human_Male")));

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "EntitySpawned 11" }));
            Assert.That(harness.Hub.Players.SpawnedEntityCount, Is.EqualTo(1));
            Assert.That(harness.Hub.Looks.TryGetAppearance(PlayerEntityId, out var look), Is.True);
            Assert.That(look.BodyModel, Is.EqualTo("Human_Male"));
        }

        [TestCase("the controlled entity", ControlledEntityId)]
        [TestCase("a spawned player", PlayerEntityId)]
        [TestCase("a spawned world actor", ActorEntityId)]
        public void ACombatantStateOfAnEntityWithPresenceIsRaised(string entity, ulong entityId)
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)));
            harness.Deliver(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn()));
            harness.Clear();

            harness.Deliver(RealtimeProtocol.EncodeCombatantState(CombatantState(entityId)));

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "CombatantStateReceived " + entityId }), entity);
        }

        [Test]
        public void ACombatantStateOfAnEntityWithoutPresenceFails()
        {
            harness.JoinSession();

            harness.Deliver(RealtimeProtocol.EncodeCombatantState(CombatantState(OtherPlayerEntityId)));

            AssertSessionFailed("combatant_state_without_spawn");
        }

        // A look for a player the client does not see, as one that despawned while the change
        // was on its way, is dropped without a failure.
        [Test]
        public void AnAppearanceChangeOfAnEntityTheClientDoesNotSeeIsDropped()
        {
            harness.JoinSession();

            harness.Deliver(RealtimeProtocol.EncodeEntityAppearanceChanged(AppearanceChanged(OtherPlayerEntityId)));

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame" }));
            Assert.That(harness.Hub.Looks.TryGetAppearance(OtherPlayerEntityId, out _), Is.False);
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joined));
        }

        [Test]
        public void AnAppearanceChangeReplacesTheLookOfASpawnedPlayer()
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)));
            var seen = new List<string>();
            harness.Hub.Looks.EntityAppearanceChanged += _ =>
                seen.Add(harness.Hub.Looks.TryGetAppearance(PlayerEntityId, out var current) ? current.BodyModel : null);
            harness.Clear();

            harness.Deliver(RealtimeProtocol.EncodeEntityAppearanceChanged(AppearanceChanged(PlayerEntityId)));

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame", "EntityAppearanceChanged 11" }));
            Assert.That(seen, Is.EqualTo(new[] { "Human_Male" }), "The look is stored before the event.");
        }

        // Surprising but pinned: a despawn of the controlled entity id is raised like any other,
        // and the hub keeps the controlled entity's look through it.
        [Test]
        public void ADespawnKeepsTheLookOfTheControlledEntityOnly()
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeEntityAppearanceChanged(AppearanceChanged(ControlledEntityId)));
            harness.Deliver(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)));
            harness.Clear();
            harness.Queue(RealtimeProtocol.EncodeEntityDespawn(EntityDespawn(ControlledEntityId)));
            harness.Queue(RealtimeProtocol.EncodeEntityDespawn(EntityDespawn(PlayerEntityId)));

            harness.Frame();

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[] { "frame", "EntityDespawned 7 out_of_interest", "EntityDespawned 11 out_of_interest" }));
            Assert.That(harness.Hub.Looks.TryGetAppearance(ControlledEntityId, out var look), Is.True);
            Assert.That(look.BodyModel, Is.EqualTo("Human_Male"));
            Assert.That(harness.Hub.Looks.TryGetAppearance(PlayerEntityId, out _), Is.False);
            Assert.That(harness.Hub.Players.SpawnedEntityCount, Is.Zero);
        }

        // Five handlers apply the carry state of their message to the movement session before
        // they raise anything, and raise CarryStateChanged before their own event.
        [TestCase(RealtimeMessageType.CarryStateChanged, null)]
        [TestCase(RealtimeMessageType.ItemOperationResult, "ItemOperationCompleted")]
        [TestCase(RealtimeMessageType.CorpseInteractionResult, "CorpseInteractionCompleted")]
        [TestCase(RealtimeMessageType.OwnerItemQuantityChanged, "OwnerItemQuantityChanged")]
        [TestCase(RealtimeMessageType.OwnerItemStateChanged, "OwnerItemStateChanged")]
        public void CarryStateReachesTheMovementSessionBeforeAnyEventFires(
            RealtimeMessageType type,
            string featureEvent)
        {
            harness.JoinSession();
            var seen = new List<long>();
            harness.Hub.Movement.CarryStateChanged += _ => seen.Add(harness.Hub.Items.CarryItemStateRevision);
            harness.Hub.Items.ItemOperationCompleted += _ => seen.Add(harness.Hub.Items.CarryItemStateRevision);
            harness.Hub.Corpses.CorpseInteractionCompleted += _ => seen.Add(harness.Hub.Items.CarryItemStateRevision);
            harness.Hub.Items.OwnerItemQuantityChanged += _ => seen.Add(harness.Hub.Items.CarryItemStateRevision);
            harness.Hub.Items.OwnerItemStateChanged += _ => seen.Add(harness.Hub.Items.CarryItemStateRevision);

            harness.Deliver(CarryPacket(type, Carry(1)));

            var expected = featureEvent == null
                ? new[] { "frame", "CarryStateChanged 1" }
                : new[] { "frame", "CarryStateChanged 1", featureEvent };
            Assert.That(harness.Timeline, Is.EqualTo(expected));
            Assert.That(seen, Is.EqualTo(Enumerable.Repeat(1L, expected.Length - 1)));
        }

        [TestCase(RealtimeMessageType.CarryStateChanged, null)]
        [TestCase(RealtimeMessageType.ItemOperationResult, "ItemOperationCompleted")]
        [TestCase(RealtimeMessageType.CorpseInteractionResult, "CorpseInteractionCompleted")]
        public void AnUnchangedCarryStateRaisesNoCarryEvent(RealtimeMessageType type, string featureEvent)
        {
            harness.JoinSession();

            // The joined session carries revision 0 with weight 0.
            harness.Deliver(CarryPacket(type, Carry(0, 0)));

            Assert.That(
                harness.Timeline,
                Is.EqualTo(featureEvent == null ? new[] { "frame" } : new[] { "frame", featureEvent }));
        }

        [TestCase(RealtimeMessageType.CarryStateChanged, "an older revision", "invalid_carry_state")]
        [TestCase(RealtimeMessageType.CarryStateChanged, "other values for one revision", "invalid_carry_state")]
        [TestCase(RealtimeMessageType.ItemOperationResult, "an older revision", "invalid_item_operation_result")]
        [TestCase(RealtimeMessageType.CorpseInteractionResult, "an older revision", "invalid_corpse_operation_result")]
        [TestCase(RealtimeMessageType.OwnerItemStateChanged, "an older revision", "invalid_owner_item_state_changed")]
        public void ACarryStateThatTheMovementSessionRefusesFails(RealtimeMessageType type, string carry, string code)
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeCarryStateChanged(Carry(2)));

            harness.Deliver(CarryPacket(type, carry == "an older revision" ? Carry(1) : Carry(2, 99)));

            AssertSessionFailed(code);
        }

        [Test]
        public void ACarryStateWhileLeavingAdvancesTheMovementSessionWithoutAnEvent()
        {
            harness.JoinSession();
            Assert.That(harness.BeginLeave(), Is.True);
            harness.Clear();

            harness.Deliver(RealtimeProtocol.EncodeCarryStateChanged(Carry(3)));

            Assert.That(harness.Timeline, Is.EqualTo(new[] { "frame" }));
            Assert.That(harness.Hub.Items.CarryItemStateRevision, Is.EqualTo(3));
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Leaving));
        }

        [Test]
        public void AnAdminStateSetsTheWalkSpeedBeforeItsEvent()
        {
            harness.JoinSession();
            var seen = new List<(int, int)>();
            harness.Hub.Admin.AdminStateChanged += () =>
                seen.Add((harness.Hub.Admin.AdminState.SpeedPercent, harness.Hub.Movement.MovementSession.WalkSpeedPercent));

            harness.Deliver(RealtimeProtocol.EncodeAdminStateChanged(AdminState(250)));

            Assert.That(seen, Is.EqualTo(new[] { (250, 250) }));
        }

        // Owner snapshots raise an event unless their server tick is older than the newest
        // one; a duplicate tick is raised again. The sequence counts frames on its own, so a
        // newer sequence with an older tick counts a frame and raises nothing.
        [Test]
        public void OwnerSnapshotsAreRaisedByServerTickAndCountedBySequence()
        {
            harness.JoinSession();

            harness.Deliver(RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(5, 100)));
            harness.Deliver(RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(5, 100)));
            harness.Deliver(RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(4, 99)));
            harness.Deliver(RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(8, 110)));
            harness.Deliver(RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(9, 105)));

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "frame", "OwnerSimulationSnapshotReceived 5/100",
                    "frame", "OwnerSimulationSnapshotReceived 5/100",
                    "frame",
                    "frame", "OwnerSimulationSnapshotReceived 8/110",
                    "frame"
                }));
            Assert.That(
                new[]
                {
                    harness.Hub.Diagnostics.SnapshotPacketsReceived,
                    harness.Hub.Diagnostics.SnapshotFramesReceived,
                    harness.Hub.Diagnostics.EstimatedMissingSnapshotFrames
                },
                Is.EqualTo(new[] { 5L, 3L, 2L }));
            Assert.That(harness.Hub.Diagnostics.LatestServerTick, Is.EqualTo(110u));
        }

        [Test]
        public void OwnerSnapshotSequencesWrapAround()
        {
            harness.JoinSession();

            harness.Deliver(RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(uint.MaxValue, 100)));
            harness.Deliver(RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(1, 101)));

            Assert.That(
                (harness.Hub.Diagnostics.SnapshotFramesReceived, harness.Hub.Diagnostics.EstimatedMissingSnapshotFrames),
                Is.EqualTo((2L, 1L)));
            Assert.That(
                harness.Timeline.Count(entry => entry.StartsWith("OwnerSimulationSnapshotReceived", StringComparison.Ordinal)),
                Is.EqualTo(2));
        }

        // The age counts from the newest sequence, which an older one does not touch.
        [Test]
        public void TheSnapshotAgeFollowsTheNewestSequence()
        {
            harness.JoinSession();
            Assert.That(harness.Hub.Diagnostics.LatestSnapshotAgeSeconds, Is.EqualTo(-1f));
            harness.Now = 101f;
            harness.Deliver(RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(5, 100)));
            harness.Now = 102f;
            harness.Deliver(RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(4, 99)));

            harness.Now = 102.5f;

            Assert.That(harness.Hub.Diagnostics.LatestSnapshotAgeSeconds, Is.EqualTo(1.5f));
            harness.Deliver(RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(9, 90)));
            Assert.That(harness.Hub.Diagnostics.LatestSnapshotAgeSeconds, Is.Zero, "A newer sequence with an older tick still counts.");
        }

        // Remote snapshots carry no order of their own in the hub: each one is raised.
        [Test]
        public void RemoteSnapshotsAreRaisedWithoutOrderingAndLeaveTheServerTickAlone()
        {
            harness.JoinSession();
            harness.Queue(RealtimeProtocol.EncodeSimulationSnapshot(RemoteSnapshot(5, 100)));
            harness.Queue(RealtimeProtocol.EncodeSimulationSnapshot(RemoteSnapshot(5, 100)));
            harness.Queue(RealtimeProtocol.EncodeSimulationSnapshot(RemoteSnapshot(4, 99)));

            harness.Frame();

            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "frame",
                    "SimulationSnapshotReceived 5/100",
                    "SimulationSnapshotReceived 5/100",
                    "SimulationSnapshotReceived 4/99"
                }));
            Assert.That(
                (harness.Hub.Diagnostics.SnapshotPacketsReceived, harness.Hub.Diagnostics.SnapshotFramesReceived, harness.Hub.Diagnostics.LatestServerTick),
                Is.EqualTo((3L, 0L, 0u)));
        }

        // The registries hold the presence of the session for any reader, also one that comes
        // after the spawn events, and hand out copies.
        [Test]
        public void TheRegistriesHoldTheCurrentPresenceAndHandOutCopies()
        {
            harness.JoinSession();
            harness.Queue(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)));
            harness.Queue(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(OtherPlayerEntityId, persistentId: "9c3d2e1f-0b4a-4c5d-9e6f-a1b2c3d4e5f6")));
            harness.Queue(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn()));
            harness.Queue(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn(OtherActorEntityId, OtherActorRuntimeId)));
            harness.Queue(RealtimeProtocol.EncodeEntityDespawn(EntityDespawn(OtherPlayerEntityId)));
            harness.Frame();
            var lateSubscriberEvents = 0;
            harness.Hub.Players.EntitySpawned += _ => lateSubscriberEvents++;
            harness.Hub.WorldActors.WorldActorSpawned += _ => lateSubscriberEvents++;

            var players = harness.Hub.Players.SpawnedEntities;
            var actors = harness.Hub.WorldActors.SpawnedWorldActors;

            Assert.That(lateSubscriberEvents, Is.Zero, "Subscribing raises nothing; a late reader reads the registry.");
            Assert.That(players.Select(spawn => spawn.EntityId), Is.EqualTo(new[] { PlayerEntityId }));
            Assert.That(harness.Hub.Players.SpawnedEntityCount, Is.EqualTo(1));
            Assert.That(actors.Select(spawn => spawn.EntityId), Is.EquivalentTo(new[] { ActorEntityId, OtherActorEntityId }));
            harness.Deliver(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(OtherPlayerEntityId, persistentId: "9c3d2e1f-0b4a-4c5d-9e6f-a1b2c3d4e5f6")));
            Assert.That(players.Count, Is.EqualTo(1), "A registry read is a copy.");
            Assert.That(harness.Hub.Players.SpawnedEntityCount, Is.EqualTo(2));
        }

        // At the end of a session the hub despawns every player, then every world actor, each in
        // the order it spawned.
        [Test]
        public void TheEndOfASessionDespawnsPlayersBeforeWorldActors()
        {
            harness.JoinSession();
            harness.Queue(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn()));
            harness.Queue(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId)));
            harness.Queue(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn(OtherActorEntityId, OtherActorRuntimeId)));
            harness.Queue(RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(OtherPlayerEntityId, persistentId: "9c3d2e1f-0b4a-4c5d-9e6f-a1b2c3d4e5f6")));
            harness.Frame();
            harness.Clear();

            harness.Transport.QueueDisconnected(DisconnectReason.Timeout);
            harness.Frame();

            Assert.That(
                harness.Timeline.Take(5),
                Is.EqualTo(new[]
                {
                    "frame",
                    "EntityDespawned 11 connection_lost",
                    "EntityDespawned 12 connection_lost",
                    "WorldActorDespawned 21 connection_lost",
                    "WorldActorDespawned 22 connection_lost"
                }));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT connection_lost" }));
        }

        private static byte[] CarryPacket(RealtimeMessageType type, RealtimeCarryState carry)
        {
            switch (type)
            {
                case RealtimeMessageType.ItemOperationResult:
                    return RealtimeProtocol.EncodeItemOperationResult(ItemOperationResult(carry));
                case RealtimeMessageType.CorpseInteractionResult:
                    return RealtimeProtocol.EncodeCorpseInteractionResult(CorpseInteractionResult(carry));
                case RealtimeMessageType.OwnerItemQuantityChanged:
                    return RealtimeProtocol.EncodeOwnerItemQuantityChanged(
                        OwnerItemQuantityChanged(carry.ItemStateRevision - 1, carry));
                case RealtimeMessageType.OwnerItemStateChanged:
                    return RealtimeProtocol.EncodeOwnerItemStateChanged(OwnerItemStateChanged(carry.ItemStateRevision - 1));
                default:
                    return RealtimeProtocol.EncodeCarryStateChanged(carry);
            }
        }

        // The latest frame ended the session with the code.
        private void AssertSessionFailed(string code)
        {
            Assert.That(
                harness.Timeline.Skip(harness.Timeline.LastIndexOf("frame"))
                    .Where(entry => !entry.StartsWith("EntityDespawned", StringComparison.Ordinal)
                        && !entry.StartsWith("WorldActorDespawned", StringComparison.Ordinal)),
                Is.EqualTo(new[] { "frame", "Stop", "UnexpectedlyDisconnected " + code, "SessionEnded " + code }));
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT " + code }));
        }
    }
}
