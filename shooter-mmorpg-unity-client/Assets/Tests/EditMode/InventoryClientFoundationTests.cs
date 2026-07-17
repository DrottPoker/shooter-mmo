using System;
using NUnit.Framework;
using ShooterMmo.Api;
using ShooterMmo.Config;
using ShooterMmo.GameProtocol;
using ShooterMmo.Items;
using ShooterMmo.WorldData.Items.Presentation;
using UnityEngine;

namespace ShooterMmo.Tests.EditMode
{
    public sealed class InventoryClientFoundationTests
    {
        private ClientItemCatalog catalog;

        [SetUp]
        public void SetUp()
        {
            ItemPresentationCatalogLoader.ResetCache();
            var config = ShooterMmoClientConfig.Load();
            Assert.That(config.ItemGameplayCatalog, Is.Not.Null);
            Assert.That(
                ClientItemCatalog.TryLoad(
                    config.ItemGameplayCatalog,
                    out catalog,
                    out var error),
                Is.True,
                error);
        }

        [TearDown]
        public void TearDown()
        {
            ItemPresentationCatalogLoader.ResetCache();
        }

        [Test]
        public void CatalogLoadsGameplayAndPresentationOnceByDefinitionId()
        {
            var config = ShooterMmoClientConfig.Load();

            Assert.That(
                ClientItemCatalog.TryLoad(
                    config.ItemGameplayCatalog,
                    out var secondCatalog,
                    out var error),
                Is.True,
                error);

            Assert.That(secondCatalog.Revision, Is.EqualTo(catalog.Revision));
            Assert.That(secondCatalog.Presentation, Is.SameAs(catalog.Presentation));
            Assert.That(catalog.GetDisplayName("bag.field_pack"), Is.EqualTo("Field Pack"));
            Assert.That(catalog.GetLocalizationKey("bag.field_pack"), Is.Not.Empty);
            Assert.That(catalog.GetPrefabPresentationKey("bag.field_pack"), Is.EqualTo(string.Empty));
            Assert.That(catalog.LoadIcon("bag.field_pack"), Is.SameAs(
                catalog.LoadIcon("bag.field_pack")));
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
                definitionId = "material.iron_ore",
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
            divergentResponse.sprintEligible = true;
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
        public void OperationJournalAllowsOneCorrelatableOperationAtATime()
        {
            var operationId = Guid.NewGuid();
            var intent = RealtimeItemOperationIntent.CreateDestroy(
                operationId,
                4,
                Guid.NewGuid(),
                2);
            var journal = new InventoryOperationJournal();

            Assert.That(
                journal.TryBegin(intent, InventoryRefreshScope.Full),
                Is.True);
            Assert.That(
                journal.TryBegin(
                    RealtimeItemOperationIntent.CreateDestroy(
                        Guid.NewGuid(),
                        4,
                        Guid.NewGuid(),
                        1),
                    InventoryRefreshScope.Full),
                Is.False);

            var result = new RealtimeItemOperationResult(
                operationId,
                RealtimeItemOperationKind.Destroy,
                true,
                true,
                null,
                new RealtimeCarryState(5, 0, 200),
                Array.Empty<RealtimeItemRevision>(),
                Array.Empty<RealtimeContainerRevision>(),
                Array.Empty<Guid>());
            Assert.That(journal.Matches(result), Is.True);
            journal.MarkAwaitingRefresh();
            Assert.That(journal.Pending.AwaitingRefresh, Is.True);
            journal.Complete();
            Assert.That(journal.Pending, Is.Null);
            Assert.That(journal.IsDuplicateCompletion(result), Is.True);
            journal.Reset();
            Assert.That(journal.IsDuplicateCompletion(result), Is.False);
        }

        [Test]
        public void TargetAdvisorUsesCanonicalSlotsSecureEligibilityBagRulesAndHardCap()
        {
            var state = CreateAdvisorState(132, 250);
            var medical = CreateItem("medical.field_dressing", 1);
            var weapon = CreateItem("weapon.training_rifle", 1);
            var external = new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                state.Bank.ContainerId,
                "bank",
                0,
                string.Empty,
                Guid.Empty);
            var bagMedicalSlot = state.FullSnapshot.EquippedBag.Contents.Slots[0];

            Assert.That(
                InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    medical,
                    external,
                    state.FullSnapshot.EquippedBag.Contents,
                    bagMedicalSlot,
                    out var medicalReason),
                Is.True,
                medicalReason);
            Assert.That(
                InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    weapon,
                    external,
                    state.FullSnapshot.EquippedBag.Contents,
                    bagMedicalSlot,
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
            var ammunition = CreateItem("ammunition.training_556", 1);
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

            var carriedAmmunition = CreateItem("ammunition.training_556", 1);
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

            var partialStack = CreateItem("material.iron_ore", 3);
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
            var externalBag = CreateItem("bag.field_pack", 1);
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

            var equippedWeapon = CreateItem("weapon.training_rifle", 1);
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
                    CreateItem("ammunition.training_556", 1),
                    bankLocation,
                    CreateItem("weapon.training_rifle", 1),
                    permanentLocation,
                    out var reducingSwapReason),
                Is.True,
                reducingSwapReason);

            var swapHardCapState = CreateAdvisorState(338, 250);
            Assert.That(
                InventoryTargetAdvisor.CanSwap(
                    swapHardCapState,
                    CreateItem("weapon.training_rifle", 1),
                    new InventoryItemLocation(
                        InventoryItemLocationKind.Container,
                        swapHardCapState.Bank.ContainerId,
                        "bank",
                        0,
                        string.Empty,
                        Guid.Empty),
                    CreateItem("ammunition.training_556", 1),
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

            var medicalSlotLocation = new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                state.FullSnapshot.EquippedBag.Contents.ContainerId,
                "bag_contents",
                bagMedicalSlot.SlotIndex,
                string.Empty,
                Guid.Empty);
            Assert.That(
                InventoryTargetAdvisor.CanSwap(
                    state,
                    CreateItem("weapon.training_rifle", 1),
                    new InventoryItemLocation(
                        InventoryItemLocationKind.Container,
                        state.FullSnapshot.PermanentInventory.ContainerId,
                        "permanent_inventory",
                        0,
                        string.Empty,
                        Guid.Empty),
                    CreateItem("medical.field_dressing", 1),
                    medicalSlotLocation,
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
                    CreateItem("ammunition.training_556", 1),
                    new InventoryItemLocation(
                        InventoryItemLocationKind.Container,
                        swapHardCapState.FullSnapshot.PermanentInventory.ContainerId,
                        "permanent_inventory",
                        0,
                        string.Empty,
                        Guid.Empty),
                    CreateItem("weapon.training_rifle", 1),
                    corpseContainer,
                    corpseContainer.Slots[0],
                    out var externalSwapCapReason),
                Is.False);
            Assert.That(externalSwapCapReason, Does.Contain("140 percent"));
            Assert.That(
                InventoryTargetAdvisor.CanSwapWithExternalContainer(
                    hardCapState,
                    CreateItem("weapon.training_rifle", 1),
                    permanentLocation,
                    CreateItem("ammunition.training_556", 1),
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
                sprintEligible = true,
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

        private InventoryClientState CreateAdvisorState(
            long carriedWeight,
            long carryCapacity,
            bool bagHasContents = true)
        {
            var characterId = Guid.NewGuid();
            var equippedBagItem = CreateItem("bag.field_pack", 1);
            var bagContents = new InventoryContainer(
                Guid.NewGuid(),
                "bag_contents",
                1,
                7,
                new[]
                {
                    new InventorySlot(4, "specialized", new[] { "medical" }, null),
                    new InventorySlot(
                        5,
                        "specialized",
                        new[] { "material" },
                        bagHasContents ? CreateItem("material.iron_ore", 1) : null),
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
                carriedWeight <= carryCapacity,
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
                carriedWeight <= carryCapacity,
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
