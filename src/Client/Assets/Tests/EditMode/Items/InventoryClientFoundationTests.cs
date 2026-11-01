using System;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items.Presentation;
using NUnit.Framework;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class InventoryClientFoundationTests
    {
        private ClientItemCatalog catalog;
        private ClientConfig config;
        private AsyncOperationHandle<ClientConfig> configHandle;

        [SetUp]
        public void SetUp()
        {
            ItemPresentationCatalogLoader.ResetCache();
            config = ClientConfig.Load(out configHandle);
            Assert.That(config.ItemGameplayCatalog, Is.Not.Null);
            catalog = FixtureItemCatalog.Load();
        }

        [TearDown]
        public void TearDown()
        {
            ItemPresentationCatalogLoader.ResetCache();
            ClientAssets.Release(configHandle);
            configHandle = default;
        }

        [Test]
        public void BundledCatalogLoadsGameplayAndPresentationOnceByDefinitionId()
        {
            Assert.That(
                ClientItemCatalog.TryLoad(config.ItemGameplayCatalog, out var bundled, out var error),
                Is.True,
                error);
            Assert.That(
                ClientItemCatalog.TryLoad(config.ItemGameplayCatalog, out var secondCatalog, out error),
                Is.True,
                error);

            var definition = bundled.Definitions.First();
            Assert.That(secondCatalog.Revision, Is.EqualTo(bundled.Revision));
            Assert.That(secondCatalog.Presentation, Is.SameAs(bundled.Presentation));
            Assert.That(bundled.GetDisplayName(definition.Id), Is.Not.Empty);
            Assert.That(bundled.GetLocalizationKey(definition.Id), Is.Not.Empty);
            Assert.That(bundled.LoadIcon(definition.Id), Is.SameAs(bundled.LoadIcon(definition.Id)));
        }

        // Every item has an icon to draw: its own, or the placeholder until approved art exists.
        [Test]
        public void EveryItemWithoutItsOwnIconShowsThePlaceholder()
        {
            Assert.That(
                ClientItemCatalog.TryLoad(config.ItemGameplayCatalog, out var bundled, out var error),
                Is.True,
                error);
            var placeholder = ItemPresentationCatalogLoader.LoadIcon(
                ItemPresentationCatalogLoader.PlaceholderIconAddress);
            Assert.That(placeholder, Is.Not.Null);

            foreach (var definition in bundled.Definitions)
            {
                Assert.That(bundled.Presentation.TryGet(definition.Id, out var entry), Is.True, definition.Id);
                var icon = bundled.LoadIcon(definition.Id);
                Assert.That(icon, Is.Not.Null, definition.Id);
                if (string.IsNullOrEmpty(entry.iconAddress))
                {
                    Assert.That(icon, Is.SameAs(placeholder), definition.Id);
                }
            }

            Assert.That(bundled.LoadIcon("unknown_definition"), Is.SameAs(placeholder));
        }

        [Test]
        public void MapperRejectsCatalogMismatchAndInvalidDeliveryRevision()
        {
            var response = CreateFullResponse(Guid.NewGuid(), 5);
            response.catalogRevision = "stale-revision";

            Assert.That(
                InventorySnapshotMapper.TryMap(
                    response,
                    catalog,
                    out _,
                    out var mismatchError),
                Is.False);
            Assert.That(mismatchError, Does.Contain("does not match"));

            response.catalogRevision = catalog.Revision;
            response.recoveryStorage.deliveries =
            new[]
            {
                new RecoveryDeliverySnapshotResponse
                {
                    deliveryId = Guid.NewGuid().ToString(),
                    revision = -1,
                    sourceKind = "test",
                    items = Array.Empty<RecoveryDeliveryItemSnapshotResponse>()
                }
            };

            Assert.That(
                InventorySnapshotMapper.TryMap(
                    response,
                    catalog,
                    out _,
                    out var deliveryError),
                Is.False);
            Assert.That(deliveryError, Does.Contain("invalid Recovery delivery"));

            response.recoveryStorage.deliveries =
                Array.Empty<RecoveryDeliverySnapshotResponse>();
            response.movementMultiplierBasisPoints = 9_999;
            Assert.That(
                InventorySnapshotMapper.TryMap(
                    response,
                    catalog,
                    out _,
                    out var carryError),
                Is.False);
            Assert.That(carryError, Does.Contain("inconsistent carry state"));

            var invalidRevisionResponse = CreateFullResponse(Guid.NewGuid(), -1);
            Assert.That(
                InventorySnapshotMapper.TryMap(
                    invalidRevisionResponse,
                    catalog,
                    out _,
                    out var revisionError),
                Is.False);
            Assert.That(revisionError, Does.Contain("invalid item-state revision"));

            var state = new InventoryClientState();
            state.SetCatalogError(
                "item_catalog_update_required",
                "This client build requires an item catalog update.",
                true);
            state.PrepareCharacter(Guid.NewGuid());
            Assert.That(state.Status, Is.EqualTo(InventoryClientStatus.UpdateRequired));
            Assert.That(state.Error.Code, Is.EqualTo("item_catalog_update_required"));
            state.ClearAll();
            Assert.That(state.Status, Is.EqualTo(InventoryClientStatus.UpdateRequired));
        }

        [Test]
        public void MapperCarriesTheBalanceAndTheRevisionCoversIt()
        {
            var characterId = Guid.NewGuid();
            var response = CreateFullResponse(characterId, 3);
            response.currency = 125;
            Assert.That(
                InventorySnapshotMapper.TryMap(response, catalog, out var snapshot, out var error),
                Is.True,
                error);
            Assert.That(snapshot.Currency, Is.EqualTo(125));

            var state = new InventoryClientState();
            state.SetCatalog(catalog);
            state.PrepareCharacter(characterId);
            Assert.That(state.ApplyFull(snapshot), Is.EqualTo(InventorySnapshotApplyResult.Applied));

            // Every online balance change is an item transaction with a new revision.
            response.currency = 126;
            Assert.That(
                InventorySnapshotMapper.TryMap(response, catalog, out var changed, out error),
                Is.True,
                error);
            Assert.That(state.ApplyFull(changed), Is.EqualTo(InventorySnapshotApplyResult.Diverged));

            response.currency = -1;
            Assert.That(InventorySnapshotMapper.TryMap(response, catalog, out _, out error), Is.False);
            Assert.That(error, Does.Contain("currency"));
            response.currency = Mmorpg.WorldData.Items.VendorPricing.MaximumCurrencyBalance + 1;
            Assert.That(InventorySnapshotMapper.TryMap(response, catalog, out _, out error), Is.False);
        }

        [Test]
        public void JsonRoundTripMapsEmptySlotsWithoutPhantomItems()
        {
            var response = CreateFullResponse(Guid.NewGuid(), 0);
            var json = JsonUtility.ToJson(response);
            var roundTripped = JsonUtility.FromJson<CharacterInventorySnapshotResponse>(json);

            Assert.That(
                InventorySnapshotMapper.TryMap(
                    roundTripped,
                    catalog,
                    out var snapshot,
                    out var error),
                Is.True,
                error);
            Assert.That(snapshot.PermanentInventory.Slots[0].Item, Is.Null);
            Assert.That(snapshot.EquippedBag, Is.Null);
        }

        [Test]
        public void MapperAcceptsCanonicalZeroRevisionItemInstances()
        {
            var response = CreateFullResponse(Guid.NewGuid(), 0);
            response.bank.slots[0].item = new ItemInstanceSnapshotResponse
            {
                itemInstanceId = Guid.NewGuid().ToString(),
                definitionId = "fixture.ore",
                quantity = 1,
                revision = 0,
                policies = Array.Empty<ItemPolicySummaryResponse>()
            };

            Assert.That(
                InventorySnapshotMapper.TryMap(
                    response,
                    catalog,
                    out var snapshot,
                    out var error),
                Is.True,
                error);
            Assert.That(snapshot.Bank.Slots[0].Item.Revision, Is.Zero);
        }

        [Test]
        public void MapperPreservesSafeInsuranceProtectionSource()
        {
            var response = CreateFullResponse(Guid.NewGuid(), 4);
            response.bank.slots[0].item = new ItemInstanceSnapshotResponse
            {
                itemInstanceId = Guid.NewGuid().ToString(),
                definitionId = "fixture.ore",
                quantity = 1,
                revision = 2,
                policies = new[]
                {
                    new ItemPolicySummaryResponse
                    {
                        policyKind = "insured",
                        status = "active",
                        protectionSource = "insurance_npc"
                    }
                }
            };

            Assert.That(
                InventorySnapshotMapper.TryMap(
                    response,
                    catalog,
                    out var snapshot,
                    out var error),
                Is.True,
                error);
            Assert.That(snapshot.Bank.Slots[0].Item.Policies.Count, Is.EqualTo(1));
            var policy = snapshot.Bank.Slots[0].Item.Policies[0];
            Assert.That(policy.Kind, Is.EqualTo("insured"));
            Assert.That(policy.Status, Is.EqualTo("active"));
            Assert.That(policy.ProtectionSource, Is.EqualTo("insurance_npc"));
        }

        [Test]
        public void RequiredRecoveryItemsRejectJsonNullPlaceholders()
        {
            var response = CreateFullResponse(Guid.NewGuid(), 0);
            response.recoveryStorage.deliveries = new[]
            {
                new RecoveryDeliverySnapshotResponse
                {
                    deliveryId = Guid.NewGuid().ToString(),
                    revision = 0,
                    sourceKind = "test",
                    items = new[]
                    {
                        new RecoveryDeliveryItemSnapshotResponse
                        {
                            itemOrder = 0,
                            containerSlotIndex = 0,
                            item = new ItemInstanceSnapshotResponse()
                        }
                    }
                }
            };

            Assert.That(
                InventorySnapshotMapper.TryMap(
                    response,
                    catalog,
                    out _,
                    out var error),
                Is.False);
            Assert.That(error, Does.Contain("invalid item"));
        }

        [Test]
        public void ClientStateRejectsStaleAndDivergentSnapshots()
        {
            var characterId = Guid.NewGuid();
            var state = new InventoryClientState();
            state.SetCatalog(catalog);
            state.PrepareCharacter(characterId);
            Assert.That(TryMap(CreateFullResponse(characterId, 5), out var initial), Is.True);

            Assert.That(state.ApplyFull(initial), Is.EqualTo(InventorySnapshotApplyResult.Applied));
            Assert.That(state.HasCoherentFullSnapshot, Is.True);
            Assert.That(state.CanMutate, Is.True);

            var sameRevisionBankConflict = new CharacterBankSnapshot(
                characterId,
                catalog.Revision,
                5,
                new InventoryContainer(
                    Guid.NewGuid(),
                    "bank",
                    5,
                    40,
                    new[] { new InventorySlot(0, "general", Array.Empty<string>(), null) }));
            Assert.That(
                state.ApplyBank(sameRevisionBankConflict),
                Is.EqualTo(InventorySnapshotApplyResult.Diverged));

            var focusedBank = new CharacterBankSnapshot(
                characterId,
                catalog.Revision,
                6,
                CreateContainer("bank", 6, 40));
            Assert.That(
                state.ApplyBank(focusedBank),
                Is.EqualTo(InventorySnapshotApplyResult.Applied));
            Assert.That(state.KnownItemStateRevision, Is.EqualTo(6));
            Assert.That(state.RequiresFullRefresh, Is.True);
            Assert.That(state.CanMutate, Is.False);

            Assert.That(state.ApplyFull(initial), Is.EqualTo(InventorySnapshotApplyResult.Stale));
            Assert.That(TryMap(CreateFullResponse(characterId, 6), out var refreshed), Is.True);
            Assert.That(state.ApplyFull(refreshed), Is.EqualTo(InventorySnapshotApplyResult.Applied));
            Assert.That(state.HasCoherentFullSnapshot, Is.True);

            var divergentResponse = CreateFullResponse(characterId, 6);
            divergentResponse.carriedWeight = 1;
            divergentResponse.loadRatioBasisPoints = 50;
            divergentResponse.movementMultiplierBasisPoints = 10_000;
            Assert.That(TryMap(divergentResponse, out var divergent), Is.True);
            Assert.That(
                state.ApplyFull(divergent),
                Is.EqualTo(InventorySnapshotApplyResult.Diverged));
            Assert.That(state.FullSnapshot.CarriedWeight, Is.Zero);
        }

        [Test]
        public void CoherentSnapshotMustReachCommittedCorpseRevisionBeforeRefreshCanStop()
        {
            var characterId = Guid.NewGuid();
            var state = new InventoryClientState();
            state.SetCatalog(catalog);
            state.PrepareCharacter(characterId);
            Assert.That(TryMap(CreateFullResponse(characterId, 5), out var snapshot), Is.True);
            Assert.That(state.ApplyFull(snapshot), Is.EqualTo(InventorySnapshotApplyResult.Applied));

            Assert.That(state.HasCoherentFullSnapshotAtLeast(5), Is.True);
            Assert.That(state.HasCoherentFullSnapshotAtLeast(6), Is.False);
        }

        [Test]
        public void TargetAdvisorUsesCanonicalSlotsSecureEligibilityBagRulesAndHardCap()
        {
            var state = CreateAdvisorState(132, 250);
            var potion = CreateItem("fixture.potion", 1);
            var weapon = CreateItem("fixture.sword", 1);
            var external = new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                state.Bank.ContainerId,
                "bank",
                0,
                string.Empty,
                Guid.Empty);
            var bagPotionSlot = state.FullSnapshot.EquippedBag.Contents.Slots[0];

            Assert.That(
                InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    potion,
                    external,
                    state.FullSnapshot.EquippedBag.Contents,
                    bagPotionSlot,
                    out var potionReason),
                Is.True,
                potionReason);
            Assert.That(
                InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    weapon,
                    external,
                    state.FullSnapshot.EquippedBag.Contents,
                    bagPotionSlot,
                    out _),
                Is.False);
            Assert.That(
                InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    weapon,
                    external,
                    state.FullSnapshot.SecureContainer.Contents,
                    state.FullSnapshot.SecureContainer.Contents.Slots[0],
                    out _),
                Is.False);

            var equippedBag = state.FullSnapshot.EquippedBag.Item;
            var equippedLocation = new InventoryItemLocation(
                InventoryItemLocationKind.Equipment,
                Guid.Empty,
                "equipment",
                -1,
                "bag",
                Guid.Empty);
            Assert.That(
                InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    equippedBag,
                    equippedLocation,
                    state.FullSnapshot.PermanentInventory,
                    state.FullSnapshot.PermanentInventory.Slots[0],
                    out var bagReason),
                Is.False);
            Assert.That(bagReason, Does.Contain("non-empty Bag"));

            var hardCapState = CreateAdvisorState(350, 250);
            var ammunition = CreateItem("fixture.arrow", 1);
            Assert.That(
                InventoryTargetAdvisor.CanPlaceInContainer(
                    hardCapState,
                    ammunition,
                    external,
                    hardCapState.FullSnapshot.PermanentInventory,
                    hardCapState.FullSnapshot.PermanentInventory.Slots[0],
                    out var hardCapReason),
                Is.False);
            Assert.That(hardCapReason, Does.Contain("140 percent"));

            var carriedAmmunition = CreateItem("fixture.arrow", 1);
            Assert.That(
                InventoryTargetAdvisor.CanMerge(
                    hardCapState,
                    ammunition,
                    external,
                    carriedAmmunition,
                    new InventoryItemLocation(
                        InventoryItemLocationKind.Container,
                        hardCapState.FullSnapshot.PermanentInventory.ContainerId,
                        "permanent_inventory",
                        0,
                        string.Empty,
                        Guid.Empty),
                    out var mergeCapReason),
                Is.False);
            Assert.That(mergeCapReason, Does.Contain("140 percent"));

            var recoveryDelivery = new RecoveryDelivery(
                Guid.NewGuid(),
                0,
                "test",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new[]
                {
                    new RecoveryDeliveryItem(0, 0, ammunition)
                });
            Assert.That(
                InventoryTargetAdvisor.CanClaimRecovery(
                    hardCapState,
                    recoveryDelivery,
                    hardCapState.FullSnapshot.PermanentInventory,
                    out var recoveryCapReason),
                Is.False);
            Assert.That(recoveryCapReason, Does.Contain("140 percent"));
            Assert.That(
                InventoryTargetAdvisor.CanClaimRecovery(
                    hardCapState,
                    recoveryDelivery,
                    hardCapState.Bank,
                    out _),
                Is.True);

            var partialStack = CreateItem("fixture.ore", 3);
            var partialState = CreateAdvisorState(338, 250);
            Assert.That(
                InventoryTargetAdvisor.CanPlaceInContainer(
                    partialState,
                    partialStack,
                    external,
                    partialState.FullSnapshot.PermanentInventory,
                    partialState.FullSnapshot.PermanentInventory.Slots[0],
                    2,
                    out _),
                Is.True);
            Assert.That(
                InventoryTargetAdvisor.CanPlaceInContainer(
                    partialState,
                    partialStack,
                    external,
                    partialState.FullSnapshot.PermanentInventory,
                    partialState.FullSnapshot.PermanentInventory.Slots[0],
                    3,
                    out _),
                Is.False);

            var noBagState = CreateAdvisorStateWithoutBag(275, 200);
            var externalBag = CreateItem("fixture.pack", 1);
            var bagEquipmentSlot = new InventoryEquipmentSlot("bag", 0, null);
            Assert.That(
                InventoryTargetAdvisor.CanEquip(
                    noBagState,
                    externalBag,
                    new InventoryItemLocation(
                        InventoryItemLocationKind.Container,
                        noBagState.Bank.ContainerId,
                        "bank",
                        0,
                        string.Empty,
                        Guid.Empty),
                    bagEquipmentSlot,
                    out var equipBagReason),
                Is.True,
                equipBagReason);

            var emptyBagState = CreateAdvisorState(275, 250, false);
            Assert.That(
                InventoryTargetAdvisor.CanUnequip(
                    emptyBagState,
                    emptyBagState.FullSnapshot.EquippedBag.Item,
                    emptyBagState.Bank,
                    emptyBagState.Bank.Slots[0],
                    out var bankUnequipReason),
                Is.True,
                bankUnequipReason);
            Assert.That(
                InventoryTargetAdvisor.CanUnequip(
                    emptyBagState,
                    emptyBagState.FullSnapshot.EquippedBag.Item,
                    emptyBagState.FullSnapshot.PermanentInventory,
                    emptyBagState.FullSnapshot.PermanentInventory.Slots[0],
                    out _),
                Is.False);

            var equippedWeapon = CreateItem("fixture.sword", 1);
            var overloadedUnequipState = CreateAdvisorStateWithoutBag(270, 200);
            Assert.That(
                InventoryTargetAdvisor.CanUnequip(
                    overloadedUnequipState,
                    equippedWeapon,
                    overloadedUnequipState.Bank,
                    overloadedUnequipState.Bank.Slots[0],
                    out var externalUnequipReason),
                Is.True,
                externalUnequipReason);
            Assert.That(
                InventoryTargetAdvisor.CanUnequip(
                    overloadedUnequipState,
                    equippedWeapon,
                    overloadedUnequipState.FullSnapshot.PermanentInventory,
                    overloadedUnequipState.FullSnapshot.PermanentInventory.Slots[0],
                    out var carriedUnequipReason),
                Is.False);
            Assert.That(carriedUnequipReason, Does.Contain("140 percent"));

            var bankLocation = new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                hardCapState.Bank.ContainerId,
                "bank",
                0,
                string.Empty,
                Guid.Empty);
            var permanentLocation = new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                hardCapState.FullSnapshot.PermanentInventory.ContainerId,
                "permanent_inventory",
                0,
                string.Empty,
                Guid.Empty);
            Assert.That(
                InventoryTargetAdvisor.CanSwap(
                    hardCapState,
                    CreateItem("fixture.arrow", 1),
                    bankLocation,
                    CreateItem("fixture.sword", 1),
                    permanentLocation,
                    out var reducingSwapReason),
                Is.True,
                reducingSwapReason);

            var swapHardCapState = CreateAdvisorState(338, 250);
            Assert.That(
                InventoryTargetAdvisor.CanSwap(
                    swapHardCapState,
                    CreateItem("fixture.sword", 1),
                    new InventoryItemLocation(
                        InventoryItemLocationKind.Container,
                        swapHardCapState.Bank.ContainerId,
                        "bank",
                        0,
                        string.Empty,
                        Guid.Empty),
                    CreateItem("fixture.arrow", 1),
                    new InventoryItemLocation(
                        InventoryItemLocationKind.Container,
                        swapHardCapState.FullSnapshot.PermanentInventory.ContainerId,
                        "permanent_inventory",
                        0,
                        string.Empty,
                        Guid.Empty),
                    out var swapHardCapReason),
                Is.False);
            Assert.That(swapHardCapReason, Does.Contain("140 percent"));

            var potionSlotLocation = new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                state.FullSnapshot.EquippedBag.Contents.ContainerId,
                "bag_contents",
                bagPotionSlot.SlotIndex,
                string.Empty,
                Guid.Empty);
            Assert.That(
                InventoryTargetAdvisor.CanSwap(
                    state,
                    CreateItem("fixture.sword", 1),
                    new InventoryItemLocation(
                        InventoryItemLocationKind.Container,
                        state.FullSnapshot.PermanentInventory.ContainerId,
                        "permanent_inventory",
                        0,
                        string.Empty,
                        Guid.Empty),
                    CreateItem("fixture.potion", 1),
                    potionSlotLocation,
                    out var specializedSwapReason),
                Is.False);
            Assert.That(specializedSwapReason, Does.Contain("accepted tags"));

            var corpseContainer = new InventoryContainer(
                Guid.NewGuid(),
                "corpse_inventory",
                2,
                20,
                new[]
                {
                    new InventorySlot(0, "general", Array.Empty<string>(), null)
                });
            Assert.That(
                InventoryTargetAdvisor.CanSwapWithExternalContainer(
                    swapHardCapState,
                    CreateItem("fixture.arrow", 1),
                    new InventoryItemLocation(
                        InventoryItemLocationKind.Container,
                        swapHardCapState.FullSnapshot.PermanentInventory.ContainerId,
                        "permanent_inventory",
                        0,
                        string.Empty,
                        Guid.Empty),
                    CreateItem("fixture.sword", 1),
                    corpseContainer,
                    corpseContainer.Slots[0],
                    out var externalSwapCapReason),
                Is.False);
            Assert.That(externalSwapCapReason, Does.Contain("140 percent"));
            Assert.That(
                InventoryTargetAdvisor.CanSwapWithExternalContainer(
                    hardCapState,
                    CreateItem("fixture.sword", 1),
                    permanentLocation,
                    CreateItem("fixture.arrow", 1),
                    corpseContainer,
                    corpseContainer.Slots[0],
                    out var externalReducingReason),
                Is.True,
                externalReducingReason);
        }

        private bool TryMap(
            CharacterInventorySnapshotResponse response,
            out CharacterInventorySnapshot snapshot)
        {
            return InventorySnapshotMapper.TryMap(
                response,
                catalog,
                out snapshot,
                out _);
        }

        private CharacterInventorySnapshotResponse CreateFullResponse(
            Guid characterId,
            long revision)
        {
            return new CharacterInventorySnapshotResponse
            {
                characterId = characterId.ToString(),
                catalogRevision = catalog.Revision,
                itemStateRevision = revision,
                permanentInventory = CreateContainerResponse(
                    "permanent_inventory",
                    revision,
                    20),
                equipment = Array.Empty<EquipmentSlotSnapshotResponse>(),
                bank = CreateContainerResponse("bank", revision, 40),
                secureContainer = new SecureContainerSnapshotResponse
                {
                    tierId = "secure_container.base",
                    entitlementRevision = revision,
                    contents = CreateContainerResponse("secure_container", revision, 4)
                },
                recoveryStorage = new RecoveryStorageSnapshotResponse
                {
                    containerId = Guid.NewGuid().ToString(),
                    revision = revision,
                    deliveries = Array.Empty<RecoveryDeliverySnapshotResponse>()
                },
                carriedWeight = 0,
                carryCapacity = 200,
                loadRatioBasisPoints = 0,
                movementMultiplierBasisPoints = 10_000
            };
        }

        private static ItemContainerSnapshotResponse CreateContainerResponse(
            string type,
            long revision,
            int capacity)
        {
            var slots = new ItemSlotSnapshotResponse[capacity];
            for (var index = 0; index < slots.Length; index++)
            {
                slots[index] = new ItemSlotSnapshotResponse
                {
                    slotIndex = index,
                    slotKind = "general",
                    acceptedTags = Array.Empty<string>()
                };
            }

            return new ItemContainerSnapshotResponse
            {
                containerId = Guid.NewGuid().ToString(),
                containerType = type,
                revision = revision,
                slotCapacity = capacity,
                slots = slots
            };
        }

        [Test]
        public void TargetAdvisorAllowsShrinkingAnInvoluntaryDeathOverflow()
        {
            // Death left 300 weight against capacity 200, above the 280 hard cap.
            var overflowState = CreateAdvisorStateWithoutBag(300, 200);
            var external = new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                overflowState.Bank.ContainerId,
                "bank",
                0,
                string.Empty,
                Guid.Empty);
            var permanentLocation = new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                overflowState.FullSnapshot.PermanentInventory.ContainerId,
                "permanent_inventory",
                0,
                string.Empty,
                Guid.Empty);

            Assert.That(
                InventoryTargetAdvisor.CanPlaceInContainer(
                    overflowState,
                    CreateItem("fixture.arrow", 1),
                    external,
                    overflowState.FullSnapshot.PermanentInventory,
                    overflowState.FullSnapshot.PermanentInventory.Slots[0],
                    out var addReason),
                Is.False);
            Assert.That(addReason, Does.Contain("140 percent"));

            Assert.That(
                InventoryTargetAdvisor.CanSwap(
                    overflowState,
                    CreateItem("fixture.arrow", 1),
                    external,
                    CreateItem("fixture.sword", 1),
                    permanentLocation,
                    out var shrinkReason),
                Is.True,
                shrinkReason);
        }

        private InventoryClientState CreateAdvisorState(
            long carriedWeight,
            long carryCapacity,
            bool bagHasContents = true)
        {
            var characterId = Guid.NewGuid();
            var equippedBagItem = CreateItem("fixture.pack", 1);
            var bagContents = new InventoryContainer(
                Guid.NewGuid(),
                "bag_contents",
                1,
                7,
                new[]
                {
                    new InventorySlot(4, "specialized", new[] { "potion" }, null),
                    new InventorySlot(
                        5,
                        "specialized",
                        new[] { "material" },
                        bagHasContents ? CreateItem("fixture.ore", 1) : null),
                    new InventorySlot(6, "specialized", new[] { "ammunition" }, null)
                });
            var snapshot = new CharacterInventorySnapshot(
                characterId,
                catalog.Revision,
                1,
                CreateContainer("permanent_inventory", 1, 20),
                Array.Empty<InventoryEquipmentSlot>(),
                new EquippedBagInventory(equippedBagItem, bagContents),
                CreateContainer("bank", 1, 40),
                new SecureContainerInventory(
                    "secure_container.base",
                    1,
                    CreateContainer("secure_container", 1, 4)),
                new RecoveryStorageInventory(
                    Guid.NewGuid(),
                    1,
                    Array.Empty<RecoveryDelivery>()),
                carriedWeight,
                carryCapacity,
                (int)(carriedWeight * 10_000 / carryCapacity),
                10_000);
            var state = new InventoryClientState();
            state.SetCatalog(catalog);
            state.PrepareCharacter(characterId);
            Assert.That(state.ApplyFull(snapshot), Is.EqualTo(InventorySnapshotApplyResult.Applied));
            return state;
        }

        [Test]
        public void EquipmentAdvisorAppliesStackLimitsBlockedSlotsAndEquippedWeight()
        {
            var bow = CreateItem("fixture.bow", 1);
            var equippedArrows = CreateItem("fixture.arrow", 30);
            var state = CreateAdvisorStateWithEquipment(
                100,
                200,
                new InventoryEquipmentSlot("main_hand", 2, bow),
                new InventoryEquipmentSlot("off_hand", 3, null),
                new InventoryEquipmentSlot("ammunition", 4, equippedArrows));
            var carried = new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                state.FullSnapshot.PermanentInventory.ContainerId,
                "permanent_inventory",
                0,
                string.Empty,
                Guid.Empty);
            var ammunitionLocation = new InventoryItemLocation(
                InventoryItemLocationKind.Equipment,
                Guid.Empty,
                "equipment",
                -1,
                "ammunition",
                Guid.Empty);

            Assert.That(
                InventoryTargetAdvisor.CanEquip(
                    state,
                    CreateItem("fixture.shield", 1),
                    carried,
                    state.FullSnapshot.Equipment[1],
                    out var blockedReason),
                Is.False);
            Assert.That(blockedReason, Does.Contain("Main Hand"));
            Assert.That(
                InventoryTargetAdvisor.CanMerge(
                    state,
                    CreateItem("fixture.arrow", 20),
                    carried,
                    equippedArrows,
                    ammunitionLocation,
                    out var topUpReason),
                Is.True,
                topUpReason);
            Assert.That(
                InventoryTargetAdvisor.CanMerge(
                    state,
                    CreateItem("fixture.arrow", 21),
                    carried,
                    equippedArrows,
                    ammunitionLocation,
                    out var limitReason),
                Is.False);
            Assert.That(limitReason, Does.Contain("equipment slot limit"));

            var emptySlotState = CreateAdvisorStateWithEquipment(
                100,
                200,
                new InventoryEquipmentSlot("ammunition", 4, null));
            Assert.That(
                InventoryTargetAdvisor.CanEquip(
                    emptySlotState,
                    CreateItem("fixture.arrow", 55),
                    carried,
                    emptySlotState.FullSnapshot.Equipment[0],
                    out _),
                Is.False);
            Assert.That(
                InventoryTargetAdvisor.CanEquip(
                    emptySlotState,
                    CreateItem("fixture.arrow", 50),
                    carried,
                    emptySlotState.FullSnapshot.Equipment[0],
                    out var fitReason),
                Is.True,
                fitReason);

            var loadedState = CreateAdvisorStateWithEquipment(
                270,
                200,
                new InventoryEquipmentSlot("ammunition", 4, equippedArrows));
            var inventory = loadedState.FullSnapshot.PermanentInventory;
            Assert.That(
                InventoryTargetAdvisor.CanPlaceInContainer(
                    loadedState,
                    equippedArrows,
                    ammunitionLocation,
                    inventory,
                    inventory.Slots[0],
                    10,
                    out var splitReason),
                Is.True,
                splitReason);
            Assert.That(
                InventoryTargetAdvisor.CanPlaceInContainer(
                    loadedState,
                    equippedArrows,
                    ammunitionLocation,
                    inventory,
                    inventory.Slots[0],
                    11,
                    out var capReason),
                Is.False);
            Assert.That(capReason, Does.Contain("140 percent"));
        }

        [Test]
        public void EquippingOntoAnOccupiedSlotSwapsWhenTheSourceSlotAcceptsTheEquippedItem()
        {
            var bow = CreateItem("fixture.bow", 1);
            var state = CreateAdvisorStateWithEquipment(
                100,
                200,
                new InventoryEquipmentSlot("main_hand", 2, bow),
                new InventoryEquipmentSlot("off_hand", 3, null));
            var mainHand = state.FullSnapshot.Equipment[0];

            Assert.That(
                InventoryTargetAdvisor.CanEquip(
                    state,
                    CreateItem("fixture.sword", 1),
                    ContainerLocation(state.FullSnapshot.PermanentInventory),
                    mainHand,
                    out var swapReason),
                Is.True,
                swapReason);

            // The bow may not enter the Secure Container, so it cannot swap into a slot there.
            Assert.That(
                InventoryTargetAdvisor.CanEquip(
                    state,
                    CreateItem("fixture.sword", 1),
                    ContainerLocation(state.FullSnapshot.SecureContainer.Contents),
                    mainHand,
                    out var secureReason),
                Is.False);
            Assert.That(secureReason, Does.Contain("Secure Container"));

            Assert.That(
                InventoryTargetAdvisor.CanEquip(
                    state,
                    bow,
                    ContainerLocation(state.FullSnapshot.PermanentInventory),
                    mainHand,
                    out var selfReason),
                Is.False);
            Assert.That(selfReason, Does.Contain("already equipped"));

            // The swapped-out sword frees the main hand, but the new bow still needs the off hand.
            var shieldState = CreateAdvisorStateWithEquipment(
                100,
                200,
                new InventoryEquipmentSlot("main_hand", 2, CreateItem("fixture.sword", 1)),
                new InventoryEquipmentSlot("off_hand", 3, CreateItem("fixture.shield", 1)));
            Assert.That(
                InventoryTargetAdvisor.CanEquip(
                    shieldState,
                    CreateItem("fixture.bow", 1),
                    ContainerLocation(shieldState.FullSnapshot.PermanentInventory),
                    shieldState.FullSnapshot.Equipment[0],
                    out var blockedReason),
                Is.False);
            Assert.That(blockedReason, Does.Contain("Off Hand"));
        }

        private static InventoryItemLocation ContainerLocation(InventoryContainer container)
        {
            return new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                container.ContainerId,
                container.ContainerType,
                0,
                string.Empty,
                Guid.Empty);
        }

        private InventoryClientState CreateAdvisorStateWithEquipment(
            long carriedWeight,
            long carryCapacity,
            params InventoryEquipmentSlot[] equipment)
        {
            var characterId = Guid.NewGuid();
            var snapshot = new CharacterInventorySnapshot(
                characterId,
                catalog.Revision,
                1,
                CreateContainer("permanent_inventory", 1, 20),
                equipment,
                null,
                CreateContainer("bank", 1, 40),
                new SecureContainerInventory(
                    "secure_container.base",
                    1,
                    CreateContainer("secure_container", 1, 4)),
                new RecoveryStorageInventory(
                    Guid.NewGuid(),
                    1,
                    Array.Empty<RecoveryDelivery>()),
                carriedWeight,
                carryCapacity,
                (int)(carriedWeight * 10_000 / carryCapacity),
                10_000);
            var state = new InventoryClientState();
            state.SetCatalog(catalog);
            state.PrepareCharacter(characterId);
            Assert.That(state.ApplyFull(snapshot), Is.EqualTo(InventorySnapshotApplyResult.Applied));
            return state;
        }

        private InventoryClientState CreateAdvisorStateWithoutBag(
            long carriedWeight,
            long carryCapacity)
        {
            var characterId = Guid.NewGuid();
            var snapshot = new CharacterInventorySnapshot(
                characterId,
                catalog.Revision,
                1,
                CreateContainer("permanent_inventory", 1, 20),
                Array.Empty<InventoryEquipmentSlot>(),
                null,
                CreateContainer("bank", 1, 40),
                new SecureContainerInventory(
                    "secure_container.base",
                    1,
                    CreateContainer("secure_container", 1, 4)),
                new RecoveryStorageInventory(
                    Guid.NewGuid(),
                    1,
                    Array.Empty<RecoveryDelivery>()),
                carriedWeight,
                carryCapacity,
                (int)(carriedWeight * 10_000 / carryCapacity),
                10_000);
            var state = new InventoryClientState();
            state.SetCatalog(catalog);
            state.PrepareCharacter(characterId);
            Assert.That(state.ApplyFull(snapshot), Is.EqualTo(InventorySnapshotApplyResult.Applied));
            return state;
        }

        private static InventoryContainer CreateContainer(
            string type,
            long revision,
            int capacity)
        {
            return new InventoryContainer(
                Guid.NewGuid(),
                type,
                revision,
                capacity,
                new[] { new InventorySlot(0, "general", Array.Empty<string>(), null) });
        }

        private static InventoryItem CreateItem(string definitionId, int quantity)
        {
            return new InventoryItem(
                Guid.NewGuid(),
                definitionId,
                quantity,
                1,
                Array.Empty<InventoryPolicy>());
        }
    }
}
