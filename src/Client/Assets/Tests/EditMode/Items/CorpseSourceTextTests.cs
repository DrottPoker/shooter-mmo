using System;
using Mmorpg.Client.Items;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Text;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // A corpse says who left it by a character name or by a Mob's actor definition, and only
    // the client turns the definition into a name.
    public sealed class CorpseSourceTextTests
    {
        [Test]
        public void ACharacterCorpseKeepsItsNameAndAMobCorpseIsNamedFromTheTextCatalog()
        {
            var texts = FixtureTexts.DevelopmentActors();

            Assert.That(
                CorpseSourceText.Name(RealtimeCorpseSource.Character("Fallen Hero"), texts),
                Is.EqualTo("Fallen Hero"));
            Assert.That(
                CorpseSourceText.Name(RealtimeCorpseSource.Actor("mob.feral_wolf"), texts),
                Is.EqualTo("Feral Wolf"));
            Assert.That(
                CorpseSourceText.Name(RealtimeCorpseSource.Actor("mob.unknown"), texts),
                Is.EqualTo("[actor/mob.unknown/name]"));
            Assert.That(
                CorpseSourceText.Name(RealtimeCorpseSource.Actor("Not A Key"), texts),
                Is.EqualTo("Not A Key"));
            Assert.That(CorpseSourceText.Name(null, texts), Is.Empty);
        }

        [Test]
        public void TheCorpseStateNamesAMobCorpseOnceItHasTheTexts()
        {
            var corpseId = Guid.NewGuid();
            var named = new CorpseClientState();
            named.UseTexts(FixtureTexts.DevelopmentActors());
            var unnamed = new CorpseClientState();

            Apply(named, corpseId);
            Apply(unnamed, corpseId);

            Assert.That(named.NearbyCorpses[0].SourceDisplayName, Is.EqualTo("Feral Wolf"));
            Assert.That(unnamed.NearbyCorpses[0].SourceDisplayName, Is.EqualTo("[actor/mob.feral_wolf/name]"));
        }

        [Test]
        public void TwoSourcesAreTheSameOnlyWithTheSameNameAndDefinition()
        {
            Assert.That(
                CorpseSourceText.AreSame(
                    RealtimeCorpseSource.Actor("mob.feral_wolf"),
                    RealtimeCorpseSource.Actor("mob.feral_wolf")),
                Is.True);
            Assert.That(
                CorpseSourceText.AreSame(
                    RealtimeCorpseSource.Actor("mob.feral_wolf"),
                    RealtimeCorpseSource.Actor("mob.grey_wolf")),
                Is.False);
            Assert.That(
                CorpseSourceText.AreSame(
                    RealtimeCorpseSource.Character("mob.feral_wolf"),
                    RealtimeCorpseSource.Actor("mob.feral_wolf")),
                Is.False);
            Assert.That(CorpseSourceText.AreSame(null, null), Is.True);
            Assert.That(CorpseSourceText.AreSame(null, RealtimeCorpseSource.Character("Ada")), Is.False);
        }

        private static void Apply(CorpseClientState state, Guid corpseId)
        {
            var chunk = new RealtimeCorpsePresenceSnapshotChunk(
                1,
                0,
                1,
                new[]
                {
                    new RealtimeCorpsePresence(
                        corpseId,
                        1f,
                        2f,
                        3f,
                        45f,
                        CorpseProtocolFixtures.ExpiresAtUnixMilliseconds,
                        false,
                        true,
                        RealtimeCorpseSource.Actor("mob.feral_wolf"),
                        "corpse.mob.feral_wolf",
                        null)
                });
            Assert.That(
                state.ApplyPresenceChunk(chunk),
                Is.EqualTo(CorpseStateApplyResult.Applied));
        }
    }
}
