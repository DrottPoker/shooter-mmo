using System;
using Mmorpg.Client.Items;
using Mmorpg.GameProtocol;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // Corpse target rules against the fixture catalog, whose ammunition slot holds 50 arrows.
    public sealed class CorpseTargetAdvisorTests
    {
        private ClientItemCatalog catalog;

        [SetUp]
        public void SetUp()
        {
            catalog = FixtureItemCatalog.Load();
        }

        [Test]
        public void CorpseLootEntersOnlyCarriedSlotsWithAValidPartialQuantity()
        {
            var state = CreateState(null);
            var corpseArrows = CorpseItem("fixture.arrow", 20);
            var general = GeneralSection(corpseArrows);
            var inventory = state.FullSnapshot.PermanentInventory;

            Assert.That(
                CorpseTargetAdvisor.CanLoot(
                    state,
                    null,
                    corpseArrows,
                    general,
                    general.Slots[0],
                    inventory,
                    inventory.Slots[0],
                    null,
                    out var lootReason),
                Is.True,
                lootReason);
            Assert.That(
                CorpseTargetAdvisor.CanLoot(
                    state,
                    null,
                    corpseArrows,
                    general,
                    general.Slots[0],
                    state.Bank,
                    state.Bank.Slots[0],
                    null,
                    out var bankReason),
                Is.False);
            Assert.That(bankReason, Does.Contain("Corpse loot can enter only"));
            Assert.That(
                CorpseTargetAdvisor.CanLoot(
                    state,
                    null,
                    corpseArrows,
                    general,
                    general.Slots[0],
                    inventory,
                    inventory.Slots[0],
                    0,
                    out _),
                Is.False);
            Assert.That(
                CorpseTargetAdvisor.CanLoot(
                    state,
                    null,
                    corpseArrows,
                    general,
                    general.Slots[0],
                    inventory,
                    inventory.Slots[0],
                    5,
                    out var partialReason),
                Is.True,
                partialReason);
        }

        [Test]
        public void DepositRespectsTheCorpseEquipmentSlotLimitAndCarriedSources()
        {
            var carriedArrows = new InventoryItem(
                Guid.NewGuid(),
                "fixture.arrow",
                15,
                1,
                Array.Empty<InventoryPolicy>());
            var state = CreateState(carriedArrows);
            var source = new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                state.FullSnapshot.PermanentInventory.ContainerId,
                "permanent_inventory",
                0,
                string.Empty,
                Guid.Empty);
            var equipment = AmmunitionSection(CorpseItem("fixture.arrow", 40));

            Assert.That(
                CorpseTargetAdvisor.CanDeposit(
                    state,
                    carriedArrows,
                    source,
                    equipment,
                    equipment.Slots[0],
                    null,
                    out var limitReason),
                Is.False);
            Assert.That(limitReason, Does.Contain("holds at most 50"));
            Assert.That(
                CorpseTargetAdvisor.CanDeposit(
                    state,
                    carriedArrows,
                    source,
                    equipment,
                    equipment.Slots[0],
                    10,
                    out var topUpReason),
                Is.True,
                topUpReason);

            var bankSource = new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                state.Bank.ContainerId,
                "bank",
                0,
                string.Empty,
                Guid.Empty);
            Assert.That(
                CorpseTargetAdvisor.CanDeposit(
                    state,
                    carriedArrows,
                    bankSource,
                    equipment,
                    equipment.Slots[0],
                    null,
                    out var bankReason),
                Is.False);
            Assert.That(bankReason, Does.Contain("cannot enter a corpse"));
            Assert.That(
                CorpseTargetAdvisor.CanDeposit(
                    state,
                    carriedArrows,
                    InventoryItemLocation.ForEquipment("ammunition"),
                    equipment,
                    equipment.Slots[0],
                    null,
                    out var equippedReason),
                Is.False);
            Assert.That(equippedReason, Does.Contain("equipped"));
        }

        [Test]
        public void InternalMovesRejectTheSameSlotAndApplyTheEquipmentSlotLimit()
        {
            var state = CreateState(null);
            var generalArrows = CorpseItem("fixture.arrow", 15);
            var equippedArrows = CorpseItem("fixture.arrow", 40);
            var general = GeneralSection(generalArrows);
            var equipment = AmmunitionSection(equippedArrows);
            var view = new CorpseLootView(
                Guid.NewGuid(),
                1,
                2_000_000_000_000L,
                "Fallen Hero",
                "corpse.remains",
                new[] { general, equipment, EmptyBagSection() });

            Assert.That(
                CorpseTargetAdvisor.CanMoveWithinCorpse(
                    state,
                    view,
                    generalArrows,
                    general,
                    general.Slots[0],
                    general,
                    general.Slots[0],
                    null,
                    out var sameSlotReason),
                Is.False);
            Assert.That(sameSlotReason, Does.Contain("already in that slot"));
            Assert.That(
                CorpseTargetAdvisor.CanMoveWithinCorpse(
                    state,
                    view,
                    generalArrows,
                    general,
                    general.Slots[0],
                    equipment,
                    equipment.Slots[0],
                    null,
                    out _),
                Is.False);
            Assert.That(
                CorpseTargetAdvisor.CanMoveWithinCorpse(
                    state,
                    view,
                    generalArrows,
                    general,
                    general.Slots[0],
                    equipment,
                    equipment.Slots[0],
                    10,
                    out var partialReason),
                Is.True,
                partialReason);
        }

        [Test]
        public void CorpseBagSwapRequiresBothOccupiedBagAggregates()
        {
            var equippedBag = new InventoryItem(
                Guid.NewGuid(),
                "fixture.pack",
                1,
                1,
                Array.Empty<InventoryPolicy>());
            var state = CreateState(null, equippedBag);
            var bagSlot = new InventoryEquipmentSlot("bag", 8, equippedBag);
            var corpseBag = new CorpseLootItem(new RealtimeCorpseItem(
                Guid.NewGuid(),
                "fixture.pack",
                1,
                1,
                Guid.NewGuid(),
                1));
            var equipment = new CorpseLootSection(
                "equipment",
                Guid.NewGuid(),
                1,
                1,
                new[] { new CorpseLootSlot(0, "general", Array.Empty<string>(), "bag", corpseBag) });

            Assert.That(
                CorpseTargetAdvisor.CanSwapCorpseBagIntoEquipment(
                    state,
                    corpseBag,
                    equipment,
                    bagSlot,
                    out var swapReason),
                Is.True,
                swapReason);
            Assert.That(
                CorpseTargetAdvisor.CanSwapCorpseBagIntoEquipment(
                    state,
                    corpseBag,
                    GeneralSection(null),
                    bagSlot,
                    out _),
                Is.False);
            Assert.That(
                CorpseTargetAdvisor.IsBagSwap(
                    state,
                    equippedBag,
                    InventoryItemLocation.ForEquipment("bag"),
                    equipment,
                    equipment.Slots[0]),
                Is.True);
        }

        [Test]
        public void CorpseItemsEquipIntoEmptySlotsTopUpStacksAndSwapIntoTheCorpse()
        {
            var bow = CorpseItem("fixture.bow", 1);
            var general = GeneralSection(bow);
            var emptyMainHand = new InventoryEquipmentSlot("main_hand", 2, null);
            Assert.That(
                CorpseTargetAdvisor.CanLootIntoEquipment(
                    CreateState(null, null, emptyMainHand),
                    null,
                    bow,
                    general,
                    general.Slots[0],
                    emptyMainHand,
                    out var emptyReason),
                Is.True,
                emptyReason);

            // An equipped sword swaps into the corpse slot that the bow leaves.
            var sword = new InventoryItem(Guid.NewGuid(), "fixture.sword", 1, 1, Array.Empty<InventoryPolicy>());
            var occupiedMainHand = new InventoryEquipmentSlot("main_hand", 2, sword);
            Assert.That(
                CorpseTargetAdvisor.CanLootIntoEquipment(
                    CreateState(null, null, occupiedMainHand),
                    null,
                    bow,
                    general,
                    general.Slots[0],
                    occupiedMainHand,
                    out var swapReason),
                Is.True,
                swapReason);

            // The bow still needs a free off hand.
            var shieldOffHand = new InventoryEquipmentSlot(
                "off_hand",
                3,
                new InventoryItem(Guid.NewGuid(), "fixture.shield", 1, 1, Array.Empty<InventoryPolicy>()));
            Assert.That(
                CorpseTargetAdvisor.CanLootIntoEquipment(
                    CreateState(null, null, emptyMainHand, shieldOffHand),
                    null,
                    bow,
                    general,
                    general.Slots[0],
                    emptyMainHand,
                    out var blockedReason),
                Is.False);
            Assert.That(blockedReason, Does.Contain("Off Hand"));

            // Corpse arrows top up the equipped arrows up to the 50 the fixture slot holds.
            var equippedArrows = new InventoryItem(Guid.NewGuid(), "fixture.arrow", 30, 1, Array.Empty<InventoryPolicy>());
            var ammunition = new InventoryEquipmentSlot("ammunition", 4, equippedArrows);
            var arrowState = CreateState(null, null, ammunition);
            var fittingArrows = CorpseItem("fixture.arrow", 20);
            var fittingSection = GeneralSection(fittingArrows);
            Assert.That(
                CorpseTargetAdvisor.CanLootIntoEquipment(
                    arrowState,
                    null,
                    fittingArrows,
                    fittingSection,
                    fittingSection.Slots[0],
                    ammunition,
                    out var topUpReason),
                Is.True,
                topUpReason);
            var tooManyArrows = CorpseItem("fixture.arrow", 25);
            var tooManySection = GeneralSection(tooManyArrows);
            Assert.That(
                CorpseTargetAdvisor.CanLootIntoEquipment(
                    arrowState,
                    null,
                    tooManyArrows,
                    tooManySection,
                    tooManySection.Slots[0],
                    ammunition,
                    out _),
                Is.False);

            // The equipped Bag swaps only through the Bag aggregate swap.
            var equippedBag = new InventoryItem(Guid.NewGuid(), "fixture.pack", 1, 1, Array.Empty<InventoryPolicy>());
            var bagSlot = new InventoryEquipmentSlot("bag", 8, equippedBag);
            var emptyPack = CorpseItem("fixture.pack", 1);
            var packSection = GeneralSection(emptyPack);
            Assert.That(
                CorpseTargetAdvisor.CanLootIntoEquipment(
                    CreateState(null, equippedBag, bagSlot),
                    null,
                    emptyPack,
                    packSection,
                    packSection.Slots[0],
                    bagSlot,
                    out var bagReason),
                Is.False);
            Assert.That(bagReason, Does.Contain("Bag"));
        }

        private InventoryClientState CreateState(
            InventoryItem permanentItem,
            InventoryItem equippedBag = null,
            params InventoryEquipmentSlot[] equipment)
        {
            var characterId = Guid.NewGuid();
            var bag = equippedBag == null
                ? null
                : new EquippedBagInventory(
                    equippedBag,
                    new InventoryContainer(
                        Guid.NewGuid(),
                        "bag_contents",
                        1,
                        1,
                        new[] { new InventorySlot(0, "general", Array.Empty<string>(), null) }));
            var snapshot = new CharacterInventorySnapshot(
                characterId,
                catalog.Revision,
                1,
                new InventoryContainer(
                    Guid.NewGuid(),
                    "permanent_inventory",
                    1,
                    20,
                    new[]
                    {
                        new InventorySlot(0, "general", Array.Empty<string>(), permanentItem),
                        new InventorySlot(1, "general", Array.Empty<string>(), null)
                    }),
                equipment ?? Array.Empty<InventoryEquipmentSlot>(),
                bag,
                Container("bank"),
                new RecoveryStorageInventory(Guid.NewGuid(), 1, Array.Empty<RecoveryDelivery>()),
                50,
                200,
                2_500,
                10_000);
            var state = new InventoryClientState();
            state.SetCatalog(catalog);
            state.PrepareCharacter(characterId);
            Assert.That(state.ApplyFull(snapshot), Is.EqualTo(InventorySnapshotApplyResult.Applied));
            return state;
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

        private static CorpseLootItem CorpseItem(string definitionId, int quantity)
        {
            return new CorpseLootItem(new RealtimeCorpseItem(
                Guid.NewGuid(),
                definitionId,
                quantity,
                1,
                Guid.Empty,
                0));
        }

        private static CorpseLootSection GeneralSection(CorpseLootItem item)
        {
            return new CorpseLootSection(
                "general_inventory",
                Guid.NewGuid(),
                1,
                2,
                new[]
                {
                    new CorpseLootSlot(0, "general", item),
                    new CorpseLootSlot(1, "general", null)
                });
        }

        private static CorpseLootSection AmmunitionSection(CorpseLootItem item)
        {
            return new CorpseLootSection(
                "equipment",
                Guid.NewGuid(),
                1,
                1,
                new[] { new CorpseLootSlot(0, "general", Array.Empty<string>(), "ammunition", item) });
        }

        private static CorpseLootSection EmptyBagSection()
        {
            return new CorpseLootSection(
                "bag",
                Guid.NewGuid(),
                1,
                1,
                new[] { new CorpseLootSlot(0, "general", null) });
        }
    }
}
