using System.Collections.Generic;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using static Mmorpg.Client.Tests.EditMode.RealtimeHubMessages;

namespace Mmorpg.Client.Tests.EditMode
{
    // The progression channel keeps the controlled character's latest progression from the worker,
    // raises it and the level-ups of others, forgets it with its session, and fails the session on
    // a packet it cannot read.
    public sealed class RealtimeProgressionChannelTests
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
        public void TheLatestProgressionIsKeptAndRaisedInOrder()
        {
            harness.JoinSession();
            Assert.That(harness.Hub.Progression.Progression, Is.Null, "A joined session has none before the first.");
            var seen = new List<(ushort Level, bool IsKept)>();
            harness.Hub.Progression.ProgressionChanged += change =>
                seen.Add((change.Level, ReferenceEquals(change, harness.Hub.Progression.Progression)));

            harness.Deliver(RealtimeProtocol.EncodeOwnerProgressionChanged(Progression(12, 0)));
            harness.Deliver(RealtimeProtocol.EncodeOwnerProgressionChanged(Progression(13, 600)));

            Assert.That(seen, Is.EqualTo(new[] { ((ushort)12, true), ((ushort)13, true) }));
            var latest = harness.Hub.Progression.Progression;
            Assert.That(
                (latest.Level, latest.Experience, latest.ExperienceToNextLevel, latest.ExperienceGained, latest.Reason),
                Is.EqualTo(((ushort)13, 3400L, 9800L, 600L, RealtimeProgressionChangeReason.MobKill)));
        }

        [Test]
        public void ALevelUpOfAnotherCharacterIsRaisedAndLeavesTheOwnProgression()
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeOwnerProgressionChanged(Progression(12, 0)));
            RealtimeEntityLeveledUp raised = null;
            harness.Hub.Progression.EntityLeveledUp += levelUp => raised = levelUp;

            harness.Deliver(RealtimeProtocol.EncodeEntityLeveledUp(LevelUp(PlayerEntityId, 30)));

            Assert.That((raised.EntityId, raised.Level), Is.EqualTo((PlayerEntityId, (ushort)30)));
            Assert.That(harness.Hub.Progression.Progression.Level, Is.EqualTo(12));
        }

        [Test]
        public void TheProgressionIsForgottenWhenTheSessionEnds()
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeOwnerProgressionChanged(Progression()));
            Assert.That(harness.Hub.Progression.Progression, Is.Not.Null);

            Assert.That(harness.BeginLeave(), Is.True);
            harness.Deliver(RealtimeProtocol.EncodeLeaveAccepted(20));

            Assert.That(harness.Hub.Progression.Progression, Is.Null);
        }

        [Test]
        public void TheProgressionIsForgottenWhenTheSessionIsAborted()
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeOwnerProgressionChanged(Progression()));

            harness.Hub.Session.Abort();

            Assert.That(harness.Hub.Progression.Progression, Is.Null);
        }

        // The snapshot that overtook JoinAccepted on the owner channel waits for it, so the join's
        // reset of the session state keeps it.
        [Test]
        public void ASnapshotThatOvertookTheJoinAnswerIsKeptAfterTheJoin()
        {
            harness.BeginJoinAndConnect();
            harness.Deliver(RealtimeProtocol.EncodeOwnerProgressionChanged(Progression(12, 0)));
            Assert.That(harness.Hub.Progression.Progression, Is.Null);

            harness.Deliver(RealtimeProtocol.EncodeJoinAccepted(RealtimeHubHarness.JoinAccepted()));

            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joined));
            Assert.That(harness.Hub.Progression.Progression.Level, Is.EqualTo(12));
        }

        [Test]
        public void AnUnreadableProgressionFailsTheSession()
        {
            harness.JoinSession();
            var packet = RealtimeProtocol.EncodeOwnerProgressionChanged(Progression());
            packet[7] = 0;
            packet[8] = 0;

            harness.Deliver(packet);

            Assert.That(harness.Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Disconnected));
            Assert.That(harness.Hub.Progression.Progression, Is.Null);
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "CLIENT invalid_owner_progression" }));
        }
    }
}
