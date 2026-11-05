using System.IO;
using Mmorpg.Client.Quests;
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

        public static ClientQuestCatalog Load()
        {
            Assert.That(
                ClientQuestCatalog.TryParse(File.ReadAllText(RuntimePath), out var catalog, out var error),
                Is.True,
                error);
            return catalog;
        }
    }
}
