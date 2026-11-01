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

        [Test]
        public void AWorkerRevisionThatNoIntentCausedRefreshesAfterOwnWork()
        {
            var follower = new InventoryServerRevisionFollower();

            // No joined session, or the inventory already knows the revision.
            Assert.That(follower.ShouldRefresh(-1, -1, false, false, 0f), Is.False);
            Assert.That(follower.ShouldRefresh(8, 8, false, false, 0f), Is.False);
            Assert.That(follower.ShouldRefresh(7, 8, false, false, 0f), Is.False);

            // An own operation or refresh in flight settles first.
            Assert.That(follower.ShouldRefresh(8, 7, true, false, 0f), Is.False);
            Assert.That(follower.ShouldRefresh(8, 7, false, true, 0f), Is.False);

            // A death commits a newer revision; the inventory asks for it at once.
            Assert.That(follower.ShouldRefresh(8, 7, false, false, 0f), Is.True);
            Assert.That(follower.ShouldRefresh(8, 7, false, true, 0.5f), Is.False);

            // The refresh reached it, so nothing is asked again.
            Assert.That(follower.ShouldRefresh(8, 8, false, false, 1f), Is.False);

            // A newer revision is asked for at once again.
            Assert.That(follower.ShouldRefresh(9, 8, false, false, 2f), Is.True);
        }

        [Test]
        public void AFailedWorkerRevisionRefreshRetriesWithADoublingDelay()
        {
            var follower = new InventoryServerRevisionFollower();
            Assert.That(follower.ShouldRefresh(8, 7, false, false, 0f), Is.True);
            Assert.That(follower.ShouldRefresh(8, 7, false, true, 3f), Is.False);

            // The refresh ended at 10 s without reaching revision 8; the delay starts then.
            Assert.That(follower.ShouldRefresh(8, 7, false, false, 10f), Is.False);
            Assert.That(follower.ShouldRefresh(8, 7, false, false, 10.9f), Is.False);
            Assert.That(follower.ShouldRefresh(8, 7, false, false, 11f), Is.True);

            // Every further failure doubles the delay up to the cap.
            var now = 11f;
            var expectedDelay = InventoryServerRevisionFollower.FirstRetryDelaySeconds * 2f;
            for (var retry = 0; retry < 8; retry++)
            {
                Assert.That(follower.ShouldRefresh(8, 7, false, false, now), Is.False);
                Assert.That(
                    follower.ShouldRefresh(8, 7, false, false, now + expectedDelay - 0.01f),
                    Is.False);
                now += expectedDelay;
                Assert.That(follower.ShouldRefresh(8, 7, false, false, now), Is.True);
                expectedDelay = Math.Min(
                    expectedDelay * 2f,
                    InventoryServerRevisionFollower.MaximumRetryDelaySeconds);
            }

            Assert.That(
                expectedDelay,
                Is.EqualTo(InventoryServerRevisionFollower.MaximumRetryDelaySeconds));

            // A newer revision starts over without a delay, and so does a new session.
            Assert.That(follower.ShouldRefresh(9, 7, false, false, now), Is.True);
            follower.Reset();
            Assert.That(follower.ShouldRefresh(9, 7, false, false, now), Is.True);
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
