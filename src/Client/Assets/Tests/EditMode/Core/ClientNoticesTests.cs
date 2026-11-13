using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Ui;
using Mmorpg.WorldData.Text;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // What the next entry screen tells the player: why the client left its World, once, and how
    // long the body of a character that left still stands, counting down without blocking a join.
    public sealed class ClientNoticesTests
    {
        private float now;

        [SetUp]
        public void SetUp()
        {
            now = 50f;
        }

        [Test]
        public void ANoticeShowsOnceAndANewerOneReplacesIt()
        {
            var notices = new ClientNotices(() => now);

            notices.Post("session_revoked");
            notices.Post("reconnect_failed");

            Assert.That(notices.Take(), Is.EqualTo("reconnect_failed"));
            Assert.That(notices.Take(), Is.Empty);
        }

        // Rule 7: the time is a lower bound, so the text goes when it is up and nothing more is
        // assumed.
        [Test]
        public void TheBodyOfACharacterThatLeftCountsDownAndGoesAtZero()
        {
            var notices = new ClientNotices(() => now);
            notices.RememberBody("character-a", 20);

            Assert.That(BodyOf(notices), Is.EqualTo(("character-a", 20)));
            now += 0.4f;
            Assert.That(BodyOf(notices), Is.EqualTo(("character-a", 20)), "Whole seconds, rounded up.");
            now += 12.1f;
            Assert.That(BodyOf(notices), Is.EqualTo(("character-a", 8)));
            now = 70f;
            Assert.That(notices.TryGetBody(out _, out _), Is.False);
            now -= 5f;
            Assert.That(notices.TryGetBody(out _, out _), Is.False, "A body that is gone stays gone.");

            notices.RememberBody("character-b", 0);
            Assert.That(notices.TryGetBody(out _, out _), Is.False, "A leave with no body time keeps nothing.");
        }

        // CharacterSelect names the body on the chosen character, its own or the one that keeps
        // another character out, and Enter World stays open either way.
        [Test]
        public void CharacterSelectShowsTheBodyAndStillLetsEveryCharacterEnter()
        {
            var texts = FixtureTexts.Ui();
            var state = new CharacterSelectState();
            state.SetCharacters(new[] { Character("a", "Mira"), Character("b", "Torvald") }, "a");
            state.SetShards(new[] { new ShardResponse { id = "one", displayName = "One", isOnline = true } }, null);
            var seconds = MessageArgument.Text("seconds", "17");

            Assert.That(
                AccountEntryPresentation.BodyNote(state, "A", 17, texts),
                Is.EqualTo(texts.Format("ui/characters/body-in-world", seconds)),
                "Ids compare without case.");
            Assert.That(AccountEntryPresentation.EnterWorldBlocker(state, texts), Is.Null);

            state.SelectCharacter("b");
            Assert.That(
                AccountEntryPresentation.BodyNote(state, "a", 17, texts),
                Is.EqualTo(texts.Format(
                    "ui/characters/other-body-in-world",
                    MessageArgument.Text("character", "Mira"),
                    seconds)));
            Assert.That(AccountEntryPresentation.EnterWorldBlocker(state, texts), Is.Null);

            Assert.That(AccountEntryPresentation.BodyNote(state, "a", 0, texts), Is.Null);
            Assert.That(AccountEntryPresentation.BodyNote(state, "of-another-account", 17, texts), Is.Null);
            Assert.That(AccountEntryPresentation.BodyNote(state, null, 17, texts), Is.Null);
        }

        private static (string, int) BodyOf(ClientNotices notices)
        {
            Assert.That(notices.TryGetBody(out var characterId, out var secondsLeft), Is.True);
            return (characterId, secondsLeft);
        }

        private static CharacterResponse Character(string id, string name)
        {
            return new CharacterResponse { id = id, name = name, bodyModel = "Human_Male" };
        }
    }
}
