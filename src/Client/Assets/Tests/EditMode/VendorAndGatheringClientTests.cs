using System;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class VendorAndGatheringClientTests
    {
        private const string VendorId = "vendor.fixture_store";
        private ClientItemCatalog catalog;
        private VendorProfileDefinition vendor;

        [OneTimeSetUp]
        public void LoadCatalog()
        {
            catalog = FixtureItemCatalog.Load();
            Assert.That(VendorClientRules.TryResolve(catalog, VendorId, out vendor, out var error), Is.True, error);
        }

        [Test]
        public void OffersArePricedWithTheSharedRuleInContentOrder()
        {
            var offers = VendorClientRules.ListOffers(catalog, vendor);

            Assert.That(
                offers.Select(offer => offer.DefinitionId),
                Is.EqualTo(new[] { "fixture.arrow", "fixture.potion", "fixture.pickaxe", "fixture.pack" }));
            Assert.That(offers.Select(offer => offer.UnitPrice), Is.EqualTo(new long[] { 1, 25, 50, 200 }));
            Assert.That(offers[0].MaximumQuantity, Is.EqualTo(60));
            Assert.That(VendorClientRules.FormatAmount(catalog, 1250), Is.EqualTo("1,250 Fixture Coins"));
            Assert.That(VendorClientRules.TryResolve(catalog, "vendor.unknown", out _, out var error), Is.False);
            Assert.That(error, Does.Contain("Update the client"));
        }

        [Test]
        public void BuyingChecksTheOfferQuantityBalanceAndWeight()
        {
            var snapshot = CreateSnapshot(30, null);

            Assert.That(Buy(snapshot, "fixture.potion", 1, out var price, out var reason), Is.True, reason);
            Assert.That(price, Is.EqualTo(25));
            Assert.That(Buy(snapshot, "fixture.potion", 2, out _, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("You need 50 Fixture Coins."));
            Assert.That(Buy(snapshot, "fixture.potion", 0, out _, out reason), Is.False);
            Assert.That(Buy(snapshot, "fixture.potion", 11, out _, out reason), Is.False);
            Assert.That(reason, Does.Contain("from 1 to 10"));
            Assert.That(Buy(snapshot, "fixture.ore", 1, out _, out reason), Is.False);
            Assert.That(reason, Does.Contain("does not sell"));

            // 30 arrows weigh 30 against a capacity of 20, above the 28 hard cap.
            var heavy = CreateSnapshot(1000, null, carriedWeight: 0, carryCapacity: 20);
            Assert.That(Buy(heavy, "fixture.arrow", 30, out _, out reason), Is.False);
            Assert.That(reason, Does.Contain("too heavy"));
            Assert.That(Buy(heavy, "fixture.arrow", 28, out price, out reason), Is.True, reason);
            Assert.That(price, Is.EqualTo(28));
        }

        [Test]
        public void SellingRoundsDownAndAcceptsPartialStacks()
        {
            var ore = Item("fixture.ore", 5);
            var snapshot = CreateSnapshot(0, ore);
            var location = InventoryLocation(snapshot);

            Assert.That(Sell(snapshot, ore, location, 5, out var price, out var reason), Is.True, reason);
            Assert.That(price, Is.EqualTo(20));
            Assert.That(Sell(snapshot, ore, location, 3, out price, out reason), Is.True, reason);
            Assert.That(price, Is.EqualTo(12));
            Assert.That(Sell(snapshot, ore, location, 6, out _, out reason), Is.False);

            var arrows = Item("fixture.arrow", 3);
            var arrowSnapshot = CreateSnapshot(0, arrows);
            Assert.That(
                Sell(arrowSnapshot, arrows, InventoryLocation(arrowSnapshot), 3, out price, out reason),
                Is.True,
                reason);
            Assert.That(price, Is.EqualTo(1));
            Assert.That(
                Sell(arrowSnapshot, arrows, InventoryLocation(arrowSnapshot), 2, out _, out reason),
                Is.False);
            Assert.That(reason, Does.Contain("pays nothing"));
        }

        [Test]
        public void SellingRefusesQuestProtectedEquippedAndBankedItems()
        {
            var letter = Item("fixture.letter", 1);
            var letterSnapshot = CreateSnapshot(0, letter);
            Assert.That(
                Sell(letterSnapshot, letter, InventoryLocation(letterSnapshot), 1, out _, out var reason),
                Is.False);
            Assert.That(reason, Does.Contain("does not buy"));

            var insured = new InventoryItem(
                Guid.NewGuid(),
                "fixture.ring",
                1,
                1,
                new[] { new InventoryPolicy(ItemPolicyIds.Insured, ItemPolicyRules.ActiveStatus, string.Empty) });
            var insuredSnapshot = CreateSnapshot(0, insured);
            Assert.That(
                Sell(insuredSnapshot, insured, InventoryLocation(insuredSnapshot), 1, out _, out reason),
                Is.False);
            Assert.That(reason, Does.Contain("insured"));

            var sword = Item("fixture.sword", 1);
            var snapshot = CreateSnapshot(0, sword);
            Assert.That(
                Sell(snapshot, sword, InventoryItemLocation.ForEquipment("main_hand"), 1, out _, out reason),
                Is.False);
            Assert.That(reason, Does.Contain("carry"));
            var bank = new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                snapshot.Bank.ContainerId,
                ItemContainerTypeIds.Bank,
                0,
                string.Empty,
                Guid.Empty);
            Assert.That(Sell(snapshot, sword, bank, 1, out _, out reason), Is.False);
        }

        [Test]
        public void GatheringStateFollowsOneGatheringSession()
        {
            var state = new GatheringClientState();
            var interaction = Guid.NewGuid();
            var other = Guid.NewGuid();
            var runtime = Guid.NewGuid();
            var changes = 0;
            state.Changed += () => changes++;
            Assert.That(state.UsedEquipmentSlot, Is.Empty);

            Assert.That(
                state.TryApply(RealtimeGatheringState.CreateStarted(interaction, 9, runtime, 3000), other, 1f, out _),
                Is.False);
            Assert.That(
                state.TryApply(RealtimeGatheringState.CreateStarted(interaction, 9, runtime, 3000), interaction, 1f, out var error),
                Is.True,
                error);
            Assert.That(state.Status, Is.EqualTo(GatheringClientStatus.Gathering));
            // The body holds the tool of the Tool slot while it gathers.
            Assert.That(state.UsedEquipmentSlot, Is.EqualTo("tool"));
            Assert.That(state.CycleProgress(2.5f), Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(state.CycleProgress(9f), Is.EqualTo(1f));

            Assert.That(
                state.TryApply(
                    RealtimeGatheringState.CreateYielded(other, 9, runtime, 3000, "fixture.ore", 1, 4),
                    interaction,
                    4f,
                    out _),
                Is.False);
            // A yield still completes after the interaction closed.
            Assert.That(
                state.TryApply(
                    RealtimeGatheringState.CreateYielded(interaction, 9, runtime, 3000, "fixture.ore", 1, 4),
                    Guid.Empty,
                    4f,
                    out error),
                Is.True,
                error);
            Assert.That(state.YieldedQuantity, Is.EqualTo(1));
            Assert.That(state.CycleProgress(4f), Is.EqualTo(0f));

            Assert.That(
                state.TryApply(
                    RealtimeGatheringState.CreateStopped(interaction, 9, runtime, "resource_node_depleted", "Depleted."),
                    Guid.Empty,
                    5f,
                    out error),
                Is.True,
                error);
            Assert.That(state.Status, Is.EqualTo(GatheringClientStatus.Stopped));
            Assert.That(state.UsedEquipmentSlot, Is.Empty);
            Assert.That(state.StopCode, Is.EqualTo("resource_node_depleted"));
            Assert.That(state.CycleProgress(5f), Is.EqualTo(0f));
            Assert.That(changes, Is.EqualTo(3));

            // A cycle that was in flight when gathering stopped still yields.
            Assert.That(
                state.TryApply(
                    RealtimeGatheringState.CreateYielded(interaction, 9, runtime, 3000, "fixture.ore", 1, 5),
                    Guid.Empty,
                    6f,
                    out error),
                Is.True,
                error);
            Assert.That(state.Status, Is.EqualTo(GatheringClientStatus.Stopped));
            Assert.That(state.YieldedQuantity, Is.EqualTo(2));

            state.Clear();
            Assert.That(state.Status, Is.EqualTo(GatheringClientStatus.Idle));
            Assert.That(state.YieldedQuantity, Is.EqualTo(0));
        }

        [Test]
        public void GatheringToolCheckIsAdvisoryUntilTheInventoryLoads()
        {
            var inventory = new InventoryClientState();
            Assert.That(WorldInteractionClientController.HasEquippedGatheringTool(inventory), Is.True);

            inventory.SetCatalog(catalog);
            var withoutTool = CreateSnapshot(0, null);
            inventory.PrepareCharacter(withoutTool.CharacterId);
            Assert.That(inventory.ApplyFull(withoutTool), Is.EqualTo(InventorySnapshotApplyResult.Applied));
            Assert.That(WorldInteractionClientController.HasEquippedGatheringTool(inventory), Is.False);

            var withTool = CreateSnapshot(0, null, tool: Item("fixture.pickaxe", 1));
            var equipped = new InventoryClientState();
            equipped.SetCatalog(catalog);
            equipped.PrepareCharacter(withTool.CharacterId);
            Assert.That(equipped.ApplyFull(withTool), Is.EqualTo(InventorySnapshotApplyResult.Applied));
            Assert.That(WorldInteractionClientController.HasEquippedGatheringTool(equipped), Is.True);
        }

        [Test]
        public void DepletedResourceNodeStaysVisibleButCannotBeTargeted()
        {
            var root = new GameObject("IronVein");
            try
            {
                var available = new GameObject(ResourceNodeView.AvailableModelName);
                available.transform.SetParent(root.transform, false);
                var depleted = new GameObject(ResourceNodeView.DepletedModelName);
                depleted.transform.SetParent(root.transform, false);
                var view = root.AddComponent<ResourceNodeView>();
                var state = new WorldActorClientState();
                var spawn = CreateNodeSpawn(true);
                Assert.That(state.TryApplySpawn(spawn, out var error), Is.True, error);
                Assert.That(state.TryGet(spawn.EntityId, out var actor), Is.True);
                view.Initialize(actor);

                Assert.That(view.IsTargetActive, Is.True);
                Assert.That(available.activeSelf, Is.True);
                Assert.That(depleted.activeSelf, Is.False);

                Assert.That(
                    state.TryApplyState(
                        new RealtimeWorldActorState(
                            spawn.EntityId,
                            2,
                            2,
                            false,
                            spawn.PositionX,
                            spawn.PositionY,
                            spawn.PositionZ,
                            spawn.YawDegrees,
                            RealtimeWorldActorActivityTier.EventDriven),
                        out error),
                    Is.True,
                    error);
                view.Apply(actor);

                Assert.That(root.activeSelf, Is.True);
                Assert.That(view.IsDepleted, Is.True);
                Assert.That(view.IsTargetActive, Is.False);
                Assert.That(available.activeSelf, Is.False);
                Assert.That(depleted.activeSelf, Is.True);
                Assert.That(root.GetComponentInChildren<BoxCollider>().enabled, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private bool Buy(
            CharacterInventorySnapshot snapshot,
            string definitionId,
            int quantity,
            out long price,
            out string reason)
        {
            return VendorClientRules.CanBuy(catalog, vendor, snapshot, definitionId, quantity, out price, out reason);
        }

        private bool Sell(
            CharacterInventorySnapshot snapshot,
            InventoryItem item,
            InventoryItemLocation location,
            int quantity,
            out long price,
            out string reason)
        {
            return VendorClientRules.CanSell(catalog, vendor, snapshot, item, location, quantity, out price, out reason);
        }

        private static InventoryItemLocation InventoryLocation(CharacterInventorySnapshot snapshot)
        {
            return new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                snapshot.PermanentInventory.ContainerId,
                ItemContainerTypeIds.PermanentInventory,
                0,
                string.Empty,
                Guid.Empty);
        }

        private CharacterInventorySnapshot CreateSnapshot(
            long currency,
            InventoryItem carried,
            long carriedWeight = 0,
            long carryCapacity = 200,
            InventoryItem tool = null)
        {
            return new CharacterInventorySnapshot(
                Guid.NewGuid(),
                catalog.Revision,
                1,
                Container(ItemContainerTypeIds.PermanentInventory, carried),
                new[] { new InventoryEquipmentSlot(ItemEquipmentSlotIds.Tool, 0, tool) },
                null,
                Container(ItemContainerTypeIds.Bank, null),
                new SecureContainerInventory(
                    "secure_container.base",
                    1,
                    Container(ItemContainerTypeIds.SecureContainer, null)),
                new RecoveryStorageInventory(Guid.NewGuid(), 1, Array.Empty<RecoveryDelivery>()),
                carriedWeight,
                carryCapacity,
                (int)(carriedWeight * 10_000 / carryCapacity),
                EncumbranceRules.CalculateMovementMultiplierBasisPoints(carriedWeight, carryCapacity),
                currency);
        }

        private static InventoryContainer Container(string type, InventoryItem item)
        {
            return new InventoryContainer(
                Guid.NewGuid(),
                type,
                1,
                1,
                new[] { new InventorySlot(0, "general", Array.Empty<string>(), item) });
        }

        private static InventoryItem Item(string definitionId, int quantity)
        {
            return new InventoryItem(Guid.NewGuid(), definitionId, quantity, 1, Array.Empty<InventoryPolicy>());
        }

        private static RealtimeWorldActorSpawn CreateNodeSpawn(bool isActive)
        {
            return new RealtimeWorldActorSpawn(
                11,
                Guid.NewGuid(),
                "resource.iron_vein",
                "local.iron_vein",
                "Iron Vein",
                RealtimeWorldActorKind.ResourceNode,
                "world",
                RealtimeWorldActorDisposition.Neutral,
                "resource.iron_vein",
                1,
                1,
                isActive,
                -8f,
                0f,
                1f,
                0f,
                RealtimeWorldActorActivityTier.EventDriven,
                0f,
                0.6f,
                0f,
                1.6f,
                1.2f,
                1.6f,
                "A seam of iron ore runs through the rock.",
                new[] { new RealtimeWorldActorMenuOption("resource.mine", RealtimeWorldActorCapabilityKind.Gathering, "Mine") });
        }
    }
}
