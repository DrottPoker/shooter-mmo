using System.IO;
using Mmorpg.Client.Core;
using Mmorpg.Client.Quests;
using Mmorpg.WorldData.Text;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The test-owned quest catalog in tests/Fixtures/Quests, shared with the .NET tests: the
    // wolves hunt, the errand that requires it, the repeatable ore run, and the trophies, all at
    // Mira.
    internal static class FixtureQuestCatalog
    {
        public const string Mira = "npc.city_services";
        public const string Wolves = "quest.fixture.wolves";
        public const string Errand = "quest.fixture.errand";
        public const string OreRun = "quest.fixture.ore_run";
        public const string Trophies = "quest.fixture.trophies";

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
                    "Quests",
                    "generated",
                    "Quests",
                    "core.quest-catalog.json"));
            }
        }

        // The texts of the fixture quests, checked in next to the compiled fixture catalog.
        public static string TextTablePath
        {
            get
            {
                return Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(RuntimePath),
                    "..",
                    "Text",
                    "en",
                    "quests.text-table.json"));
            }
        }

        // More tables join the catalog's texts, such as the messages of a test.
        public static ClientQuestCatalog Load(params TextTableDocument[] moreTables)
        {
            // The fixture quests are not in the bundled text tables, so their own table and
            // the development actors, which name the Mob of a quest drop, make the catalog.
            var json = File.ReadAllText(RuntimePath);
            Assert.That(
                ClientTexts.TryParse(File.ReadAllText(TextTablePath), out var quests, out var error),
                Is.True,
                error);
            var texts = FixtureTexts.WithDevelopmentActors(quests, moreTables);
            Assert.That(
                ClientQuestCatalog.TryParse(json, texts, out var catalog, out error),
                Is.True,
                error);
            return catalog;
        }
    }
}
