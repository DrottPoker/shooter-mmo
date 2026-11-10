using Mmorpg.Client.Items;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // The codes are the stable wire values the worker sends, so they are spelled out here. The
    // text is the client's text for the code, which the corpse state looks up.
    public sealed class CorpseViewClosureRuleTests
    {
        [TestCase("corpse_emptied")]
        [TestCase("corpse_expired")]
        [TestCase("combat_character_dead")]
        [TestCase("corpse_not_lootable")]
        public void IntendedClosureShowsItsTextAsANeutralNotice(string code)
        {
            var closure = CorpseViewClosureRule.Classify(code, "The text of the code.");

            Assert.That(closure.Kind, Is.EqualTo(CorpseViewClosureKind.Expected));
            Assert.That(closure.Error, Is.Null);
            Assert.That(closure.Notice, Is.EqualTo("The text of the code."));
        }

        [Test]
        public void OwnCloseNeedsNoNotice()
        {
            var closure = CorpseViewClosureRule.Classify("corpse_view_closed", "The corpse view was closed.");

            Assert.That(closure.Kind, Is.EqualTo(CorpseViewClosureKind.Expected));
            Assert.That(closure.Error, Is.Null);
            Assert.That(closure.Notice, Is.Empty);
        }

        [TestCase("corpse_invalidated")]
        [TestCase("corpse_not_found")]
        [TestCase("wrong_simulation_worker")]
        [TestCase("corpse_closed_for_a_new_reason")]
        public void InvalidationAndUnknownClosuresStayErrors(string code)
        {
            var closure = CorpseViewClosureRule.Classify(code, "The text of the code.");

            Assert.That(closure.Kind, Is.EqualTo(CorpseViewClosureKind.Failure));
            Assert.That(closure.Notice, Is.Empty);
            Assert.That(closure.Error.Code, Is.EqualTo(code));
            Assert.That(closure.Error.ToDisplayMessage(), Is.EqualTo("The text of the code."));
        }

        [TestCase(null)]
        [TestCase("")]
        public void ClosureWithoutACodeIsAnError(string code)
        {
            var closure = CorpseViewClosureRule.Classify(code, string.Empty);

            Assert.That(closure.Kind, Is.EqualTo(CorpseViewClosureKind.Failure));
            Assert.That(closure.Error.Code, Is.EqualTo("corpse_error"));
            Assert.That(closure.Error.ToDisplayMessage(), Is.EqualTo("The corpse interaction failed."));
        }
    }
}
