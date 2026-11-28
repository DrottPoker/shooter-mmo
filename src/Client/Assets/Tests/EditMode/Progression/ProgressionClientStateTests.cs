using System.Collections.Generic;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using static Mmorpg.Client.Tests.EditMode.RealtimeHubMessages;

namespace Mmorpg.Client.Tests.EditMode
{
    // The progression state holds the selected character's level until the worker's first
    // progression, then every progression replaces it with one change event, and it forgets the
    // progression with its session and with a change of character.
    public sealed class ProgressionClientStateTests
    {
        private RealtimeHubHarness harness;
        private ClientSession clientSession;
        private ProgressionClientState state;
        private List<RealtimeOwnerProgressionChanged> changes;

        [SetUp]
        public void SetUp()
        {
            harness = new RealtimeHubHarness();
            clientSession = new ClientSession(RealtimeHubHarness.Settings);
            clientSession.SignIn(new AuthResponse { accountId = "account-1" });
            clientSession.Select(Character("c-1", 7), null);
            state = new ProgressionClientState(harness.Hub.Progression, harness.Hub.Session, clientSession);
            changes = new List<RealtimeOwnerProgressionChanged>();
            state.Changed += changes.Add;
        }

        [TearDown]
        public void TearDown()
        {
            state.Dispose();
            harness.Dispose();
        }

        [Test]
        public void BeforeTheFirstProgressionTheLevelIsTheSelectedCharactersOne()
        {
            Assert.That(state.HasProgression, Is.False);
            Assert.That(state.Progression, Is.Null);
            Assert.That(state.Level, Is.EqualTo(7));

            clientSession.Select(Character("c-2", 0), null);
            Assert.That(state.Level, Is.EqualTo(1), "A level below 1 counts as 1.");
            Assert.That(changes, Is.EqualTo(new RealtimeOwnerProgressionChanged[] { null }));
        }

        [Test]
        public void EveryProgressionReplacesTheStateWithOneEvent()
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeOwnerProgressionChanged(Progression(12, 0)));
            harness.Deliver(RealtimeProtocol.EncodeOwnerProgressionChanged(Progression(13, 600)));

            Assert.That(changes.Count, Is.EqualTo(2));
            Assert.That(changes[1], Is.SameAs(state.Progression));
            Assert.That((state.Level, state.Progression.ExperienceGained), Is.EqualTo((13, 600L)));
            Assert.That(state.HasProgression, Is.True);
        }

        // A lost connection forgets the progression and keeps the level it reached, so the level
        // never steps back to the character list's while the client joins again.
        [Test]
        public void TheSessionEndForgetsTheProgressionAndKeepsItsLevel()
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeOwnerProgressionChanged(Progression(12, 0)));
            changes.Clear();

            harness.Hub.Session.Abort();

            Assert.That(state.HasProgression, Is.False);
            Assert.That(state.Level, Is.EqualTo(12));
            Assert.That(changes, Is.EqualTo(new RealtimeOwnerProgressionChanged[] { null }));
        }

        [Test]
        public void AnotherCharacterStartsOverFromItsOwnLevel()
        {
            harness.JoinSession();
            harness.Deliver(RealtimeProtocol.EncodeOwnerProgressionChanged(Progression(12, 0)));
            changes.Clear();

            clientSession.Select(Character("c-2", 4), null);

            Assert.That(state.HasProgression, Is.False);
            Assert.That(state.Level, Is.EqualTo(4));
            Assert.That(changes, Is.EqualTo(new RealtimeOwnerProgressionChanged[] { null }));
        }

        // The character list read again after a session names the same character's stored level.
        [Test]
        public void ANewerCharacterListOfTheSameCharacterSetsItsLevel()
        {
            clientSession.Select(Character("c-1", 9), null);

            Assert.That(state.Level, Is.EqualTo(9));
            Assert.That(changes, Is.EqualTo(new RealtimeOwnerProgressionChanged[] { null }));
        }

        [Test]
        public void AStateWithoutASourceAppliesAndResetsByHand()
        {
            var plain = new ProgressionClientState();
            var seen = new List<RealtimeOwnerProgressionChanged>();
            plain.Changed += seen.Add;
            Assert.That(plain.Level, Is.EqualTo(1));

            plain.SetSelectedCharacterLevel(5);
            var change = Progression(6, 30);
            plain.Apply(change);
            plain.Reset();
            plain.Reset();

            Assert.That(seen, Is.EqualTo(new[] { null, change, null }));
            Assert.That(plain.Level, Is.EqualTo(6));
        }

        private static CharacterResponse Character(string id, int level)
        {
            return new CharacterResponse { id = id, name = "Mira", level = level };
        }
    }
}
