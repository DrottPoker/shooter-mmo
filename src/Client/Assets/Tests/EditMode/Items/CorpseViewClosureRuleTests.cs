using Mmorpg.Client.Items;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // The codes are the stable wire values the worker sends, so they are spelled out here.
    public sealed class CorpseViewClosureRuleTests
    {
        [TestCase("corpse_emptied", "The corpse is empty.")]
        [TestCase("corpse_expired", "The corpse expired and is no longer available.")]
        [TestCase("combat_character_dead", "The character died.")]
        public void IntendedClosureShowsTheWorkerMessageAsANeutralNotice(string code, string message)
        {
            var closure = CorpseViewClosureRule.Classify(code, message);

            Assert.That(closure.Kind, Is.EqualTo(CorpseViewClosureKind.Expected));
            Assert.That(closure.Error, Is.Null);
            Assert.That(closure.Notice, Is.EqualTo(message));
        }

        [TestCase("corpse_emptied", "The corpse is empty.")]
        [TestCase("corpse_expired", "The corpse expired.")]
        [TestCase("combat_character_dead", "The character died.")]
        public void IntendedClosureWithoutAMessageFallsBackToItsOwnNotice(string code, string notice)
        {
            var closure = CorpseViewClosureRule.Classify(code, " ");

            Assert.That(closure.Kind, Is.EqualTo(CorpseViewClosureKind.Expected));
            Assert.That(closure.Notice, Is.EqualTo(notice));
        }

        [Test]
        public void OwnCloseNeedsNoNotice()
        {
            var closure = CorpseViewClosureRule.Classify("corpse_view_closed", "The corpse view was closed.");

            Assert.That(closure.Kind, Is.EqualTo(CorpseViewClosureKind.Expected));
            Assert.That(closure.Error, Is.Null);
            Assert.That(closure.Notice, Is.Empty);
        }

        [TestCase("corpse_invalidated", "The corpse is no longer available.")]
        [TestCase("corpse_not_found", "The live Mob corpse is not active.")]
        [TestCase("wrong_simulation_worker", "The live Mob corpse belongs to another Shard.")]
        [TestCase("corpse_closed_for_a_new_reason", "A reason this client does not know.")]
        public void InvalidationAndUnknownClosuresStayErrors(string code, string message)
        {
            var closure = CorpseViewClosureRule.Classify(code, message);

            Assert.That(closure.Kind, Is.EqualTo(CorpseViewClosureKind.Failure));
            Assert.That(closure.Notice, Is.Empty);
            Assert.That(closure.Error.Code, Is.EqualTo(code));
            Assert.That(closure.Error.ToDisplayMessage(), Is.EqualTo(code + ": " + message));
        }

        [TestCase(null)]
        [TestCase("")]
        public void ClosureWithoutACodeIsAnError(string code)
        {
            var closure = CorpseViewClosureRule.Classify(code, string.Empty);

            Assert.That(closure.Kind, Is.EqualTo(CorpseViewClosureKind.Failure));
            Assert.That(
                closure.Error.ToDisplayMessage(),
                Is.EqualTo("corpse_error: The corpse interaction failed."));
        }
    }
}
