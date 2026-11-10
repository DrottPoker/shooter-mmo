using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;
using Mmorpg.WorldData.Text;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // Text catalogs for tests: the bundled one that the client configuration lists, and ones
    // built from a test catalog's own texts, as the content compilers build the real tables.
    internal static class FixtureTexts
    {
        public static TextCatalog Bundled(ClientConfig config)
        {
            Assert.That(ClientTexts.TryLoad(config.TextTables, out var texts, out var error), Is.True, error);
            return texts;
        }

        public static TextCatalog For(ItemCatalogRuntimeDocument items)
        {
            return TextCatalog.Create(new[] { ContentText.FromItemCatalog(items) });
        }

        public static TextCatalog For(QuestCatalogRuntimeDocument quests)
        {
            return TextCatalog.Create(new[] { ContentText.FromQuestCatalog(quests) });
        }

        // A catalog of one table whose texts the test chooses, to prove where a text comes from.
        public static TextCatalog Of(string tableId, params (string Key, string Text)[] entries)
        {
            return TextCatalog.Create(new[]
            {
                TextTable.Create(
                    tableId,
                    TextTableFormat.SourceLocale,
                    entries.Select(entry => new TextTableEntry { Key = entry.Key, Text = entry.Text }))
            });
        }
    }
}
