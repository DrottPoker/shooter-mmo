using System;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.Client.Ui;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // Drops against the fixture catalog, whose ore stacks to 100, whose ammunition slot holds
    // 50 arrows, and whose bow blocks the off hand.
    public sealed class InventoryDropPlannerTests
    {
        private ClientItemCatalog catalog;

        [SetUp]
        public void SetUp()
        {
            catalog = FixtureItemCatalog.Load();
        }

        [Test]
        public void AnItemMovesIntoAnEmptySlotAndPartOfItsStackSplitsThere()
        {
            var ore = Item("fixture.ore", 10);
            var state = CreateState(new[] { ore });
            var inventory = state.FullSnapshot.PermanentInventory;
            var empty = inventory.Slots[2];

            var move = Plan(state, inventory, empty, ore, null);
            var split = Plan(state, inventory, empty, ore, 4);

            Assert.That(move.Kind, Is.EqualTo(InventoryDropPlanKind.Relocate), move.Reason.ToString());
            Assert.That(move.Slot.SlotIndex, Is.EqualTo(2));
            Assert.That(split.Kind, Is.EqualTo(InventoryDropPlanKind.Split), split.Reason.ToString());
            Assert.That(split.Quantity, Is.EqualTo(4));
            Assert.That(split.IsCorpseOperation, Is.False);
        }

        // A part tops up a compatible stack and never swaps with another item, and it always
        // leaves part of the stack behind.
        [Test]
        public void APartTopsUpACompatibleStackAndLeavesPartOfTheStack()
        {
            var ore = Item("fixture.ore", 10);
            var potion = Item("fixture.potion", 1);
            var moreOre = Item("fixture.ore", 95);
            var state = CreateState(new[] { ore, potion, moreOre });
            var inventory = state.FullSnapshot.PermanentInventory;

            var ontoItem = Plan(state, inventory, inventory.Slots[1], ore, 4);
            var topUp = Plan(state, inventory, inventory.Slots[2], ore, 4);
            var overTop = Plan(state, inventory, inventory.Slots[2], ore, 6);
            var whole = Plan(state, inventory, inventory.Slots[3], ore, 10);

            Assert.That(ontoItem.Kind, Is.EqualTo(InventoryDropPlanKind.None));
            Assert.That(topUp.Kind, Is.EqualTo(InventoryDropPlanKind.Merge), topUp.Reason.ToString());
            Assert.That(topUp.Target.ItemInstanceId, Is.EqualTo(moreOre.ItemInstanceId));
            Assert.That(topUp.Quantity, Is.EqualTo(4));
            Assert.That(overTop.Kind, Is.EqualTo(InventoryDropPlanKind.None));
            Assert.That(whole.Kind, Is.EqualTo(InventoryDropPlanKind.None));
            Assert.That(whole.Reason.IsEmpty, Is.False);
        }

        [Test]
        public void AnOccupiedSlotMergesTheSameStackAndSwapsAnotherItem()
        {
            var ore = Item("fixture.ore", 10);
            var moreOre = Item("fixture.ore", 5);
            var potion = Item("fixture.potion", 1);
            var state = CreateState(new[] { ore, moreOre, potion });
            var inventory = state.FullSnapshot.PermanentInventory;

            var merge = Plan(state, inventory, inventory.Slots[1], ore, null);
            var swap = Plan(state, inventory, inventory.Slots[2], ore, null);
            var self = Plan(state, inventory, inventory.Slots[0], ore, null);

            Assert.That(merge.Kind, Is.EqualTo(InventoryDropPlanKind.Merge), merge.Reason.ToString());
            Assert.That(merge.Target.ItemInstanceId, Is.EqualTo(moreOre.ItemInstanceId));
            Assert.That(swap.Kind, Is.EqualTo(InventoryDropPlanKind.Swap), swap.Reason.ToString());
            Assert.That(swap.Target.ItemInstanceId, Is.EqualTo(potion.ItemInstanceId));
            Assert.That(self.Kind, Is.EqualTo(InventoryDropPlanKind.None));
        }

        [Test]
        public void AnEquippedStackUnequipsWholeOrSplitsPartIntoTheInventory()
        {
            var arrows = Item("fixture.arrow", 30);
            var state = CreateState(Array.Empty<InventoryItem>(), Slot("ammunition", arrows));
            var inventory = state.FullSnapshot.PermanentInventory;

            var unequip = Plan(state, inventory, inventory.Slots[0], arrows, null);
            var split = Plan(state, inventory, inventory.Slots[0], arrows, 10);

            Assert.That(unequip.Kind, Is.EqualTo(InventoryDropPlanKind.Unequip), unequip.Reason.ToString());
            Assert.That(split.Kind, Is.EqualTo(InventoryDropPlanKind.Split), split.Reason.ToString());
            Assert.That(split.Quantity, Is.EqualTo(10));
        }

        // A part of a stack tops up the equipped stack or equips into an empty slot, but never
        // replaces an equipped item.
        [Test]
        public void AnItemOrPartOfAStackEquipsOrTopsUpTheEquippedStack()
        {
            var sword = Item("fixture.sword", 1);
            var arrows = Item("fixture.arrow", 10);
            var equippedArrows = Item("fixture.arrow", 20);
            var state = CreateState(
                new[] { sword, arrows },
                Slot("main_hand", null),
                Slot("ammunition", equippedArrows));
            var mainHand = state.FullSnapshot.Equipment.Single(slot => slot.SlotId == "main_hand");
            var ammunition = state.FullSnapshot.Equipment.Single(slot => slot.SlotId == "ammunition");

            var equip = PlanEquipment(state, mainHand, sword, null);
            var topUp = PlanEquipment(state, ammunition, arrows, null);
            var partTopUp = PlanEquipment(state, ammunition, arrows, 5);

            Assert.That(equip.Kind, Is.EqualTo(InventoryDropPlanKind.Equip), equip.Reason.ToString());
            Assert.That(equip.EquipmentSlotId, Is.EqualTo("main_hand"));
            Assert.That(equip.Target, Is.Null);
            Assert.That(topUp.Kind, Is.EqualTo(InventoryDropPlanKind.Merge), topUp.Reason.ToString());
            Assert.That(topUp.Target.ItemInstanceId, Is.EqualTo(equippedArrows.ItemInstanceId));
            Assert.That(topUp.Quantity, Is.EqualTo(0));
            Assert.That(partTopUp.Kind, Is.EqualTo(InventoryDropPlanKind.Merge), partTopUp.Reason.ToString());
            Assert.That(partTopUp.Quantity, Is.EqualTo(5));

            var emptyAmmunition = CreateState(new[] { arrows }, Slot("ammunition", null));
            var emptySlot = emptyAmmunition.FullSnapshot.Equipment.Single(slot => slot.SlotId == "ammunition");
            var partEquip = PlanEquipment(emptyAmmunition, emptySlot, arrows, 5);
            Assert.That(partEquip.Kind, Is.EqualTo(InventoryDropPlanKind.Equip), partEquip.Reason.ToString());
            Assert.That(partEquip.Quantity, Is.EqualTo(5));
            Assert.That(PlanEquipment(emptyAmmunition, emptySlot, arrows, 10).Kind, Is.EqualTo(InventoryDropPlanKind.None));
        }

        [Test]
        public void ARecoveryDeliveryIsClaimedWhole()
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
            var inventory = state.FullSnapshot.PermanentInventory;

            var claim = InventoryDropPlanner.PlanContainerDrop(
                state,
                null,
                false,
                inventory,
                inventory.Slots[0],
                InventoryDragPayload.ForRecoveryDelivery(delivery.DeliveryId),
                null);

            Assert.That(claim.Kind, Is.EqualTo(InventoryDropPlanKind.ClaimRecovery), claim.Reason.ToString());
            Assert.That(claim.Delivery.DeliveryId, Is.EqualTo(delivery.DeliveryId));
        }

        [Test]
        public void ACorpseItemIsLootedWholeOrInPartOnlyWhileTheCorpseTakesOperations()
        {
            var corpseOre = new CorpseLootItem(Guid.NewGuid(), "fixture.ore", 8, 1, Guid.Empty, 0);
            var view = CorpseView(corpseOre);
            var state = CreateState(Array.Empty<InventoryItem>());
            var inventory = state.FullSnapshot.PermanentInventory;
            var payload = InventoryDragPayload.ForCorpseItem(view.CorpseId, corpseOre.ItemInstanceId);

            var whole = InventoryDropPlanner.PlanContainerDrop(state, view, true, inventory, inventory.Slots[0], payload, null);
            var part = InventoryDropPlanner.PlanContainerDrop(state, view, true, inventory, inventory.Slots[0], payload, 3);
            var busy = InventoryDropPlanner.PlanContainerDrop(state, view, false, inventory, inventory.Slots[0], payload, null);

            Assert.That(whole.Kind, Is.EqualTo(InventoryDropPlanKind.CorpseLoot), whole.Reason.ToString());
            Assert.That(whole.Quantity, Is.EqualTo(8));
            Assert.That(whole.IsCorpseOperation, Is.True);
            Assert.That(part.Kind, Is.EqualTo(InventoryDropPlanKind.CorpseLoot), part.Reason.ToString());
            Assert.That(part.Quantity, Is.EqualTo(3));
            Assert.That(busy.Kind, Is.EqualTo(InventoryDropPlanKind.None));
        }

        // Part of the corpse arrows tops up the equipped arrows or fills the empty ammunition
        // slot, and the rest stays in the corpse.
        [Test]
        public void PartOfACorpseStackTopsUpOrFillsTheAmmunitionSlot()
        {
            var corpseArrows = new CorpseLootItem(Guid.NewGuid(), "fixture.arrow", 20, 1, Guid.Empty, 0);
            var view = CorpseView(corpseArrows);
            var payload = InventoryDragPayload.ForCorpseItem(view.CorpseId, corpseArrows.ItemInstanceId);
            var empty = CreateState(Array.Empty<InventoryItem>(), Slot("ammunition", null));
            var topped = CreateState(Array.Empty<InventoryItem>(), Slot("ammunition", Item("fixture.arrow", 40)));

            var fill = InventoryDropPlanner.PlanEquipmentDrop(empty, view, true, AmmunitionSlot(empty), payload, 10);
            var topUp = InventoryDropPlanner.PlanEquipmentDrop(topped, view, true, AmmunitionSlot(topped), payload, 10);
            var overTop = InventoryDropPlanner.PlanEquipmentDrop(topped, view, true, AmmunitionSlot(topped), payload, 19);
            var whole = InventoryDropPlanner.PlanEquipmentDrop(empty, view, true, AmmunitionSlot(empty), payload, 20);

            Assert.That(fill.Kind, Is.EqualTo(InventoryDropPlanKind.CorpseLootToEquipment), fill.Reason.ToString());
            Assert.That(fill.Quantity, Is.EqualTo(10));
            Assert.That(topUp.Kind, Is.EqualTo(InventoryDropPlanKind.CorpseLootToEquipment), topUp.Reason.ToString());
            Assert.That(topUp.Quantity, Is.EqualTo(10));
            Assert.That(overTop.Kind, Is.EqualTo(InventoryDropPlanKind.None), "the ammunition slot holds 50");
            Assert.That(whole.Kind, Is.EqualTo(InventoryDropPlanKind.None));
        }

        [Test]
        public void ACarriedItemIsDepositedIntoAnEmptyCorpseSlot()
        {
            var ore = Item("fixture.ore", 10);
            var view = CorpseView(null);
            var state = CreateState(new[] { ore });
            var section = view.Sections.Single(candidate => candidate.SectionKind == "general_inventory");

            var deposit = InventoryDropPlanner.PlanCorpseDrop(
                state,
                view,
                true,
                section,
                section.Slots[0],
                InventoryDragPayload.ForItem(ore.ItemInstanceId),
                null);

            Assert.That(deposit.Kind, Is.EqualTo(InventoryDropPlanKind.CorpseDeposit), deposit.Reason.ToString());
            Assert.That(deposit.Quantity, Is.EqualTo(10));
        }

        [Test]
        public void ASlotThatNoLongerExistsTakesNothing()
        {
            var ore = Item("fixture.ore", 10);
            var state = CreateState(new[] { ore });
            var stale = Container("permanent_inventory", 4);

            var plan = Plan(state, stale, stale.Slots[0], ore, null);

            Assert.That(plan.Kind, Is.EqualTo(InventoryDropPlanKind.None));
            Assert.That(plan.Reason.Key, Is.EqualTo("ui/items/drop-slot-gone"));
        }

        private static InventoryDropPlan Plan(
            InventoryClientState state,
            InventoryContainer container,
            InventorySlot slot,
            InventoryItem item,
            int? quantity)
        {
            return InventoryDropPlanner.PlanContainerDrop(
                state,
                null,
                false,
                container,
                slot,
                InventoryDragPayload.ForItem(item.ItemInstanceId),
                quantity);
        }

        private static InventoryDropPlan PlanEquipment(
            InventoryClientState state,
            InventoryEquipmentSlot slot,
            InventoryItem item,
            int? quantity)
        {
            return InventoryDropPlanner.PlanEquipmentDrop(
                state,
                null,
                false,
                slot,
                InventoryDragPayload.ForItem(item.ItemInstanceId),
                quantity);
        }

        private InventoryClientState CreateState(
            InventoryItem[] carried,
            params InventoryEquipmentSlot[] equipment)
        {
            return CreateState(carried, null, equipment);
        }

        private InventoryClientState CreateState(
            InventoryItem[] carried,
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
                Container("bank", 4),
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

        private static InventoryEquipmentSlot AmmunitionSlot(InventoryClientState state)
        {
            return state.FullSnapshot.Equipment.Single(slot => slot.SlotId == "ammunition");
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
                    new CorpseLootSection(
                        "general_inventory",
                        Guid.NewGuid(),
                        1,
                        2,
                        new[] { new CorpseLootSlot(0, "general", item), new CorpseLootSlot(1, "general", null) }),
                    new CorpseLootSection("equipment", Guid.NewGuid(), 1, 0, Array.Empty<CorpseLootSlot>()),
                    new CorpseLootSection("bag", Guid.NewGuid(), 1, 0, Array.Empty<CorpseLootSlot>())
                });
        }
    }
}
