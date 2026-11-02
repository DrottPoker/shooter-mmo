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

        // Shift+right-click sends half of a stack, rounded down, where right-click sends the
        // stack: into the Bank while it is open, and from the Bank or an equipment slot into
        // carried storage, topping up a stack before it takes a free slot. Carried arrows top up
        // the equipped arrows.
        [Test]
        public void HalfAStackGoesWhereRightClickSendsTheStack()
        {
            var ore = Item("fixture.ore", 7);
            var carriedArrows = Item("fixture.arrow", 10);
            var equippedArrows = Item("fixture.arrow", 30);
            var bankedPotion = Item("fixture.potion", 5);
            var state = CreateState(new[] { ore, carriedArrows }, bankedPotion, null, Slot("ammunition", equippedArrows));

            Assert.That(InventoryQuickMoves.HalfOf(7), Is.EqualTo(3));
            Assert.That(InventoryQuickMoves.HalfOf(1), Is.EqualTo(0));

            var deposit = InventoryQuickMoves.ForItemPart(state, ore, 3, true);
            Assert.That(deposit.Kind, Is.EqualTo(InventoryQuickMoveKind.Split), deposit.Reason);
            Assert.That(deposit.Quantity, Is.EqualTo(3));
            Assert.That(deposit.Container.ContainerType, Is.EqualTo("bank"));
            Assert.That(deposit.Slot.SlotIndex, Is.EqualTo(1));

            var withdraw = InventoryQuickMoves.ForItemPart(state, bankedPotion, 2, true);
            Assert.That(withdraw.Kind, Is.EqualTo(InventoryQuickMoveKind.Split), withdraw.Reason);
            Assert.That(withdraw.Container.ContainerType, Is.EqualTo("permanent_inventory"));
            Assert.That(withdraw.Slot.SlotIndex, Is.EqualTo(2));

            var unequip = InventoryQuickMoves.ForItemPart(state, equippedArrows, 15, false);
            Assert.That(unequip.Kind, Is.EqualTo(InventoryQuickMoveKind.Merge), unequip.Reason);
            Assert.That(unequip.Item.ItemInstanceId, Is.EqualTo(equippedArrows.ItemInstanceId));
            Assert.That(unequip.Target.ItemInstanceId, Is.EqualTo(carriedArrows.ItemInstanceId));
            Assert.That(unequip.Quantity, Is.EqualTo(15));

            var topUp = InventoryQuickMoves.ForItemPart(state, carriedArrows, 5, false);
            Assert.That(topUp.Kind, Is.EqualTo(InventoryQuickMoveKind.Merge), topUp.Reason);
            Assert.That(topUp.Target.ItemInstanceId, Is.EqualTo(equippedArrows.ItemInstanceId));
            Assert.That(topUp.Quantity, Is.EqualTo(5));
            Assert.That(InventoryQuickMoves.ForItemPart(state, ore, 3, false).Kind, Is.EqualTo(InventoryQuickMoveKind.None));
            Assert.That(InventoryQuickMoves.ForItemPart(state, ore, 7, true).Kind, Is.EqualTo(InventoryQuickMoveKind.None));
        }

        // Half of the carried arrows fills an empty ammunition slot as a new stack, and the
        // prediction shows both stacks and the weight that left carried storage.
        [Test]
        public void HalfTheArrowsFillAnEmptyAmmunitionSlot()
        {
            var arrows = Item("fixture.arrow", 40);
            var state = CreateState(new[] { arrows }, Slot("ammunition", null));

            var move = InventoryQuickMoves.ForItemPart(state, arrows, 20, false);

            Assert.That(move.Kind, Is.EqualTo(InventoryQuickMoveKind.Equip), move.Reason);
            Assert.That(move.EquipmentSlotId, Is.EqualTo("ammunition"));
            Assert.That(move.Quantity, Is.EqualTo(20));
            var operation = InventoryPredictedOperation.Equip(arrows.ItemInstanceId, "ammunition", 20);
            Assert.That(InventoryOperationPredictor.TryApply(state, operation, out var predicted, out var reason), Is.True, reason);
            Assert.That(predicted.PermanentInventory.Slots[0].Item.Quantity, Is.EqualTo(20));
            var equipped = predicted.Equipment.Single(slot => slot.SlotId == "ammunition").Item;
            Assert.That(equipped.ItemInstanceId, Is.EqualTo(operation.PredictedItemInstanceId));
            Assert.That(equipped.Quantity, Is.EqualTo(20));
            Assert.That(catalog.TryGetDefinition("fixture.arrow", out var definition), Is.True);
            Assert.That(predicted.CarriedWeight, Is.EqualTo(state.FullSnapshot.CarriedWeight - definition.UnitWeight * 20));

            // The ammunition slot holds 50 arrows, so a larger part stays carried.
            var full = Item("fixture.arrow", 60);
            var tooMany = InventoryQuickMoves.ForItemPart(CreateState(new[] { full }, Slot("ammunition", null)), full, 55, false);
            Assert.That(tooMany.Kind, Is.EqualTo(InventoryQuickMoveKind.None));
            Assert.That(tooMany.Reason, Does.Contain("at most 50"));
        }

        // Half of the corpse arrows goes where right-click sends them: onto the equipped arrows
        // or into the empty ammunition slot, before carried storage.
        [Test]
        public void HalfTheCorpseArrowsGoToTheAmmunitionSlotFirst()
        {
            var corpseArrows = new CorpseLootItem(Guid.NewGuid(), "fixture.arrow", 20, 1, Guid.Empty, 0);
            var view = CorpseView(corpseArrows);

            var fill = InventoryQuickMoves.ForCorpseItemPart(CreateState(Array.Empty<InventoryItem>(), Slot("ammunition", null)), view, corpseArrows, 10);
            var topUp = InventoryQuickMoves.ForCorpseItemPart(
                CreateState(Array.Empty<InventoryItem>(), Slot("ammunition", Item("fixture.arrow", 30))),
                view,
                corpseArrows,
                10);
            var carried = InventoryQuickMoves.ForCorpseItemPart(
                CreateState(Array.Empty<InventoryItem>(), Slot("ammunition", Item("fixture.arrow", 45))),
                view,
                corpseArrows,
                10);

            Assert.That(fill.Kind, Is.EqualTo(InventoryQuickMoveKind.LootToEquipment), fill.Reason);
            Assert.That(fill.EquipmentSlotId, Is.EqualTo("ammunition"));
            Assert.That(fill.Quantity, Is.EqualTo(10));
            Assert.That(topUp.Kind, Is.EqualTo(InventoryQuickMoveKind.LootToEquipment), topUp.Reason);
            Assert.That(topUp.Quantity, Is.EqualTo(10));
            Assert.That(carried.Kind, Is.EqualTo(InventoryQuickMoveKind.Loot), "45 and 10 exceed the 50 the slot holds");
            Assert.That(carried.Quantity, Is.EqualTo(10));
        }

        // Half of a corpse stack tops up a carried stack, or takes the first free carried slot.
        [Test]
        public void HalfACorpseStackTopsUpACarriedStackOrTakesAFreeSlot()
        {
            var corpseOre = new CorpseLootItem(Guid.NewGuid(), "fixture.ore", 9, 1, Guid.Empty, 0);
            var view = CorpseView(corpseOre);

            var topUp = InventoryQuickMoves.ForCorpseItemPart(CreateState(new[] { Item("fixture.ore", 2) }), view, corpseOre, 4);
            var free = InventoryQuickMoves.ForCorpseItemPart(CreateState(new[] { Item("fixture.potion", 1) }), view, corpseOre, 4);
            var whole = InventoryQuickMoves.ForCorpseItemPart(CreateState(Array.Empty<InventoryItem>()), view, corpseOre, 9);

            Assert.That(topUp.Kind, Is.EqualTo(InventoryQuickMoveKind.Loot), topUp.Reason);
            Assert.That(topUp.Quantity, Is.EqualTo(4));
            Assert.That(topUp.Slot.SlotIndex, Is.EqualTo(0));
            Assert.That(free.Kind, Is.EqualTo(InventoryQuickMoveKind.Loot), free.Reason);
            Assert.That(free.Slot.SlotIndex, Is.EqualTo(1));
            Assert.That(whole.Kind, Is.EqualTo(InventoryQuickMoveKind.None));
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

        // At the carry cap a corpse Bag cannot equip with its contents, so the contents equip
        // one by one and the emptied Bag follows; it shows empty, not with them a second time.
        [Test]
        public void LootAllEquipsADeferredCorpseBagWithTheContentsThatLeftIt()
        {
            var bagContainerId = Guid.NewGuid();
            var contents = new[] { "fixture.sword", "fixture.shield", "fixture.tunic", "fixture.pickaxe" }
                .Select(definitionId => new CorpseLootItem(Guid.NewGuid(), definitionId, 1, 1, Guid.Empty, 0))
                .ToArray();
            var pack = new CorpseLootItem(Guid.NewGuid(), "fixture.pack", 1, 1, bagContainerId, 1);
            var corpse = new CorpseLootView(
                Guid.NewGuid(),
                3,
                DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
                "Fallen Hero",
                "corpse.remains",
                new[]
                {
                    new CorpseLootSection("general_inventory", Guid.NewGuid(), 1, 0, Array.Empty<CorpseLootSlot>()),
                    new CorpseLootSection(
                        "equipment",
                        Guid.NewGuid(),
                        1,
                        1,
                        new[] { new CorpseLootSlot(0, "general", Array.Empty<string>(), "bag", pack) }),
                    new CorpseLootSection(
                        "bag",
                        bagContainerId,
                        1,
                        7,
                        Enumerable.Range(0, 7).Select(index => new CorpseLootSlot(
                            index,
                            "general",
                            index < contents.Length ? contents[index] : null)))
                });
            var state = CreateState(
                Array.Empty<InventoryItem>(),
                Slot("body", null),
                Slot("main_hand", null),
                Slot("off_hand", null),
                Slot("tool", null),
                Slot("bag", null));
            var atCap = new CharacterInventorySnapshot(
                state.FullSnapshot.CharacterId,
                state.FullSnapshot.CatalogRevision,
                state.FullSnapshot.ItemStateRevision,
                state.FullSnapshot.PermanentInventory,
                state.FullSnapshot.Equipment.ToArray(),
                null,
                state.FullSnapshot.Bank,
                state.FullSnapshot.RecoveryStorage,
                280,
                200,
                14_000,
                2_000);
            var view = new InventorySnapshotView(catalog, atCap);
            var createdIds = contents.Append(pack).ToDictionary(item => item.ItemInstanceId, _ => Guid.NewGuid());
            var edit = new InventoryPredictionEdit(view);

            Assert.That(CorpseLootAllPrediction.TryApply(edit, corpse, false, createdIds, out var reason), Is.True, reason);

            var predicted = edit.Build();
            Assert.That(predicted.EquippedBag, Is.Not.Null);
            Assert.That(predicted.EquippedBag.Contents.Slots.All(slot => slot.Item == null), Is.True);
            Assert.That(
                predicted.Equipment.Single(slot => slot.SlotId == "main_hand").Item.ItemInstanceId,
                Is.EqualTo(createdIds[contents[0].ItemInstanceId]));
            Assert.That(predicted.CarriedWeight, Is.EqualTo(280));
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
