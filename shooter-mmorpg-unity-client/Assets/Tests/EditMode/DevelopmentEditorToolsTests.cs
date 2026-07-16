using NUnit.Framework;
using ShooterMmo.Editor;
using ShooterMmo.WorldData.Editor.Items;

namespace ShooterMmo.Tests.EditMode
{
    public sealed class DevelopmentEditorToolsTests
    {
        [Test]
        public void EveryEditorToolUsesTheSharedMenuRoot()
        {
            Assert.That(
                InventoryItemGrantWindow.MenuPath,
                Does.StartWith("Shooter MMO/Tools/"));
            Assert.That(
                WorldCollisionBaker.MenuPath,
                Does.StartWith("Shooter MMO/Tools/"));
            Assert.That(
                ItemCatalogEditorWindow.MenuPath,
                Does.StartWith("Shooter MMO/Tools/"));
        }

        [Test]
        public void ItemToolProtocolParsesResponseAmongDotnetLogs()
        {
            const string output = "info: startup\n"
                + "SHOOTER_MMO_DEV_ITEMS_JSON:{\"success\":true,\"message\":\"Loaded.\","
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
                "dotnet failed before AuthService started",
                out var response,
                out var error);

            Assert.That(parsed, Is.False);
            Assert.That(response, Is.Null);
            Assert.That(error, Does.Contain("dotnet build ShooterMmo.slnx"));
        }
    }
}
