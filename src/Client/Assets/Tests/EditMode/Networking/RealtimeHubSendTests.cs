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
    // Pins what each send method of RealtimeSimulationClient puts on which channel, when it
    // refuses, and the diagnostics counters of a short scripted session.
    public sealed class RealtimeHubSendTests
    {
        private RealtimeHubHarness harness;

        private static readonly MovementPath Route = new MovementPath(
            3,
            new[] { new SimulationVector3(1f, 0f, 2f), new SimulationVector3(4f, 0f, 5f) });

        private static IEnumerable<SendCase> SendCases
        {
            get
            {
                yield return new SendCase(
                    "TrySendMovementInputs",
                    hub => hub.Movement.TrySendMovementInputs(
                        new[] { new PlayerMovementInput(1, 10, null), new PlayerMovementInput(2, 11, Route) },
                        Route),
                    () => RealtimeProtocol.EncodeMovementInputBatch(new RealtimeMovementInputBatch(
                        new[] { new RealtimeMovementInput(1, 10, 0), new RealtimeMovementInput(2, 11, 3) },
                        new RealtimeMovementPath(
                            3,
                            new[]
                            {
                                new RealtimeMovementPathWaypoint(1f, 0f, 2f),
                                new RealtimeMovementPathWaypoint(4f, 0f, 5f)
                            }))),
                    RealtimeProtocol.MovementInputChannel,
                    DeliveryMethod.Sequenced);
                yield return Control(
                    "TrySendItemOperation",
                    hub => hub.Items.TrySendItemOperation(ItemIntent()),
                    () => RealtimeProtocol.EncodeItemOperationIntent(ItemIntent()));
                yield return Control(
                    "TrySendCorpseInteraction",
                    hub => hub.Corpses.TrySendCorpseInteraction(CorpseIntent()),
                    () => RealtimeProtocol.EncodeCorpseInteractionIntent(CorpseIntent()));
                yield return Control(
                    "TrySendWorldInteraction",
                    hub => hub.WorldInteractions.TrySendWorldInteraction(WorldIntent()),
                    () => RealtimeProtocol.EncodeWorldInteractionIntent(WorldIntent()));
                yield return Control(
                    "TrySendAdminIntent",
                    hub => hub.Admin.TrySendAdminIntent(AdminIntent()),
                    () => RealtimeProtocol.EncodeAdminIntent(AdminIntent()));
                yield return Control(
                    "TrySendAppearanceIntent",
                    hub => hub.Looks.TrySendAppearanceIntent(AppearanceIntent()),
                    () => RealtimeProtocol.EncodeAppearanceIntent(AppearanceIntent()));
                yield return Control(
                    "TrySendQuestIntent",
                    hub => hub.Quests.TrySendQuestIntent(QuestIntent()),
                    () => RealtimeProtocol.EncodeQuestIntent(QuestIntent()));
                yield return Control(
                    "TrySendCombatIntent",
                    hub => hub.Combat.TrySendCombatIntent(CombatIntent()),
                    () => RealtimeProtocol.EncodeCombatIntent(CombatIntent()));
            }
        }

        private static IEnumerable<TestCaseData> RefusedCases
        {
            get
            {
                foreach (var item in SendCases)
                {
                    foreach (var state in new[] { "never joined", "joining", "leaving", "ended" })
                    {
                        yield return new TestCaseData(item, state).SetName(item.Name + "IsRefusedWhen(" + state + ")");
                    }
                }
            }
        }

        // A call without an intent, or with inputs that do not fit one batch.
        private static IEnumerable<TestCaseData> InvalidArgumentCases
        {
            get
            {
                yield return Invalid("no inputs", hub => hub.Movement.TrySendMovementInputs(null, null));
                yield return Invalid("an empty batch", hub => hub.Movement.TrySendMovementInputs(Array.Empty<PlayerMovementInput>(), null));
                yield return Invalid(
                    "five inputs",
                    hub => hub.Movement.TrySendMovementInputs(
                        Enumerable.Range(1, RealtimeProtocol.MaximumInputBatchSize + 1)
                            .Select(index => new PlayerMovementInput((uint)index, (uint)index, null))
                            .ToArray(),
                        null));
                yield return Invalid("no item intent", hub => hub.Items.TrySendItemOperation(null));
                yield return Invalid("no corpse intent", hub => hub.Corpses.TrySendCorpseInteraction(null));
                yield return Invalid("no world intent", hub => hub.WorldInteractions.TrySendWorldInteraction(null));
                yield return Invalid("no admin intent", hub => hub.Admin.TrySendAdminIntent(null));
                yield return Invalid("no appearance intent", hub => hub.Looks.TrySendAppearanceIntent(null));
                yield return Invalid("no quest intent", hub => hub.Quests.TrySendQuestIntent(null));
                yield return Invalid("no combat intent", hub => hub.Combat.TrySendCombatIntent(null));
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

        [TestCaseSource(nameof(SendCases))]
        public void ASendWhileJoinedPutsTheEncodedIntentOnItsChannel(SendCase item)
        {
            harness.JoinSession();
            var expected = item.Expected();

            Assert.That(item.Send(harness.Hub), Is.True);

            var sent = harness.Transport.Sent.Single();
            Assert.That(sent.Packet, Is.EqualTo(expected));
            Assert.That((sent.Channel, sent.DeliveryMethod), Is.EqualTo((item.Channel, item.DeliveryMethod)));
            // Movement input wakes the transport at once instead of on its next update.
            var expectedCalls = item.Channel == RealtimeProtocol.MovementInputChannel
                ? new[] { "Send " + sent, "TriggerUpdate" }
                : new[] { "Send " + sent };
            Assert.That(harness.Timeline, Is.EqualTo(expectedCalls));
            Assert.That(
                (harness.Hub.Diagnostics.RealtimePacketsSent, harness.Hub.Diagnostics.RealtimeBytesSent),
                Is.EqualTo((1L, (long)expected.Length)));
        }

        // A send needs the Joined state; leaving counts as not joined.
        [TestCaseSource(nameof(RefusedCases))]
        public void ASendOutsideAJoinedSessionIsRefused(SendCase item, string state)
        {
            switch (state)
            {
                case "joining":
                    harness.BeginJoinAndConnect();
                    break;
                case "leaving":
                    harness.JoinSession();
                    Assert.That(harness.BeginLeave(), Is.True);
                    break;
                case "ended":
                    harness.JoinSession();
                    harness.Hub.Session.Abort();
                    break;
            }

            harness.Clear();
            var sentBefore = harness.Hub.Diagnostics.RealtimePacketsSent;

            Assert.That(item.Send(harness.Hub), Is.False);

            Assert.That(harness.Transport.Sent, Is.Empty);
            Assert.That(harness.Timeline, Is.Empty);
            Assert.That(harness.Hub.Diagnostics.RealtimePacketsSent, Is.EqualTo(sentBefore));
        }

        [TestCaseSource(nameof(InvalidArgumentCases))]
        public void ASendWithoutAValidIntentIsRefused(Func<RealtimeSimulationClient, bool> send)
        {
            harness.JoinSession();

            Assert.That(send(harness.Hub), Is.False);

            Assert.That(harness.Transport.Sent, Is.Empty);
            Assert.That(harness.Timeline, Is.Empty);
        }

        // A message that the encoder refuses is logged and not sent; the session goes on. A
        // batch may carry only the path of its newest input.
        [TestCase("an item operation", "[CLIENT] Item operation could not be encoded: Item operation intent is invalid.")]
        [TestCase("a path that is not the newest input's", "[CLIENT] Movement input could not be encoded: Movement input batch is invalid.")]
        public void AMessageThatCannotBeEncodedIsRefusedAndLogged(string message, string log)
        {
            harness.JoinSession();

            var sent = message == "an item operation"
                ? harness.Hub.Items.TrySendItemOperation(UnencodableItemIntent())
                : harness.Hub.Movement.TrySendMovementInputs(
                    new[] { new PlayerMovementInput(1, 10, Route), new PlayerMovementInput(2, 11, null) },
                    Route);

            Assert.That(sent, Is.False);
            Assert.That(harness.Timeline, Is.Empty, "Nothing is sent and the transport is not woken.");
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joined));
            var failures = harness.TakeFailures();
            Assert.That(failures.Length, Is.EqualTo(1));
            Assert.That(failures[0], Does.StartWith(log));
        }

        // A short scripted session: what the counters show while joined, and that every ending
        // resets them.
        [Test]
        public void TheDiagnosticsCountTheSessionAndResetWhenItEnds()
        {
            harness.JoinSession();
            var spawn = RealtimeProtocol.EncodeEntitySpawn(EntitySpawn(PlayerEntityId));
            var first = RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(5, 100));
            var second = RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerSnapshot(8, 110));
            var remote = RealtimeProtocol.EncodeSimulationSnapshot(RemoteSnapshot(8, 110));
            harness.Now = 101f;
            harness.Queue(spawn);
            harness.Queue(first);
            harness.Frame();
            harness.Now = 101.5f;
            harness.Queue(second);
            harness.Queue(remote);
            harness.Frame();
            harness.Now = 101.75f;
            var intent = RealtimeProtocol.EncodeCombatIntent(CombatIntent());
            Assert.That(harness.Hub.Combat.TrySendCombatIntent(CombatIntent()), Is.True);

            Assert.That(
                new[]
                {
                    harness.Hub.Diagnostics.RealtimePacketsReceived,
                    harness.Hub.Diagnostics.RealtimeBytesReceived,
                    harness.Hub.Diagnostics.RealtimePacketsSent,
                    harness.Hub.Diagnostics.RealtimeBytesSent,
                    harness.Hub.Diagnostics.SnapshotPacketsReceived,
                    harness.Hub.Diagnostics.SnapshotFramesReceived,
                    harness.Hub.Diagnostics.EstimatedMissingSnapshotFrames,
                    harness.Hub.Diagnostics.LatestServerTick
                },
                Is.EqualTo(new[]
                {
                    4L,
                    spawn.Length + first.Length + second.Length + remote.Length,
                    1L,
                    intent.Length,
                    3L,
                    2L,
                    2L,
                    110L
                }));
            Assert.That(harness.Hub.Diagnostics.LatestSnapshotAgeSeconds, Is.EqualTo(0.25f));

            Assert.That(harness.BeginLeave(), Is.True);
            harness.Deliver(RealtimeProtocol.EncodeLeaveAccepted(20));

            Assert.That(
                new[]
                {
                    harness.Hub.Diagnostics.RealtimePacketsReceived,
                    harness.Hub.Diagnostics.RealtimeBytesReceived,
                    harness.Hub.Diagnostics.RealtimePacketsSent,
                    harness.Hub.Diagnostics.RealtimeBytesSent,
                    harness.Hub.Diagnostics.SnapshotPacketsReceived,
                    harness.Hub.Diagnostics.SnapshotFramesReceived,
                    harness.Hub.Diagnostics.EstimatedMissingSnapshotFrames,
                    harness.Hub.Diagnostics.LatestServerTick
                },
                Is.All.Zero);
            Assert.That(harness.Hub.Diagnostics.LatestSnapshotAgeSeconds, Is.EqualTo(-1f));
            Assert.That(harness.Hub.Diagnostics.RoundTripTimeMilliseconds, Is.Zero);
        }

        [Test]
        public void TheRoundTripTimeIsTheTransportsOnlyWhileConnected()
        {
            harness.Transport.ReportedRoundTripTime = 35;
            Assert.That(harness.BeginJoin(), Is.True);
            Assert.That(harness.Hub.Diagnostics.RoundTripTimeMilliseconds, Is.Zero);

            harness.Transport.QueueConnected();
            harness.Frame();
            Assert.That(harness.Hub.Diagnostics.RoundTripTimeMilliseconds, Is.EqualTo(35));

            harness.Hub.Session.Abort();
            Assert.That(harness.Hub.Diagnostics.RoundTripTimeMilliseconds, Is.Zero);
            Assert.That(harness.StepJoin(), Is.False);
            harness.TakeFailures();
        }

        private static SendCase Control(string name, Func<RealtimeSimulationClient, bool> send, Func<byte[]> expected)
        {
            return new SendCase(name, send, expected, RealtimeProtocol.ControlChannel, DeliveryMethod.ReliableOrdered);
        }

        private static TestCaseData Invalid(string name, Func<RealtimeSimulationClient, bool> send)
        {
            return new TestCaseData(send).SetName("ASendWithoutAValidIntentIsRefused(" + name + ")");
        }

        // One send method, the packet it must produce, and where it must go.
        public sealed class SendCase
        {
            public SendCase(
                string name,
                Func<RealtimeSimulationClient, bool> send,
                Func<byte[]> expected,
                byte channel,
                DeliveryMethod deliveryMethod)
            {
                Name = name;
                Send = send;
                Expected = expected;
                Channel = channel;
                DeliveryMethod = deliveryMethod;
            }

            public string Name { get; }

            public Func<RealtimeSimulationClient, bool> Send { get; }

            public Func<byte[]> Expected { get; }

            public byte Channel { get; }

            public DeliveryMethod DeliveryMethod { get; }

            public override string ToString()
            {
                return Name;
            }
        }
    }
}
