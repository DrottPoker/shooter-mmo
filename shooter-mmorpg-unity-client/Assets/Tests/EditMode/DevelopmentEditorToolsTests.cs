using System;
using NUnit.Framework;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Worlds;
using Mmorpg.WorldData.Editor.Items;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class DevelopmentEditorToolsTests
    {
        [Test]
        public void EveryEditorToolUsesTheSharedMenuRoot()
        {
            Assert.That(
                InventoryItemGrantWindow.MenuPath,
                Does.StartWith("MMORPG/Tools/"));
            Assert.That(
                WorldCollisionBaker.MenuPath,
                Does.StartWith("MMORPG/Tools/"));
            Assert.That(
                WorldCollisionBaker.BakeBuildWorldScenesMenuPath,
                Does.StartWith("MMORPG/Tools/"));
            Assert.That(
                ItemCatalogEditorWindow.MenuPath,
                Does.StartWith("MMORPG/Tools/"));
        }

        [Test]
        public void BuildWorldBakeOpensASharedSceneOnceForEveryWorld()
        {
            var plans = WorldCollisionBaker.PlanBuildWorldBakes(
                new[]
                {
                    new WorldSceneCatalogEntry { worldId = "world-a", sceneName = "SharedArena" },
                    new WorldSceneCatalogEntry { worldId = "world-b", sceneName = "SharedArena" },
                    new WorldSceneCatalogEntry { worldId = "world-c", sceneName = "Solo" }
                },
                new[] { "Assets/Scenes/SharedArena.unity", "Assets/Scenes/Solo.unity" });

            Assert.That(plans.Count, Is.EqualTo(2));
            Assert.That(plans[0].ScenePath, Is.EqualTo("Assets/Scenes/SharedArena.unity"));
            Assert.That(plans[0].WorldIds, Is.EqualTo(new[] { "world-a", "world-b" }));
            Assert.That(plans[1].WorldIds, Is.EqualTo(new[] { "world-c" }));
            Assert.Throws<InvalidOperationException>(() => WorldCollisionBaker.PlanBuildWorldBakes(
                new[] { new WorldSceneCatalogEntry { worldId = "world-d", sceneName = "Missing" } },
                new[] { "Assets/Scenes/Solo.unity" }));
        }

        [Test]
        public void ItemToolProtocolParsesResponseAmongDotnetLogs()
        {
            const string output = "info: startup\n"
                + "MMORPG_DEV_ITEMS_JSON:{\"success\":true,\"message\":\"Loaded.\","
                + "\"data\":{\"characters\":[{\"characterId\":\"character-1\","
                + "\"characterName\":\"Hero\",\"accountUsername\":\"player\","
                + "\"isOnline\":false,\"itemCount\":0,\"recoveryDeliveryCount\":0,"
                + "\"itemStateRevision\":0,\"carriedWeight\":0,\"carryCapacity\":200}],"
                + "\"definitions\":[{\"definitionId\":\"material.iron_ore\","
                + "\"displayName\":\"Iron Ore\",\"categoryId\":\"material\","
                + "\"maximumStackSize\":100,\"unitWeight\":6,"
                + "\"secureContainerEligible\":true}],"
                + "\"packages\":[{\"packageId\":\"encumbrance_140\","
                + "\"displayName\":\"Encumbrance 140% Pack\","
                + "\"description\":\"Hard cap.\",\"requiresEmptyCharacter\":true}]}}";

            var parsed = DevelopmentItemToolCli.TryParseResponse(
                output,
                out var response,
                out var error);

            Assert.That(parsed, Is.True, error);
            Assert.That(response.success, Is.True);
            Assert.That(response.data.characters[0].carryCapacity, Is.EqualTo(200));
            Assert.That(
                response.data.definitions[0].maximumStackSize,
                Is.EqualTo(100));
            Assert.That(
                response.data.packages[0].requiresEmptyCharacter,
                Is.True);
        }

        [Test]
        public void ItemToolProtocolRejectsMissingMachineReadableResponse()
        {
            var parsed = DevelopmentItemToolCli.TryParseResponse(
                "dotnet failed before GameService started",
                out var response,
                out var error);

            Assert.That(parsed, Is.False);
            Assert.That(response, Is.Null);
            Assert.That(error, Does.Contain("dotnet build Mmorpg.slnx"));
        }
    }
}
