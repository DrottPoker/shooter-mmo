using System;
using Mmorpg.Client.Items;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class InventoryRefreshScopesTests
    {
        [Test]
        public void OnlyOperationsInsideTheBankRefreshTheBankAlone()
        {
            var catalog = FixtureItemCatalog.Load();
            var characterId = Guid.NewGuid();
            var bank = Container("bank");
            var inventory = Container("permanent_inventory");
            var state = new InventoryClientState();
            state.SetCatalog(catalog);
            state.PrepareCharacter(characterId);
            Assert.That(
                state.ApplyFull(new CharacterInventorySnapshot(
                    characterId,
                    catalog.Revision,
                    1,
                    inventory,
                    Array.Empty<InventoryEquipmentSlot>(),
                    null,
                    bank,
                    new SecureContainerInventory("secure_container.base", 1, Container("secure_container")),
                    new RecoveryStorageInventory(Guid.NewGuid(), 1, Array.Empty<RecoveryDelivery>()),
                    0,
                    200,
                    0,
                    10_000)),
                Is.EqualTo(InventorySnapshotApplyResult.Applied));
            var inBank = Location(bank);
            var carried = Location(inventory);
            var recovery = new InventoryItemLocation(
                InventoryItemLocationKind.RecoveryStorage,
                Guid.NewGuid(),
                "recovery_storage",
                0,
                string.Empty,
                Guid.NewGuid());

            Assert.That(
                InventoryRefreshScopes.ForMove(state, inBank, bank.ContainerId),
                Is.EqualTo(InventoryRefreshScope.Bank));
            Assert.That(
                InventoryRefreshScopes.ForMove(state, inBank, inventory.ContainerId),
                Is.EqualTo(InventoryRefreshScope.Full));
            Assert.That(
                InventoryRefreshScopes.ForLocations(state, inBank, inBank),
                Is.EqualTo(InventoryRefreshScope.Bank));
            Assert.That(
                InventoryRefreshScopes.ForLocations(state, inBank, carried),
                Is.EqualTo(InventoryRefreshScope.Full));
            Assert.That(
                InventoryRefreshScopes.ForLocations(state, recovery),
                Is.EqualTo(InventoryRefreshScope.Full));
            Assert.That(
                InventoryRefreshScopes.ForLocations(state, InventoryItemLocation.ForEquipment("main_hand")),
                Is.EqualTo(InventoryRefreshScope.Full));
        }

        private static InventoryContainer Container(string type)
        {
            return new InventoryContainer(
                Guid.NewGuid(),
                type,
                1,
                4,
                new[] { new InventorySlot(0, "general", Array.Empty<string>(), null) });
        }

        private static InventoryItemLocation Location(InventoryContainer container)
        {
            return new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                container.ContainerId,
                container.ContainerType,
                0,
                string.Empty,
                Guid.Empty);
        }
    }
}
