using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // Item operations show at once on top of the confirmed state. A committed change patches the
    // confirmed state without a reload, and a rejection or a newer state that no longer fits
    // undoes the pending operations.
    public sealed class InventoryPredictionTests
    {
        private const long KnownRevision = 5;
        private ClientItemCatalog catalog;

        [OneTimeSetUp]
        public void LoadCatalog()
        {
            catalog = FixtureItemCatalog.Load();
        }

        [Test]
        public void CommittedChangePatchesTheSnapshotToWhatTheServerReturns()
        {
            var ore = Item("fixture.ore", 10, 3);
            var state = CreateState(new[] { ore });
            var inventory = state.FullSnapshot.PermanentInventory;
            var bank = state.FullSnapshot.Bank;
            var changes = 0;
            state.Changed += () => changes++;

            Assert.That(
                state.TryApplyItemStateChange(Change(
                    40,
                    new[] { Revision(inventory, 2), Revision(bank, 2) },
                    Contained(ore, bank, 2, revision: 4))),
                Is.True);

            Assert.That(changes, Is.EqualTo(1));
            Assert.That(state.KnownItemStateRevision, Is.EqualTo(KnownRevision + 1));
            Assert.That(state.HasCoherentFullSnapshotAtLeast(KnownRevision + 1), Is.True);
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 0).Item, Is.Null);
            var moved = Slot(state.FullSnapshot.Bank, 2).Item;
            Assert.That(moved.ItemInstanceId, Is.EqualTo(ore.ItemInstanceId));
            Assert.That(moved.Revision, Is.EqualTo(4));
            Assert.That(state.FullSnapshot.CarriedWeight, Is.EqualTo(40));
            Assert.That(state.FullSnapshot.Currency, Is.EqualTo(75));

            // The patched snapshot equals what GameService returns for the new revision.
            var authoritative = Snapshot(
                state.CharacterId,
                KnownRevision + 1,
                Container(inventory.ContainerId, "permanent_inventory", 2),
                Container(bank.ContainerId, "bank", 2, (2, Item(ore.ItemInstanceId, "fixture.ore", 10, 4))),
                state.FullSnapshot.RecoveryStorage,
                40,
                75);
            Assert.That(state.ApplyFull(authoritative), Is.EqualTo(InventorySnapshotApplyResult.Unchanged));
        }

        [Test]
        public void AReconnectThatChangedNothingMakesTheInventoryUsableAgain()
        {
            var ore = Item("fixture.ore", 10, 3);
            var state = CreateState(new[] { ore });
            var unchanged = state.FullSnapshot;

            state.MarkDisconnected(false);
            Assert.That(state.CanPredict, Is.False);

            state.BeginLoading(false);
            Assert.That(state.ApplyFull(unchanged), Is.EqualTo(InventorySnapshotApplyResult.Unchanged));
            Assert.That(state.Status, Is.EqualTo(InventoryClientStatus.Ready));
            Assert.That(state.RequiresFullRefresh, Is.False);
            Assert.That(state.HasCoherentFullSnapshotAtLeast(KnownRevision), Is.True);
            Assert.That(state.CanPredict, Is.True);
            Assert.That(state.CanMutate, Is.True);
        }

        [Test]
        public void ChangesThePatchCannotDescribeRequireAReload()
        {
            var ore = Item("fixture.ore", 10, 3);

            // The change does not follow the known snapshot.
            AssertRefused(ore, state => new RealtimeOwnerItemStateChanged(
                Guid.Empty,
                KnownRevision + 1,
                new RealtimeCarryState(KnownRevision + 2, 40, 200),
                75,
                new[] { Revision(state.FullSnapshot.PermanentInventory, 2) },
                new[] { Contained(ore, state.FullSnapshot.PermanentInventory.ContainerId, 1, 4) },
                Array.Empty<Guid>()));

            // A changed container without its new revision.
            AssertRefused(ore, state => Change(
                40,
                Array.Empty<RealtimeContainerRevision>(),
                Contained(ore, state.FullSnapshot.PermanentInventory.ContainerId, 1, 4)));

            // The equipped Bag comes with contents that only a reload shows.
            AssertRefused(ore, state => Change(
                40,
                new[] { Revision(state.FullSnapshot.PermanentInventory, 2) },
                Equipped(ore, ItemEquipmentSlotIds.Bag, 4)));

            // Recovery Storage deliveries are not part of the change.
            AssertRefused(ore, state => Change(
                40,
                new[] { Revision(state.FullSnapshot.PermanentInventory, 2) },
                Contained(ore, state.FullSnapshot.RecoveryStorage.ContainerId, 0, 4)));

            // An occupied destination means the snapshot is not the one the change follows.
            var other = Item("fixture.potion", 1, 1);
            AssertRefused(
                ore,
                state => Change(
                    40,
                    new[] { Revision(state.FullSnapshot.PermanentInventory, 2) },
                    Contained(ore, state.FullSnapshot.PermanentInventory.ContainerId, 1, 4)),
                other);
        }

        [Test]
        public void ItemsThatLeaveForAnUnseenContainerLeaveTheView()
        {
            var ore = Item("fixture.ore", 10, 3);
            var state = CreateState(new[] { ore });
            var corpseContainerId = Guid.NewGuid();

            Assert.That(
                state.TryApplyItemStateChange(Change(
                    40,
                    new[]
                    {
                        Revision(state.FullSnapshot.PermanentInventory, 2),
                        new RealtimeContainerRevision(corpseContainerId, 9)
                    },
                    Contained(ore, corpseContainerId, 0, 4))),
                Is.True);
            Assert.That(state.TryFindItem(ore.ItemInstanceId, out _, out _), Is.False);
        }

        [Test]
        public void PredictedMoveShowsAtOnceAndTheCommittedChangeConfirmsIt()
        {
            var ore = Item("fixture.ore", 10, 3);
            var state = CreateState(new[] { ore });
            var inventory = state.FullSnapshot.PermanentInventory;

            Assert.That(
                state.TryPredict(
                    InventoryPredictedOperation.Relocate(ore.ItemInstanceId, inventory.ContainerId, 3),
                    out var error),
                Is.True,
                error);

            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 3).Item, Is.SameAs(ore));
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 0).Item, Is.Null);
            Assert.That(Slot(state.ConfirmedSnapshot.PermanentInventory, 0).Item, Is.SameAs(ore));
            Assert.That(state.HasPendingOperations, Is.True);
            Assert.That(state.CanMutate, Is.False);
            Assert.That(state.CanPredict, Is.True);
            Assert.That(state.TryFindConfirmedItem(ore.ItemInstanceId, out _, out var confirmedLocation), Is.True);
            Assert.That(confirmedLocation.SlotIndex, Is.Zero);

            state.PendingOperations[0].OperationId = Guid.NewGuid();
            Assert.That(
                state.TryApplyItemStateChange(Change(
                    100,
                    new[] { Revision(inventory, 2) },
                    Contained(ore, inventory.ContainerId, 3, 4))),
                Is.True);
            // The confirmed state reached the prediction, which stays shown until the result.
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 3).Item.Revision, Is.EqualTo(4));

            state.CompletePendingHead(true);
            Assert.That(state.HasPendingOperations, Is.False);
            Assert.That(state.FullSnapshot, Is.SameAs(state.ConfirmedSnapshot));
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 3).Item.Revision, Is.EqualTo(4));
            Assert.That(state.CanMutate, Is.True);
        }

        [Test]
        public void TheChangeOfTheSentOperationConfirmsItWithoutApplyingItAgain()
        {
            var ore = Item("fixture.ore", 10, 3);
            var potion = Item("fixture.potion", 2, 1);
            var state = CreateState(new[] { ore, potion });
            var inventory = state.FullSnapshot.PermanentInventory;
            Assert.That(state.TryPredict(InventoryPredictedOperation.Swap(ore.ItemInstanceId, potion.ItemInstanceId), out var error), Is.True, error);
            var operationId = Guid.NewGuid();
            state.PendingOperations[0].OperationId = operationId;

            Assert.That(
                state.TryApplyItemStateChange(OperationChange(
                    operationId,
                    100,
                    new[] { Revision(inventory, 2) },
                    Contained(ore, inventory.ContainerId, 1, 4),
                    Contained(potion, inventory.ContainerId, 0, 2))),
                Is.True);

            // A swap applied a second time would swap the items back.
            Assert.That(state.HasPendingOperations, Is.False);
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 0).Item.ItemInstanceId, Is.EqualTo(potion.ItemInstanceId));
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 1).Item.ItemInstanceId, Is.EqualTo(ore.ItemInstanceId));
        }
        [Test]
        public void LaterMovesBuildOnEarlierPredictions()
        {
            var ore = Item("fixture.ore", 10, 3);
            var potion = Item("fixture.potion", 2, 1);
            var state = CreateState(new[] { ore, potion });
            var inventory = state.FullSnapshot.PermanentInventory.ContainerId;

            Assert.That(state.TryPredict(InventoryPredictedOperation.Relocate(ore.ItemInstanceId, inventory, 4), out var error), Is.True, error);
            // The second move uses the slot the first one emptied.
            Assert.That(state.TryPredict(InventoryPredictedOperation.Relocate(potion.ItemInstanceId, inventory, 0), out error), Is.True, error);

            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 0).Item, Is.SameAs(potion));
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 4).Item, Is.SameAs(ore));
            Assert.That(state.PendingOperations.Count, Is.EqualTo(2));
        }

        [Test]
        public void RejectionUndoesEveryPendingMoveAndNamesTheirItems()
        {
            var ore = Item("fixture.ore", 10, 3);
            var potion = Item("fixture.potion", 2, 1);
            var state = CreateState(new[] { ore, potion });
            var inventory = state.FullSnapshot.PermanentInventory.ContainerId;
            InventoryPredictionRollback rollback = null;
            state.PredictionRolledBack += value => rollback = value;
            Assert.That(state.TryPredict(InventoryPredictedOperation.Relocate(ore.ItemInstanceId, inventory, 4), out _), Is.True);
            Assert.That(state.TryPredict(InventoryPredictedOperation.Relocate(potion.ItemInstanceId, inventory, 0), out _), Is.True);

            state.RollBackPredictions(new InventoryClientError("item_slot_occupied", "The slot is occupied."));

            Assert.That(state.HasPendingOperations, Is.False);
            Assert.That(state.FullSnapshot, Is.SameAs(state.ConfirmedSnapshot));
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 0).Item, Is.SameAs(ore));
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 1).Item, Is.SameAs(potion));
            Assert.That(state.Error.Code, Is.EqualTo("item_slot_occupied"));
            Assert.That(rollback, Is.Not.Null);
            Assert.That(rollback.ItemInstanceIds, Is.EquivalentTo(new[] { ore.ItemInstanceId, potion.ItemInstanceId }));
            Assert.That(state.CanMutate, Is.True);
        }

        [Test]
        public void ANewerStateThatNoLongerFitsAPendingMoveUndoesIt()
        {
            var ore = Item("fixture.ore", 10, 3);
            var state = CreateState(new[] { ore });
            var inventory = state.FullSnapshot.PermanentInventory;
            InventoryPredictionRollback rollback = null;
            state.PredictionRolledBack += value => rollback = value;
            Assert.That(state.TryPredict(InventoryPredictedOperation.Relocate(ore.ItemInstanceId, inventory.ContainerId, 3), out _), Is.True);

            // A gathered stack lands in the slot the pending move wanted.
            var gathered = Item("fixture.potion", 1, 1);
            Assert.That(
                state.TryApplyItemStateChange(Change(
                    102,
                    new[] { Revision(inventory, 2) },
                    Contained(gathered, inventory.ContainerId, 3, 1))),
                Is.True);

            Assert.That(state.HasPendingOperations, Is.False);
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 0).Item.ItemInstanceId, Is.EqualTo(ore.ItemInstanceId));
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 3).Item.ItemInstanceId, Is.EqualTo(gathered.ItemInstanceId));
            Assert.That(rollback, Is.Not.Null);
            Assert.That(rollback.Error.Code, Is.EqualTo("item_move_superseded"));
            Assert.That(rollback.ItemInstanceIds, Does.Contain(ore.ItemInstanceId));
        }

        [Test]
        public void ASentMoveThatNoLongerFitsWaitsForItsResult()
        {
            var ore = Item("fixture.ore", 10, 3);
            var potion = Item("fixture.potion", 2, 1);
            var state = CreateState(new[] { ore, potion });
            var inventory = state.FullSnapshot.PermanentInventory;
            Assert.That(state.TryPredict(InventoryPredictedOperation.Relocate(ore.ItemInstanceId, inventory.ContainerId, 3), out _), Is.True);
            Assert.That(state.TryPredict(InventoryPredictedOperation.Relocate(potion.ItemInstanceId, inventory.ContainerId, 0), out _), Is.True);
            state.PendingOperations[0].OperationId = Guid.NewGuid();

            var gathered = Item("fixture.potion", 1, 1);
            Assert.That(
                state.TryApplyItemStateChange(Change(
                    102,
                    new[] { Revision(inventory, 2) },
                    Contained(gathered, inventory.ContainerId, 3, 1))),
                Is.True);

            // The server still answers the sent move, so only the move after it is undone.
            Assert.That(state.PendingOperations.Count, Is.EqualTo(1));
            Assert.That(state.PendingOperations[0].IsPredicted, Is.False);
            Assert.That(state.FullSnapshot, Is.SameAs(state.ConfirmedSnapshot));
        }

        [Test]
        public void SplitShowsANewStackThatWaitsForItsServerId()
        {
            var ore = Item("fixture.ore", 10, 3);
            var state = CreateState(new[] { ore });
            var inventory = state.FullSnapshot.PermanentInventory.ContainerId;
            var split = InventoryPredictedOperation.Split(ore.ItemInstanceId, 4, inventory, 2);

            Assert.That(state.TryPredict(split, out var error), Is.True, error);

            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 0).Item.Quantity, Is.EqualTo(6));
            var created = Slot(state.FullSnapshot.PermanentInventory, 2).Item;
            Assert.That(created.ItemInstanceId, Is.EqualTo(split.PredictedItemInstanceId));
            Assert.That(created.Quantity, Is.EqualTo(4));
            Assert.That(state.IsPredictedItem(created.ItemInstanceId), Is.True);
            Assert.That(state.FullSnapshot.CarriedWeight, Is.EqualTo(100));
        }

        [Test]
        public void MergeSwapAndEquipShowTheServersResult()
        {
            var ore = Item("fixture.ore", 10, 3);
            var moreOre = Item("fixture.ore", 5, 2);
            var sword = Item("fixture.sword", 1, 1);
            var bow = Item("fixture.bow", 1, 1);
            var state = CreateState(new[] { ore, moreOre, sword }, bow);

            Assert.That(state.TryPredict(InventoryPredictedOperation.Merge(moreOre.ItemInstanceId, ore.ItemInstanceId), out var error), Is.True, error);
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 0).Item.Quantity, Is.EqualTo(15));
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 1).Item, Is.Null);

            Assert.That(state.TryPredict(InventoryPredictedOperation.Swap(ore.ItemInstanceId, sword.ItemInstanceId), out error), Is.True, error);
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 0).Item, Is.SameAs(sword));
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 2).Item.ItemInstanceId, Is.EqualTo(ore.ItemInstanceId));

            // Equipping onto the occupied main hand swaps the bow into the sword's slot.
            Assert.That(state.TryPredict(InventoryPredictedOperation.Equip(sword.ItemInstanceId, ItemEquipmentSlotIds.MainHand), out error), Is.True, error);
            Assert.That(MainHand(state).Item, Is.SameAs(sword));
            Assert.That(Slot(state.FullSnapshot.PermanentInventory, 0).Item, Is.SameAs(bow));
            var swordWeight = ItemWeightRules.CalculateStackWeight(Definition("fixture.sword").UnitWeight, 1);
            var bowWeight = ItemWeightRules.CalculateStackWeight(Definition("fixture.bow").UnitWeight, 1);
            Assert.That(state.FullSnapshot.CarriedWeight, Is.EqualTo(100 - swordWeight + bowWeight));
        }

        [Test]
        public void AnInvalidMoveIsNotShown()
        {
            var ore = Item("fixture.ore", 10, 3);
            var potion = Item("fixture.potion", 2, 1);
            var state = CreateState(new[] { ore, potion });

            Assert.That(
                state.TryPredict(
                    InventoryPredictedOperation.Relocate(ore.ItemInstanceId, state.FullSnapshot.PermanentInventory.ContainerId, 1),
                    out var error),
                Is.False);
            Assert.That(error, Is.Not.Empty);
            Assert.That(state.HasPendingOperations, Is.False);
        }

        [Test]
        public void ThePendingQueueIsBounded()
        {
            var ore = Item("fixture.ore", 10, 3);
            var state = CreateState(new[] { ore });
            var inventory = state.FullSnapshot.PermanentInventory.ContainerId;
            for (var index = 0; index < InventoryClientState.MaximumPendingOperations; index++)
            {
                Assert.That(
                    state.TryPredict(InventoryPredictedOperation.Relocate(ore.ItemInstanceId, inventory, index % 2 == 0 ? 3 : 0), out var error),
                    Is.True,
                    error);
            }

            Assert.That(state.CanPredict, Is.False);
            Assert.That(
                state.TryPredict(InventoryPredictedOperation.Relocate(ore.ItemInstanceId, inventory, 4), out var full),
                Is.False);
            Assert.That(full, Does.Contain("Too many"));
        }

        private void AssertRefused(
            InventoryItem ore,
            Func<InventoryClientState, RealtimeOwnerItemStateChanged> change,
            InventoryItem occupant = null)
        {
            var state = CreateState(occupant == null ? new[] { ore } : new[] { ore, occupant });
            var before = state.FullSnapshot;
            var changes = 0;
            state.Changed += () => changes++;

            Assert.That(state.TryApplyItemStateChange(change(state)), Is.False);

            Assert.That(changes, Is.Zero);
            Assert.That(state.FullSnapshot, Is.SameAs(before));
            Assert.That(state.KnownItemStateRevision, Is.EqualTo(KnownRevision));
        }

        private InventoryClientState CreateState(
            IReadOnlyList<InventoryItem> inventoryItems,
            InventoryItem mainHand = null,
            RecoveryDelivery[] deliveries = null)
        {
            var characterId = Guid.NewGuid();
            var state = new InventoryClientState();
            state.SetCatalog(catalog);
            state.PrepareCharacter(characterId);
            var slots = inventoryItems.Select((item, index) => (index, item)).ToArray();
            Assert.That(
                state.ApplyFull(Snapshot(
                    characterId,
                    KnownRevision,
                    Container(Guid.NewGuid(), "permanent_inventory", 1, slots),
                    Container(Guid.NewGuid(), "bank", 1),
                    new RecoveryStorageInventory(Guid.NewGuid(), 1, deliveries ?? Array.Empty<RecoveryDelivery>()),
                    100,
                    75,
                    mainHand)),
                Is.EqualTo(InventorySnapshotApplyResult.Applied));
            return state;
        }

        private CharacterInventorySnapshot Snapshot(
            Guid characterId,
            long revision,
            InventoryContainer inventory,
            InventoryContainer bank,
            RecoveryStorageInventory recovery,
            long weight,
            long currency,
            InventoryItem mainHand = null)
        {
            return new CharacterInventorySnapshot(
                characterId,
                catalog.Revision,
                revision,
                inventory,
                new[]
                {
                    new InventoryEquipmentSlot(ItemEquipmentSlotIds.MainHand, 2, mainHand),
                    new InventoryEquipmentSlot(ItemEquipmentSlotIds.Bag, 9, null)
                },
                null,
                bank,
                recovery,
                weight,
                200,
                InventorySnapshotMapper.CalculateLoadRatioBasisPoints(weight, 200),
                EncumbranceRules.CalculateMovementMultiplierBasisPoints(weight, 200),
                currency);
        }

        private static InventoryContainer Container(
            Guid containerId,
            string type,
            long revision,
            params (int SlotIndex, InventoryItem Item)[] items)
        {
            return new InventoryContainer(
                containerId,
                type,
                revision,
                8,
                Enumerable.Range(0, 8)
                    .Select(index => new InventorySlot(
                        index,
                        "general",
                        Array.Empty<string>(),
                        items.FirstOrDefault(entry => entry.SlotIndex == index).Item))
                    .ToArray());
        }

        private static RealtimeOwnerItemStateChanged Change(
            long weight,
            RealtimeContainerRevision[] containers,
            params RealtimeItemStateChange[] items)
        {
            return OperationChange(Guid.Empty, weight, containers, items);
        }

        private static RealtimeOwnerItemStateChanged OperationChange(
            Guid operationId,
            long weight,
            RealtimeContainerRevision[] containers,
            params RealtimeItemStateChange[] items)
        {
            return ClaimChange(operationId, weight, containers, null, items);
        }

        private static RealtimeOwnerItemStateChanged ClaimChange(
            Guid operationId,
            long weight,
            RealtimeContainerRevision[] containers,
            Guid[] claimed,
            params RealtimeItemStateChange[] items)
        {
            return new RealtimeOwnerItemStateChanged(
                operationId,
                KnownRevision,
                new RealtimeCarryState(KnownRevision + 1, weight, 200),
                75,
                containers,
                items,
                claimed ?? Array.Empty<Guid>());
        }

        private static RealtimeContainerRevision Revision(InventoryContainer container, long revision)
        {
            return new RealtimeContainerRevision(container.ContainerId, revision);
        }

        private static RealtimeItemStateChange Contained(InventoryItem item, InventoryContainer container, int slotIndex, long revision)
        {
            return Contained(item, container.ContainerId, slotIndex, revision);
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
            Guid bagContentsId = default,
            long bagContentsRevision = 0)
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

        private static InventoryEquipmentSlot MainHand(InventoryClientState state)
        {
            return state.FullSnapshot.Equipment.Single(slot => slot.SlotId == ItemEquipmentSlotIds.MainHand);
        }

        private static InventoryItem Item(string definitionId, int quantity, long revision)
        {
            return Item(Guid.NewGuid(), definitionId, quantity, revision);
        }

        private static InventoryItem Item(Guid itemInstanceId, string definitionId, int quantity, long revision)
        {
            return new InventoryItem(itemInstanceId, definitionId, quantity, revision, Array.Empty<InventoryPolicy>());
        }
    }
}
