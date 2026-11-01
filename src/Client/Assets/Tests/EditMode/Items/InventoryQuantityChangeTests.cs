using System;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // Spent arrows reach the inventory as OwnerItemQuantityChanged. A patch keeps the
    // equipped stack current without a read; every other case falls back to the refresh
    // that EnsureFullState starts when no coherent snapshot reaches the new revision.
    public sealed class InventoryQuantityChangeTests
    {
        private const long KnownRevision = 5;
        private ClientItemCatalog catalog;

        [OneTimeSetUp]
        public void LoadCatalog()
        {
            catalog = FixtureItemCatalog.Load();
        }

        [Test]
        public void ChangeAtTheKnownRevisionPatchesTheEquippedArrowStack()
        {
            var bow = Item("fixture.bow", 1, 4);
            var arrows = Item("fixture.arrow", 30, 2);
            var state = CreateState(bow, arrows);
            var changes = 0;
            state.Changed += () => changes++;

            Assert.That(
                state.TryApplyEquippedQuantityChange(Change(arrows, KnownRevision, KnownRevision + 1, 25, 3)),
                Is.True);

            Assert.That(changes, Is.EqualTo(1));
            Assert.That(state.KnownItemStateRevision, Is.EqualTo(KnownRevision + 1));
            Assert.That(state.HasCoherentFullSnapshotAtLeast(KnownRevision + 1), Is.True);
            Assert.That(state.CanMutate, Is.True);
            var patched = Slot(state, ItemEquipmentSlotIds.Ammunition).Item;
            Assert.That(patched.ItemInstanceId, Is.EqualTo(arrows.ItemInstanceId));
            Assert.That(patched.DefinitionId, Is.EqualTo("fixture.arrow"));
            Assert.That(patched.Quantity, Is.EqualTo(25));
            Assert.That(patched.Revision, Is.EqualTo(3));
            Assert.That(Slot(state, ItemEquipmentSlotIds.MainHand).Item, Is.SameAs(bow));
            Assert.That(state.FullSnapshot.CarriedWeight, Is.EqualTo(95));
            Assert.That(state.FullSnapshot.LoadRatioBasisPoints, Is.EqualTo(4_750));
            Assert.That(
                state.FullSnapshot.MovementMultiplierBasisPoints,
                Is.EqualTo(EncumbranceRules.CalculateMovementMultiplierBasisPoints(95, 200)));

            // The patched snapshot equals what GameService returns for the new revision.
            var source = state.FullSnapshot;
            var authoritative = new CharacterInventorySnapshot(
                source.CharacterId,
                catalog.Revision,
                KnownRevision + 1,
                source.PermanentInventory,
                new[]
                {
                    new InventoryEquipmentSlot(ItemEquipmentSlotIds.MainHand, 2, bow),
                    new InventoryEquipmentSlot(
                        ItemEquipmentSlotIds.Ammunition,
                        4,
                        Item(arrows.ItemInstanceId, "fixture.arrow", 25, 3))
                },
                null,
                source.Bank,
                source.RecoveryStorage,
                95,
                200,
                4_750,
                EncumbranceRules.CalculateMovementMultiplierBasisPoints(95, 200));
            Assert.That(state.ApplyFull(authoritative), Is.EqualTo(InventorySnapshotApplyResult.Unchanged));
        }

        [Test]
        public void UsedUpStackLeavesTheAmmunitionSlotEmpty()
        {
            var arrows = Item("fixture.arrow", 3, 7);
            var state = CreateState(Item("fixture.bow", 1, 4), arrows);

            Assert.That(
                state.TryApplyEquippedQuantityChange(Change(arrows, KnownRevision, KnownRevision + 1, 0, 0)),
                Is.True);

            var slot = Slot(state, ItemEquipmentSlotIds.Ammunition);
            Assert.That(slot.Item, Is.Null);
            Assert.That(slot.SortOrder, Is.EqualTo(4));
            Assert.That(state.FullSnapshot.Equipment.Count, Is.EqualTo(2));
            Assert.That(state.TryFindItem(arrows.ItemInstanceId, out _, out _), Is.False);
        }

        [Test]
        public void PatchKeepsAPendingOperationBusy()
        {
            var arrows = Item("fixture.arrow", 30, 2);
            var state = CreateState(Item("fixture.bow", 1, 4), arrows);
            state.BeginLoading(true);

            Assert.That(
                state.TryApplyEquippedQuantityChange(Change(arrows, KnownRevision, KnownRevision + 1, 29, 3)),
                Is.True);

            Assert.That(state.Status, Is.EqualTo(InventoryClientStatus.Busy));
            Assert.That(state.CanMutate, Is.False);
        }

        [Test]
        public void ChangeThatDoesNotFollowTheKnownSnapshotRequiresARefresh()
        {
            var arrows = Item("fixture.arrow", 30, 2);

            // The client is behind: it never saw the state the change applies to.
            AssertRefreshRequired(arrows, Change(arrows, KnownRevision + 1, KnownRevision + 2, 25, 3));

            // Another transaction committed between the known state and the change.
            AssertRefreshRequired(arrows, Change(arrows, KnownRevision, KnownRevision + 2, 25, 3));

            // The stack is not the equipped ammunition the client knows.
            AssertRefreshRequired(arrows, Change(Item("fixture.arrow", 30, 2), KnownRevision, KnownRevision + 1, 25, 3));

            // Consumption never grows a stack or keeps its revision.
            AssertRefreshRequired(arrows, Change(arrows, KnownRevision, KnownRevision + 1, 30, 3));
            AssertRefreshRequired(arrows, Change(arrows, KnownRevision, KnownRevision + 1, 25, 2));
        }

        [Test]
        public void StackOutsideTheAmmunitionSlotRequiresARefresh()
        {
            var bow = Item("fixture.bow", 1, 4);
            var state = CreateState(bow, Item("fixture.arrow", 30, 2));
            var change = Change(bow, KnownRevision, KnownRevision + 1, 0, 0);

            Assert.That(state.TryApplyEquippedQuantityChange(change), Is.False);
            Assert.That(Slot(state, ItemEquipmentSlotIds.MainHand).Item, Is.SameAs(bow));
            Assert.That(state.HasCoherentFullSnapshotAtLeast(KnownRevision + 1), Is.False);
        }

        [Test]
        public void IncoherentSnapshotRequiresARefresh()
        {
            var arrows = Item("fixture.arrow", 30, 2);
            var state = CreateState(Item("fixture.bow", 1, 4), arrows);
            // A focused Bank read reached a newer revision than the complete snapshot.
            Assert.That(
                state.ApplyBank(new CharacterBankSnapshot(
                    state.CharacterId,
                    catalog.Revision,
                    KnownRevision + 1,
                    state.FullSnapshot.Bank)),
                Is.EqualTo(InventorySnapshotApplyResult.Applied));
            Assert.That(state.RequiresFullRefresh, Is.True);

            Assert.That(
                state.TryApplyEquippedQuantityChange(Change(arrows, KnownRevision, KnownRevision + 1, 25, 3)),
                Is.False);
            Assert.That(Slot(state, ItemEquipmentSlotIds.Ammunition).Item, Is.SameAs(arrows));
            Assert.That(state.HasCoherentFullSnapshotAtLeast(KnownRevision + 1), Is.False);
        }

        [Test]
        public void ChangeTheSnapshotAlreadyContainsNeedsNoRefresh()
        {
            // A refresh that raced the message already returned the committed state.
            var arrows = Item("fixture.arrow", 25, 3);
            var state = CreateState(Item("fixture.bow", 1, 4), arrows);
            var change = Change(arrows, KnownRevision - 1, KnownRevision, 25, 3);

            Assert.That(state.TryApplyEquippedQuantityChange(change), Is.False);
            Assert.That(state.HasCoherentFullSnapshotAtLeast(change.CarryState.ItemStateRevision), Is.True);
            Assert.That(Slot(state, ItemEquipmentSlotIds.Ammunition).Item, Is.SameAs(arrows));
        }

        private void AssertRefreshRequired(InventoryItem arrows, RealtimeOwnerItemQuantityChanged change)
        {
            var state = CreateState(Item("fixture.bow", 1, 4), arrows);
            var changes = 0;
            state.Changed += () => changes++;

            Assert.That(state.TryApplyEquippedQuantityChange(change), Is.False);

            Assert.That(changes, Is.Zero);
            Assert.That(state.KnownItemStateRevision, Is.EqualTo(KnownRevision));
            Assert.That(Slot(state, ItemEquipmentSlotIds.Ammunition).Item, Is.SameAs(arrows));
            Assert.That(state.FullSnapshot.CarriedWeight, Is.EqualTo(100));
            Assert.That(
                state.HasCoherentFullSnapshotAtLeast(change.CarryState.ItemStateRevision),
                Is.False);
        }

        private InventoryClientState CreateState(InventoryItem mainHand, InventoryItem ammunition)
        {
            var characterId = Guid.NewGuid();
            var snapshot = new CharacterInventorySnapshot(
                characterId,
                catalog.Revision,
                KnownRevision,
                Container("permanent_inventory", 20),
                new[]
                {
                    new InventoryEquipmentSlot(ItemEquipmentSlotIds.MainHand, 2, mainHand),
                    new InventoryEquipmentSlot(ItemEquipmentSlotIds.Ammunition, 4, ammunition)
                },
                null,
                Container("bank", 40),
                new RecoveryStorageInventory(Guid.NewGuid(), 1, Array.Empty<RecoveryDelivery>()),
                100,
                200,
                5_000,
                EncumbranceRules.CalculateMovementMultiplierBasisPoints(100, 200));
            var state = new InventoryClientState();
            state.SetCatalog(catalog);
            state.PrepareCharacter(characterId);
            Assert.That(state.ApplyFull(snapshot), Is.EqualTo(InventorySnapshotApplyResult.Applied));
            return state;
        }

        private static RealtimeOwnerItemQuantityChanged Change(
            InventoryItem item,
            long previousRevision,
            long revision,
            int quantity,
            long itemRevision)
        {
            return new RealtimeOwnerItemQuantityChanged(
                RealtimeItemQuantityChangeReason.AmmunitionConsumed,
                previousRevision,
                new RealtimeCarryState(revision, 95, 200),
                item.ItemInstanceId,
                quantity,
                itemRevision);
        }

        private static InventoryEquipmentSlot Slot(InventoryClientState state, string slotId)
        {
            return state.FullSnapshot.Equipment.Single(slot => slot.SlotId == slotId);
        }

        private static InventoryContainer Container(string type, int capacity)
        {
            return new InventoryContainer(
                Guid.NewGuid(),
                type,
                1,
                capacity,
                new[] { new InventorySlot(0, "general", Array.Empty<string>(), null) });
        }

        private static InventoryItem Item(string definitionId, int quantity, long revision)
        {
            return Item(Guid.NewGuid(), definitionId, quantity, revision);
        }

        private static InventoryItem Item(
            Guid itemInstanceId,
            string definitionId,
            int quantity,
            long revision)
        {
            return new InventoryItem(
                itemInstanceId,
                definitionId,
                quantity,
                revision,
                Array.Empty<InventoryPolicy>());
        }
    }
}
