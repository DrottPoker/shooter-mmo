using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // Recovery claims, Bag equips, vendor trades, and corpse operations show at once like
    // every other item operation, and their committed changes patch the inventory without a
    // reload.
    public sealed class InventoryStoragePredictionTests
    {
        private const long KnownRevision = 5;
        private ClientItemCatalog catalog;

        [OneTimeSetUp]
        public void LoadCatalog()
        {
            catalog = FixtureItemCatalog.Load();
        }

        [Test]
        public void AClaimPatchTakesTheDeliveryOutOfRecoveryStorage()
        {
            var potion = Item("fixture.potion", 2, 3);
            var delivery = Delivery(potion);
            var state = CreateState(Array.Empty<InventoryItem>(), new[] { delivery });
            var inventory = state.FullSnapshot.PermanentInventory;
            var recovery = state.FullSnapshot.RecoveryStorage;

            Assert.That(
                state.TryApplyItemStateChange(Change(
                    104,
                    new[] { Revision(inventory.ContainerId, 2), Revision(recovery.ContainerId, 2) },
                    new[] { delivery.DeliveryId },
                    Contained(potion, inventory.ContainerId, 0, 4))),
                Is.True);

            Assert.That(state.RecoveryStorage.Deliveries, Is.Empty);
            Assert.That(state.RecoveryStorage.Revision, Is.EqualTo(2));
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 0).Item.ItemInstanceId, Is.EqualTo(potion.ItemInstanceId));
            Assert.That(state.HasCoherentFullSnapshotAtLeast(KnownRevision + 1), Is.True);
        }

        [Test]
        public void AClaimPatchThatLeavesADeliveredItemBehindNeedsAReload()
        {
            var potion = Item("fixture.potion", 2, 3);
            var delivery = Delivery(potion);
            var state = CreateState(Array.Empty<InventoryItem>(), new[] { delivery });

            Assert.That(
                state.TryApplyItemStateChange(Change(
                    100,
                    new[] { Revision(state.FullSnapshot.RecoveryStorage.ContainerId, 2) },
                    new[] { delivery.DeliveryId })),
                Is.False);
            Assert.That(state.RecoveryStorage.Deliveries.Count, Is.EqualTo(1));
        }

        [Test]
        public void AnEquippedBagPatchShowsItsContentsLaidOutAsTheCatalogBag()
        {
            var pack = Item("fixture.pack", 1, 1);
            var state = CreateState(new[] { pack });
            var contentsId = Guid.NewGuid();

            Assert.That(
                state.TryApplyItemStateChange(Change(
                    90,
                    new[] { Revision(state.FullSnapshot.PermanentInventory.ContainerId, 2), Revision(contentsId, 1) },
                    null,
                    Equipped(pack, ItemEquipmentSlotIds.Bag, 2, contentsId, 1))),
                Is.True);

            var bag = state.FullSnapshot.EquippedBag;
            Assert.That(bag, Is.Not.Null);
            Assert.That(bag.Item.ItemInstanceId, Is.EqualTo(pack.ItemInstanceId));
            Assert.That(bag.Contents.ContainerId, Is.EqualTo(contentsId));
            Assert.That(bag.Contents.ContainerType, Is.EqualTo(ItemContainerTypeIds.BagContents));
            Assert.That(bag.Contents.Slots.Count, Is.EqualTo(Definition("fixture.pack").Bag.Slots.Length));
            Assert.That(bag.Contents.Slots.All(slot => slot.Item == null), Is.True);
        }

        [Test]
        public void ABagEquipAndUnequipShowAtOnceWithTheirCapacity()
        {
            var pack = Item("fixture.pack", 1, 1);
            var state = CreateState(new[] { pack });
            var bonus = Definition("fixture.pack").Bag.CarryCapacityBonus;
            var packWeight = ItemWeightRules.CalculateStackWeight(Definition("fixture.pack").UnitWeight, 1);
            var equip = InventoryPredictedOperation.Equip(pack.ItemInstanceId, ItemEquipmentSlotIds.Bag);

            Assert.That(state.TryPredict(equip, out var error), Is.True, error);

            var bag = state.FullSnapshot.EquippedBag;
            Assert.That(bag.Item.ItemInstanceId, Is.EqualTo(pack.ItemInstanceId));
            Assert.That(state.IsPredictedContainer(bag.Contents.ContainerId), Is.True);
            Assert.That(state.FullSnapshot.CarryCapacity, Is.EqualTo(200 + bonus));
            Assert.That(state.FullSnapshot.CarriedWeight, Is.EqualTo(100 - packWeight));

            Assert.That(
                state.TryPredict(
                    InventoryPredictedOperation.Unequip(pack.ItemInstanceId, state.FullSnapshot.PermanentInventory.ContainerId, 3),
                    out error),
                Is.True,
                error);
            Assert.That(state.FullSnapshot.EquippedBag, Is.Null);
            Assert.That(state.FullSnapshot.CarryCapacity, Is.EqualTo(200));
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 3).Item.ItemInstanceId, Is.EqualTo(pack.ItemInstanceId));
        }

        [Test]
        public void AClaimShowsTheDeliveryInTheFirstFreeSlots()
        {
            var ore = Item("fixture.ore", 10, 3);
            var potion = Item("fixture.potion", 2, 3);
            var sword = Item("fixture.sword", 1, 3);
            var delivery = Delivery(potion, sword);
            var state = CreateState(new[] { ore }, new[] { delivery });
            var claim = InventoryPredictedOperation.ClaimRecovery(delivery, state.FullSnapshot.PermanentInventory.ContainerId);

            Assert.That(state.TryPredict(claim, out var error), Is.True, error);

            Assert.That(state.RecoveryStorage.Deliveries, Is.Empty);
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 1).Item.ItemInstanceId, Is.EqualTo(potion.ItemInstanceId));
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 2).Item.ItemInstanceId, Is.EqualTo(sword.ItemInstanceId));
            Assert.That(state.ConfirmedRecoveryStorage.Deliveries.Count, Is.EqualTo(1));
            Assert.That(state.CanPredict, Is.True);
        }

        [Test]
        public void APurchaseTopsUpAStackFirstAndPaysAtOnce()
        {
            var ore = Item("fixture.ore", 10, 3);
            var state = CreateState(new[] { ore });

            Assert.That(
                state.TryPredict(VendorPrediction.Purchase("fixture.ore", 5, 30, NeverSent), out var error),
                Is.True,
                error);

            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 0).Item.Quantity, Is.EqualTo(15));
            Assert.That(state.FullSnapshot.Currency, Is.EqualTo(45));
            Assert.That(
                state.FullSnapshot.CarriedWeight,
                Is.EqualTo(100 + ItemWeightRules.CalculateStackWeight(Definition("fixture.ore").UnitWeight, 5)));
        }

        [Test]
        public void APurchaseWithoutAStackTakesTheFirstFreeSlotUnderAWaitingId()
        {
            var sword = Item("fixture.sword", 1, 3);
            var state = CreateState(new[] { sword });
            var purchase = VendorPrediction.Purchase("fixture.potion", 2, 20, NeverSent);

            Assert.That(state.TryPredict(purchase, out var error), Is.True, error);

            var bought = Slot(state.FullSnapshot.PermanentInventory, 1).Item;
            Assert.That(bought.DefinitionId, Is.EqualTo("fixture.potion"));
            Assert.That(bought.Quantity, Is.EqualTo(2));
            Assert.That(state.IsPredictedItem(bought.ItemInstanceId), Is.True);
        }

        [Test]
        public void APurchaseThePlayerCannotAffordIsNotShown()
        {
            var state = CreateState(Array.Empty<InventoryItem>());

            Assert.That(
                state.TryPredict(VendorPrediction.Purchase("fixture.potion", 1, 76, NeverSent), out var error),
                Is.False);
            Assert.That(error, Does.Contain("afford"));
        }

        [Test]
        public void ASaleTakesPartOfTheStackAndCreditsThePrice()
        {
            var ore = Item("fixture.ore", 10, 3);
            var state = CreateState(new[] { ore });

            Assert.That(
                state.TryPredict(VendorPrediction.Sale(ore.ItemInstanceId, 4, 12, NeverSent), out var error),
                Is.True,
                error);

            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 0).Item.Quantity, Is.EqualTo(6));
            Assert.That(state.FullSnapshot.Currency, Is.EqualTo(87));
        }

        [Test]
        public void LootMergesIntoAStackOrSwapsAWholeItemIntoTheCorpse()
        {
            var ore = Item("fixture.ore", 10, 3);
            var sword = Item("fixture.sword", 1, 3);
            var state = CreateState(new[] { ore, sword });
            var inventory = state.FullSnapshot.PermanentInventory.ContainerId;

            var merge = new InventoryPredictionEdit(state);
            Assert.That(
                CorpseInventoryPrediction.TryLoot(merge, CorpseItem("fixture.ore", 5), 5, inventory, 0, Guid.NewGuid(), out var displaced, out var error),
                Is.True,
                error);
            Assert.That(displaced, Is.Null);
            Assert.That(Slot(merge.Build().PermanentInventory, 0).Item.Quantity, Is.EqualTo(15));

            var swap = new InventoryPredictionEdit(state);
            var helm = CorpseItem("fixture.helm", 1);
            Assert.That(
                CorpseInventoryPrediction.TryLoot(swap, helm, 1, inventory, 1, Guid.NewGuid(), out displaced, out error),
                Is.True,
                error);
            Assert.That(displaced.ItemInstanceId, Is.EqualTo(sword.ItemInstanceId));
            Assert.That(Slot(swap.Build().PermanentInventory, 1).Item.DefinitionId, Is.EqualTo("fixture.helm"));

            var partial = new InventoryPredictionEdit(state);
            Assert.That(
                CorpseInventoryPrediction.TryLoot(partial, CorpseItem("fixture.potion", 3), 1, inventory, 1, Guid.NewGuid(), out _, out _),
                Is.False);
        }

        [Test]
        public void ADepositSwapBringsTheCorpseItemIntoTheSlotTheDepositLeft()
        {
            var ore = Item("fixture.ore", 10, 3);
            var state = CreateState(new[] { ore });
            var helm = CorpseItem("fixture.helm", 1);
            var edit = new InventoryPredictionEdit(state);

            Assert.That(CorpseInventoryPrediction.TryDeposit(edit, ore.ItemInstanceId, 10, helm, out var error), Is.True, error);

            var built = edit.Build();
            Assert.That(Slot(built.PermanentInventory, 0).Item.ItemInstanceId, Is.EqualTo(helm.ItemInstanceId));
            Assert.That(built.CarriedWeight, Is.EqualTo(
                100
                - ItemWeightRules.CalculateStackWeight(Definition("fixture.ore").UnitWeight, 10)
                + ItemWeightRules.CalculateStackWeight(Definition("fixture.helm").UnitWeight, 1)));
        }

        [Test]
        public void CorpseEditsSetSlotsAndReplaceSectionsOfTheShownView()
        {
            var containerId = Guid.NewGuid();
            var item = CorpseItem("fixture.ore", 4);
            var view = new CorpseLootView(
                Guid.NewGuid(),
                3,
                1,
                "Wolf",
                "corpse.wolf",
                new[]
                {
                    new CorpseLootSection(
                        CorpseSectionKindIds.GeneralInventory,
                        containerId,
                        2,
                        2,
                        new[] { new CorpseLootSlot(0, "general", item), new CorpseLootSlot(1, "general", null) })
                });

            var shown = CorpseViewEdit.Apply(
                view,
                new[]
                {
                    CorpseViewEdit.SetSlot(containerId, 0, null),
                    CorpseViewEdit.SetSlot(containerId, 1, item.WithQuantity(2)),
                    CorpseViewEdit.SetSlot(Guid.NewGuid(), 0, item)
                });

            Assert.That(shown.Revision, Is.EqualTo(3));
            Assert.That(shown.Sections[0].Slots[0].Item, Is.Null);
            Assert.That(shown.Sections[0].Slots[1].Item.Quantity, Is.EqualTo(2));
            Assert.That(view.Sections[0].Slots[0].Item, Is.SameAs(item));
        }

        private static InventoryOperationSendResult NeverSent(InventoryClientState state, out Guid operationId, out string error)
        {
            operationId = Guid.Empty;
            error = string.Empty;
            return InventoryOperationSendResult.Wait;
        }

        private InventoryClientState CreateState(IReadOnlyList<InventoryItem> inventoryItems, RecoveryDelivery[] deliveries = null)
        {
            var characterId = Guid.NewGuid();
            var state = new InventoryClientState();
            state.SetCatalog(catalog);
            state.PrepareCharacter(characterId);
            var items = inventoryItems.Select((item, index) => (index, item)).ToArray();
            Assert.That(
                state.ApplyFull(new CharacterInventorySnapshot(
                    characterId,
                    catalog.Revision,
                    KnownRevision,
                    Container(Guid.NewGuid(), ItemContainerTypeIds.PermanentInventory, items),
                    new[]
                    {
                        new InventoryEquipmentSlot(ItemEquipmentSlotIds.MainHand, 2, null),
                        new InventoryEquipmentSlot(ItemEquipmentSlotIds.Bag, 9, null)
                    },
                    null,
                    Container(Guid.NewGuid(), ItemContainerTypeIds.Bank),
                    new SecureContainerInventory(
                        "secure_container.base",
                        1,
                        Container(Guid.NewGuid(), ItemContainerTypeIds.SecureContainer)),
                    new RecoveryStorageInventory(Guid.NewGuid(), 1, deliveries ?? Array.Empty<RecoveryDelivery>()),
                    100,
                    200,
                    InventorySnapshotMapper.CalculateLoadRatioBasisPoints(100, 200),
                    EncumbranceRules.CalculateMovementMultiplierBasisPoints(100, 200),
                    75)),
                Is.EqualTo(InventorySnapshotApplyResult.Applied));
            return state;
        }

        private static InventoryContainer Container(Guid containerId, string type, params (int SlotIndex, InventoryItem Item)[] items)
        {
            return new InventoryContainer(
                containerId,
                type,
                1,
                8,
                Enumerable.Range(0, 8)
                    .Select(index => new InventorySlot(
                        index,
                        "general",
                        Array.Empty<string>(),
                        items.FirstOrDefault(entry => entry.SlotIndex == index).Item))
                    .ToArray());
        }

        private static RecoveryDelivery Delivery(params InventoryItem[] items)
        {
            return new RecoveryDelivery(
                Guid.NewGuid(),
                1,
                "death",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                items.Select((item, index) => new RecoveryDeliveryItem(index, index, item)).ToArray());
        }

        private static RealtimeOwnerItemStateChanged Change(
            long weight,
            RealtimeContainerRevision[] containers,
            Guid[] claimed,
            params RealtimeItemStateChange[] items)
        {
            return new RealtimeOwnerItemStateChanged(
                Guid.Empty,
                KnownRevision,
                new RealtimeCarryState(KnownRevision + 1, weight, 700),
                75,
                containers,
                items,
                claimed ?? Array.Empty<Guid>());
        }

        private static RealtimeContainerRevision Revision(Guid containerId, long revision)
        {
            return new RealtimeContainerRevision(containerId, revision);
        }

        private static RealtimeItemStateChange Contained(InventoryItem item, Guid containerId, int slotIndex, long revision)
        {
            return new RealtimeItemStateChange(
                item.ItemInstanceId,
                RealtimeItemStateLocationKind.Container,
                item.DefinitionId,
                item.Quantity,
                revision,
                containerId,
                slotIndex,
                string.Empty,
                Array.Empty<RealtimeItemPolicySummary>(),
                Guid.Empty,
                0);
        }

        private static RealtimeItemStateChange Equipped(
            InventoryItem item,
            string equipmentSlotId,
            long revision,
            Guid bagContentsId,
            long bagContentsRevision)
        {
            return new RealtimeItemStateChange(
                item.ItemInstanceId,
                RealtimeItemStateLocationKind.Equipment,
                item.DefinitionId,
                item.Quantity,
                revision,
                Guid.Empty,
                -1,
                equipmentSlotId,
                Array.Empty<RealtimeItemPolicySummary>(),
                bagContentsId,
                bagContentsRevision);
        }

        private ItemDefinition Definition(string definitionId)
        {
            Assert.That(catalog.TryGetDefinition(definitionId, out var definition), Is.True);
            return definition;
        }

        private static InventorySlot Slot(InventoryContainer container, int slotIndex)
        {
            Assert.That(container.TryGetSlot(slotIndex, out var slot), Is.True);
            return slot;
        }

        private static CorpseLootItem CorpseItem(string definitionId, int quantity)
        {
            return new CorpseLootItem(Guid.NewGuid(), definitionId, quantity, 1, Guid.Empty, 0);
        }

        private static InventoryItem Item(string definitionId, int quantity, long revision)
        {
            return new InventoryItem(Guid.NewGuid(), definitionId, quantity, revision, Array.Empty<InventoryPolicy>());
        }
    }
}
