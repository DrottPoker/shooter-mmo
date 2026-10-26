using System;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.GameProtocol;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // Right-click moves against the fixture catalog, whose ammunition slot holds 50 arrows and
    // whose bow blocks the off hand.
    public sealed class InventoryQuickMovesTests
    {
        private ClientItemCatalog catalog;

        [SetUp]
        public void SetUp()
        {
            catalog = FixtureItemCatalog.Load();
        }

        [Test]
        public void ACarriedRingTakesTheFirstFreeRingSlotAndThenReplacesTheFirst()
        {
            var ring = Item("fixture.ring", 1);
            var firstRing = Item("fixture.ring", 1);
            var free = InventoryQuickMoves.ForItem(
                CreateState(new[] { ring }, Slot("ring_1", firstRing), Slot("ring_2", null)),
                ring,
                false);
            var replace = InventoryQuickMoves.ForItem(
                CreateState(new[] { ring }, Slot("ring_1", firstRing), Slot("ring_2", Item("fixture.ring", 1))),
                ring,
                false);

            Assert.That(free.Kind, Is.EqualTo(InventoryQuickMoveKind.Equip), free.Reason);
            Assert.That(free.EquipmentSlotId, Is.EqualTo("ring_2"));
            Assert.That(replace.Kind, Is.EqualTo(InventoryQuickMoveKind.Equip), replace.Reason);
            Assert.That(replace.EquipmentSlotId, Is.EqualTo("ring_1"));
            Assert.That(replace.Before, Is.Empty);
        }

        [Test]
        public void ATwoHandedBowMovesTheEquippedShieldToTheInventoryFirst()
        {
            var bow = Item("fixture.bow", 1);
            var shield = Item("fixture.shield", 1);
            var state = CreateState(new[] { bow }, Slot("main_hand", null), Slot("off_hand", shield));

            var move = InventoryQuickMoves.ForItem(state, bow, false);

            Assert.That(move.Kind, Is.EqualTo(InventoryQuickMoveKind.Equip), move.Reason);
            Assert.That(move.EquipmentSlotId, Is.EqualTo("main_hand"));
            var unequip = move.Before.Single();
            Assert.That(unequip.Kind, Is.EqualTo(InventoryQuickMoveKind.Unequip));
            Assert.That(unequip.Item.ItemInstanceId, Is.EqualTo(shield.ItemInstanceId));
            Assert.That(unequip.Slot.SlotIndex, Is.EqualTo(1));
        }

        [Test]
        public void ArrowsTopUpTheEquippedStackAndAnEquippedItemGoesToTheInventory()
        {
            var arrows = Item("fixture.arrow", 10);
            var equippedArrows = Item("fixture.arrow", 30);
            var state = CreateState(new[] { arrows }, Slot("ammunition", equippedArrows));

            var topUp = InventoryQuickMoves.ForItem(state, arrows, false);
            var unequip = InventoryQuickMoves.ForItem(state, equippedArrows, false);

            Assert.That(topUp.Kind, Is.EqualTo(InventoryQuickMoveKind.Merge), topUp.Reason);
            Assert.That(topUp.Target.ItemInstanceId, Is.EqualTo(equippedArrows.ItemInstanceId));
            // The carried arrows take the whole equipped stack before a free slot does.
            Assert.That(unequip.Kind, Is.EqualTo(InventoryQuickMoveKind.Merge), unequip.Reason);
            Assert.That(unequip.Target.ItemInstanceId, Is.EqualTo(arrows.ItemInstanceId));
        }

        [Test]
        public void WithTheBankOpenItemsGoToTheBankAndBankItemsComeBack()
        {
            var ore = Item("fixture.ore", 5);
            var bankedPotion = Item("fixture.potion", 2);
            var state = CreateState(new[] { ore }, bank: bankedPotion);

            var deposit = InventoryQuickMoves.ForItem(state, ore, true);
            var withdraw = InventoryQuickMoves.ForItem(state, bankedPotion, true);
            var nothing = InventoryQuickMoves.ForItem(state, ore, false);

            Assert.That(deposit.Kind, Is.EqualTo(InventoryQuickMoveKind.Relocate), deposit.Reason);
            Assert.That(deposit.Container.ContainerType, Is.EqualTo("bank"));
            Assert.That(withdraw.Kind, Is.EqualTo(InventoryQuickMoveKind.Relocate), withdraw.Reason);
            Assert.That(withdraw.Container.ContainerType, Is.EqualTo("permanent_inventory"));
            Assert.That(nothing.Kind, Is.EqualTo(InventoryQuickMoveKind.None));
        }

        [Test]
        public void ACorpseItemTakesAFreeEquipmentSlotButNeverReplacesOne()
        {
            var corpseSword = CorpseItem("fixture.sword");
            var view = CorpseView(corpseSword);

            var equip = InventoryQuickMoves.ForCorpseItem(CreateState(Array.Empty<InventoryItem>(), Slot("main_hand", null)), view, corpseSword);
            var carry = InventoryQuickMoves.ForCorpseItem(
                CreateState(Array.Empty<InventoryItem>(), Slot("main_hand", Item("fixture.sword", 1)), Slot("off_hand", Item("fixture.shield", 1))),
                view,
                corpseSword);

            Assert.That(equip.Kind, Is.EqualTo(InventoryQuickMoveKind.LootToEquipment), equip.Reason);
            Assert.That(equip.EquipmentSlotId, Is.EqualTo("main_hand"));
            Assert.That(carry.Kind, Is.EqualTo(InventoryQuickMoveKind.Loot), carry.Reason);
            Assert.That(carry.Container.ContainerType, Is.EqualTo("permanent_inventory"));
            Assert.That(carry.Slot.SlotIndex, Is.EqualTo(0));
        }

        [Test]
        public void ARecoveryDeliveryIsClaimedIntoPermanentInventory()
        {
            var potion = Item("fixture.potion", 1);
            var delivery = new RecoveryDelivery(
                Guid.NewGuid(),
                1,
                "death_recovered_insured",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new[] { new RecoveryDeliveryItem(0, 0, potion) });
            var state = CreateState(Array.Empty<InventoryItem>(), recovery: delivery);

            var move = InventoryQuickMoves.ForRecoveryDelivery(state, delivery);

            Assert.That(move.Kind, Is.EqualTo(InventoryQuickMoveKind.ClaimRecovery), move.Reason);
            Assert.That(move.Container.ContainerType, Is.EqualTo("permanent_inventory"));
        }

        private InventoryClientState CreateState(
            InventoryItem[] carried,
            params InventoryEquipmentSlot[] equipment)
        {
            return CreateState(carried, null, null, equipment);
        }

        private InventoryClientState CreateState(
            InventoryItem[] carried,
            InventoryItem bank = null,
            RecoveryDelivery recovery = null,
            params InventoryEquipmentSlot[] equipment)
        {
            var characterId = Guid.NewGuid();
            var snapshot = new CharacterInventorySnapshot(
                characterId,
                catalog.Revision,
                1,
                Container("permanent_inventory", 4, carried),
                equipment,
                null,
                Container("bank", 4, bank == null ? Array.Empty<InventoryItem>() : new[] { bank }),
                new SecureContainerInventory("secure_container.base", 1, Container("secure_container", 1)),
                new RecoveryStorageInventory(
                    Guid.NewGuid(),
                    1,
                    recovery == null ? Array.Empty<RecoveryDelivery>() : new[] { recovery }),
                carried.Sum(item => catalog.TryGetDefinition(item.DefinitionId, out var definition)
                    ? definition.UnitWeight * item.Quantity
                    : 0),
                200,
                0,
                10_000);
            var state = new InventoryClientState();
            state.SetCatalog(catalog);
            state.PrepareCharacter(characterId);
            Assert.That(state.ApplyFull(snapshot), Is.EqualTo(InventorySnapshotApplyResult.Applied));
            return state;
        }

        private static InventoryContainer Container(string type, int slots, params InventoryItem[] items)
        {
            return new InventoryContainer(
                Guid.NewGuid(),
                type,
                1,
                slots,
                Enumerable.Range(0, slots)
                    .Select(index => new InventorySlot(
                        index,
                        "general",
                        Array.Empty<string>(),
                        index < items.Length ? items[index] : null))
                    .ToArray());
        }

        private static InventoryEquipmentSlot Slot(string slotId, InventoryItem item)
        {
            return new InventoryEquipmentSlot(slotId, 1, item);
        }

        private static InventoryItem Item(string definitionId, int quantity)
        {
            return new InventoryItem(Guid.NewGuid(), definitionId, quantity, 1, Array.Empty<InventoryPolicy>());
        }

        private static CorpseLootItem CorpseItem(string definitionId)
        {
            return new CorpseLootItem(new RealtimeCorpseItem(Guid.NewGuid(), definitionId, 1, 1, Guid.Empty, 0));
        }

        private static CorpseLootView CorpseView(CorpseLootItem item)
        {
            return new CorpseLootView(
                Guid.NewGuid(),
                3,
                DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
                "Fallen Hero",
                "corpse.remains",
                new[]
                {
                    new CorpseLootSection("general_inventory", Guid.NewGuid(), 1, 1, new[] { new CorpseLootSlot(0, "general", item) }),
                    new CorpseLootSection("equipment", Guid.NewGuid(), 1, 0, Array.Empty<CorpseLootSlot>()),
                    new CorpseLootSection("bag", Guid.NewGuid(), 1, 0, Array.Empty<CorpseLootSlot>())
                });
        }
    }
}
