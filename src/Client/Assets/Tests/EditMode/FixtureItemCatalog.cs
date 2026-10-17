using System.IO;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Items.Presentation;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The test-owned item catalog in tests/Fixtures/ItemCatalog, shared with the .NET tests.
    // Rule tests use it so content and balance changes never break them.
    internal static class FixtureItemCatalog
    {
        public static string RuntimePath
        {
            get
            {
                return Path.GetFullPath(Path.Combine(
                    Application.dataPath,
                    "..",
                    "..",
                    "..",
                    "tests",
                    "Fixtures",
                    "ItemCatalog",
                    "generated",
                    "Items",
                    "core.item-catalog.json"));
            }
        }

        public static ClientItemCatalog Load()
        {
            Assert.That(
                ClientItemCatalog.TryParseGameplay(
                    File.ReadAllText(RuntimePath),
                    out var gameplay,
                    out var error),
                Is.True,
                error);
            var presentation = ItemPresentationCatalogCompiler.Compile(
                gameplay.Revision,
                gameplay.Definitions.Select(definition => definition.Id),
                gameplay.Definitions.Select(definition => new ItemPresentationEntry
                {
                    definitionId = definition.Id,
                    iconAddress = string.Empty,
                    localizationKey = "items." + definition.Id + ".name",
                    fallbackDisplayName = definition.DisplayName,
                    prefabPresentationKey = string.Empty
                }));
            Assert.That(
                ItemPresentationCatalogIndex.TryCreate(
                    presentation,
                    gameplay.Revision,
                    gameplay.Definitions.Select(definition => definition.Id),
                    out var index,
                    out error),
                Is.True,
                error);
            Assert.That(
                ClientItemCatalog.TryCreate(gameplay, index, out var catalog, out error),
                Is.True,
                error);
            return catalog;
        }
    }
}
