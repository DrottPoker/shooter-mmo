using System;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.Client.Quests;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // A turn-in's rewards fit by GameService's placement rules: the items the turn-in takes and
    // the quest's own quest items make room first, then rewards top up stacks and take free slots,
    // and the weight and the balance stay within their limits. The check refuses only what
    // GameService refuses too.
    public sealed class QuestRewardFitTests
    {
        private static readonly Guid CharacterId = Guid.Parse("c0000000-0000-4000-8000-000000000003");

        [Test]
        public void RewardsTopUpStacksAndTakeFreeSlots()
        {
            var items = FixtureItemCatalog.Load();
            var wolves = Quest(FixtureQuestCatalog.Wolves);
            var oneFree = Snapshot(items, 0, Item("fixture.ore", 100), Item("fixture.ore", 100), Item("fixture.ore", 100), Item("fixture.ore", 100));

            Assert.That(QuestRewardFit.Fits(wolves, string.Empty, oneFree, items, out _), Is.True, "The potion takes the free slot.");
            Assert.That(QuestRewardFit.Fits(wolves, "fixture.sword", oneFree, items, out var reason), Is.False, "The sword finds no slot after the potion.");
            Assert.That(reason, Is.EqualTo("Make room in your bags for the rewards first."));

            var potions = Snapshot(items, 0, Item("fixture.ore", 100), Item("fixture.ore", 100), Item("fixture.ore", 100), Item("fixture.potion", 9));
            Assert.That(QuestRewardFit.Fits(wolves, "fixture.sword", potions, items, out _), Is.True, "The potion tops up the stack.");

            var protectedPotions = Snapshot(items, 0, Item("fixture.ore", 100), Item("fixture.ore", 100), Item("fixture.ore", 100), Item("fixture.potion", 9, true));
            Assert.That(
                QuestRewardFit.Fits(wolves, "fixture.sword", protectedPotions, items, out _),
                Is.False,
                "A stack with a policy takes no reward.");
        }

        // The turn-in removes the quest's quest items, and a collect objective may empty a stack
        // of ordinary items, so their slots take rewards.
        [Test]
        public void TheItemsATurnInTakesMakeRoomForItsRewards()
        {
            var items = FixtureItemCatalog.Load();
            var trophies = Quest(FixtureQuestCatalog.Trophies);
            trophies.RewardItems = new[] { new QuestItemQuantityDefinition { DefinitionId = "fixture.sword", Quantity = 1 } };
            var full = Snapshot(
                items,
                0,
                Item("fixture.ore", 100),
                Item("fixture.ore", 100),
                Item("fixture.ore", 100),
                Item("fixture.trophy", 1, true),
                Item("fixture.trophy", 1, true));
            Assert.That(QuestRewardFit.Fits(trophies, string.Empty, full, items, out _), Is.True);

            var oreRun = Quest(FixtureQuestCatalog.OreRun);
            oreRun.RewardItems = new[] { new QuestItemQuantityDefinition { DefinitionId = "fixture.sword", Quantity = 1 } };
            var smallStacks = Snapshot(
                items,
                0,
                Item("fixture.ore", 2),
                Item("fixture.ore", 2),
                Item("fixture.potion", 10),
                Item("fixture.potion", 10),
                Item("fixture.potion", 10));
            Assert.That(QuestRewardFit.Fits(oreRun, string.Empty, smallStacks, items, out _), Is.True, "Three ore can empty a stack of two.");

            var oneStack = Snapshot(
                items,
                0,
                Item("fixture.ore", 5),
                Item("fixture.potion", 10),
                Item("fixture.potion", 10),
                Item("fixture.potion", 10),
                Item("fixture.potion", 10));
            Assert.That(QuestRewardFit.Fits(oreRun, string.Empty, oneStack, items, out _), Is.False, "Three of five ore leave the stack in its slot.");
        }

        // A sword (25) and a potion (2) on 1,000 of capacity: the hard cap is 1,400.
        [Test]
        public void TheWeightAndTheBalanceStayWithinTheirLimits()
        {
            var items = FixtureItemCatalog.Load();
            var wolves = Quest(FixtureQuestCatalog.Wolves);

            Assert.That(QuestRewardFit.Fits(wolves, "fixture.sword", Snapshot(items, 1373), items, out _), Is.True);
            Assert.That(QuestRewardFit.Fits(wolves, "fixture.sword", Snapshot(items, 1374), items, out var heavy), Is.False);
            Assert.That(heavy, Is.EqualTo("The rewards would be too heavy to carry."));

            var rich = Snapshot(items, 0, VendorPricing.MaximumCurrencyBalance - wolves.RewardGold + 1);
            Assert.That(QuestRewardFit.Fits(wolves, string.Empty, rich, items, out var balance), Is.False);
            Assert.That(balance, Is.EqualTo("Your balance cannot hold the Gold reward."));
            Assert.That(
                QuestRewardFit.Fits(wolves, string.Empty, Snapshot(items, 0, VendorPricing.MaximumCurrencyBalance - wolves.RewardGold), items, out _),
                Is.True);
        }

        private static QuestDefinition Quest(string questId)
        {
            Assert.That(FixtureQuestCatalog.Load().TryGet(questId, out var quest), Is.True);
            return quest;
        }

        private static InventoryItem Item(string definitionId, int quantity, bool questGrant = false)
        {
            return new InventoryItem(
                Guid.NewGuid(),
                definitionId,
                quantity,
                1,
                questGrant
                    ? new[] { new InventoryPolicy(ItemPolicyIds.ProtectedOnDeath, ItemPolicyRules.ActiveStatus, QuestCarriedItems.QuestGrantProtection) }
                    : Array.Empty<InventoryPolicy>());
        }

        // A permanent inventory of five slots that holds the items in order, on 1,000 of capacity.
        private static CharacterInventorySnapshot Snapshot(ClientItemCatalog catalog, long carriedWeight, params InventoryItem[] carried)
        {
            return Snapshot(catalog, carriedWeight, 0, carried);
        }

        private static CharacterInventorySnapshot Snapshot(
            ClientItemCatalog catalog,
            long carriedWeight,
            long currency,
            params InventoryItem[] carried)
        {
            var slots = Enumerable.Range(0, 5)
                .Select(index => new InventorySlot(index, "general", Array.Empty<string>(), index < carried.Length ? carried[index] : null))
                .ToArray();
            return new CharacterInventorySnapshot(
                CharacterId,
                catalog.Revision,
                3,
                new InventoryContainer(Guid.NewGuid(), "permanent_inventory", 1, slots.Length, slots),
                Array.Empty<InventoryEquipmentSlot>(),
                null,
                new InventoryContainer(Guid.NewGuid(), "bank", 1, 0, Array.Empty<InventorySlot>()),
                new RecoveryStorageInventory(Guid.NewGuid(), 1, Array.Empty<RecoveryDelivery>()),
                carriedWeight,
                1000,
                0,
                10_000,
                currency);
        }
    }
}
