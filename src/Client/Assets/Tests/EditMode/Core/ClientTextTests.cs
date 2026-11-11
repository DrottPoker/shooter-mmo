using Mmorpg.Client.Core;
using Mmorpg.WorldData.Text;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // Rule code names a text by its key and values, and only the window that shows it reads
    // the sentence from the text catalog.
    public sealed class ClientTextTests
    {
        private TextCatalog texts;

        [SetUp]
        public void SetUp()
        {
            texts = TextCatalog.Create(new[]
            {
                FixtureTexts.TableOf(
                    TextTableIds.Ui,
                    ("ui/test/plain", "The slot is taken."),
                    ("ui/test/count", "{count} left in {container}."),
                    ("ui/test/slot", "The item in {slot} blocks it."),
                    ("ui/test/nested", "The swap is not possible: {reason}")),
                FixtureTexts.TableOf(TextTableIds.Items, (TextKeys.EquipmentSlotName("off_hand"), "Off Hand"))
            });
        }

        [Test]
        public void NoneIsEmptyAndResolvesToNothing()
        {
            Assert.That(ClientText.None.IsEmpty, Is.True);
            Assert.That(default(ClientText).IsEmpty, Is.True);
            Assert.That(ClientText.None.Key, Is.Empty);
            Assert.That(ClientText.None.Resolve(texts), Is.Empty);
            Assert.That(ClientText.Literal(string.Empty).IsEmpty, Is.True);
        }

        [Test]
        public void AKeyWithoutArgumentsResolvesToItsText()
        {
            var text = ClientText.Of("ui/test/plain");

            Assert.That(text.IsEmpty, Is.False);
            Assert.That(text.Key, Is.EqualTo("ui/test/plain"));
            Assert.That(text.Arguments, Is.Empty);
            Assert.That(text.Resolve(texts), Is.EqualTo("The slot is taken."));
        }

        [Test]
        public void TextAndNumberArgumentsFillTheirPlaceholders()
        {
            var text = ClientText.Of(
                "ui/test/count",
                ClientTextArgument.Number("count", 1250),
                ClientTextArgument.Text("container", "the Bank"));

            Assert.That(text.Resolve(texts), Is.EqualTo("1250 left in the Bank."));
        }

        [Test]
        public void ATextKeyArgumentShowsTheTextOfItsKey()
        {
            var text = ClientText.Of(
                "ui/test/slot",
                ClientTextArgument.TextKey("slot", TextKeys.EquipmentSlotName("off_hand")));

            Assert.That(text.Resolve(texts), Is.EqualTo("The item in Off Hand blocks it."));
        }

        [Test]
        public void ANestedArgumentShowsTheResolvedInnerText()
        {
            var inner = ClientText.Of(
                "ui/test/slot",
                ClientTextArgument.TextKey("slot", TextKeys.EquipmentSlotName("off_hand")));
            var text = ClientText.Of("ui/test/nested", ClientTextArgument.Nested("reason", inner));

            Assert.That(
                text.Resolve(texts),
                Is.EqualTo("The swap is not possible: The item in Off Hand blocks it."));
        }

        [Test]
        public void ANestedItemReasonReadsAsTheCheckedInSentences()
        {
            var text = ClientText.Of(
                "ui/items/move-undone",
                ClientTextArgument.Nested("reason", ClientText.Of("ui/items/inventory-full")));

            Assert.That(
                text.Resolve(FixtureTexts.Ui()),
                Is.EqualTo("Your item move no longer fits the inventory and was undone: "
                    + "Your inventory has no room for the item."));
        }

        [Test]
        public void ALiteralShowsItsSentenceAsItIs()
        {
            var text = ClientText.Literal("The corpse is empty.");

            Assert.That(text.IsEmpty, Is.False);
            Assert.That(text.Key, Is.Empty);
            Assert.That(text.Resolve(texts), Is.EqualTo("The corpse is empty."));
            Assert.That(text.Resolve(null), Is.EqualTo("The corpse is empty."));
        }

        [Test]
        public void AMissingKeyShowsAsTheKeyInBrackets()
        {
            Assert.That(ClientText.Of("ui/test/missing").Resolve(texts), Is.EqualTo("[ui/test/missing]"));
            Assert.That(ClientText.Of("ui/test/plain").Resolve(null), Is.EqualTo("[ui/test/plain]"));
        }

        [Test]
        public void TextsAreEqualOnlyWithTheSameKeyAndArguments()
        {
            var first = ClientText.Of(
                "ui/test/count",
                ClientTextArgument.Number("count", 3),
                ClientTextArgument.Text("container", "the Bank"));
            var same = ClientText.Of(
                "ui/test/count",
                ClientTextArgument.Number("count", 3),
                ClientTextArgument.Text("container", "the Bank"));
            var otherValue = ClientText.Of(
                "ui/test/count",
                ClientTextArgument.Number("count", 4),
                ClientTextArgument.Text("container", "the Bank"));
            var nested = ClientText.Of("ui/test/nested", ClientTextArgument.Nested("reason", first));

            Assert.That(first, Is.EqualTo(same));
            Assert.That(first == same, Is.True);
            Assert.That(first.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            Assert.That(first, Is.Not.EqualTo(otherValue));
            Assert.That(first != otherValue, Is.True);
            Assert.That(
                nested,
                Is.EqualTo(ClientText.Of("ui/test/nested", ClientTextArgument.Nested("reason", same))));
            Assert.That(
                nested,
                Is.Not.EqualTo(ClientText.Of("ui/test/nested", ClientTextArgument.Nested("reason", otherValue))));
            Assert.That(ClientText.Of("ui/test/plain"), Is.Not.EqualTo(ClientText.Literal("ui/test/plain")));
            Assert.That(
                ClientText.Of("ui/test/slot", ClientTextArgument.TextKey("slot", "off_hand")),
                Is.Not.EqualTo(ClientText.Of("ui/test/slot", ClientTextArgument.Text("slot", "off_hand"))));
        }
    }
}
