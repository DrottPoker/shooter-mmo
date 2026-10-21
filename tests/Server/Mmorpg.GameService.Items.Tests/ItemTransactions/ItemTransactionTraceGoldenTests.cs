using Mmorpg.GameService.Items;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Mmorpg.WorldData.Items;
using static Mmorpg.GameService.Items.Tests.ItemTransactions.ItemScenarioWorld;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// Pins the exact statements, transaction boundaries, stored outcome, and audit journal of
// every item command and of the kernel's failure paths. A refactor of the transaction
// kernel must keep every golden file byte-identical.
public sealed class ItemTransactionTraceGoldenTests
{
    private const string Ore = "fixture.ore";
    private const string Potion = "fixture.potion";
    private const string Ring = "fixture.ring";
    private const string Pack = "fixture.pack";
    private const string QuestItem = "fixture.letter";
    private const string Arrow = "fixture.arrow";
    private const string QuestGrantId = "golden_quest";
    private const string Pickaxe = "fixture.pickaxe";
    private const string Vendor = "vendor.fixture_store";

    [PostgresIntegrationFact]
    public async Task InventoryCommandsMatchTheirGoldenTraces()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(context);
        var player = await world.RegisterAsync("golden_inventory");
        var characterId = player.Character.Id;
        var accountId = player.Registration.AccountId;
        var state = await world.LoadStateAsync(characterId);
        var inventoryId = state.PermanentInventoryContainerId;

        var grantId = Guid.NewGuid();
        var grant = await traced.AssertGoldenAsync("grant.success", grantId, service => service.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                grantId,
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(characterId, state.Revision, Ore, 10, inventoryId, 0)),
            CancellationToken.None));
        AssertCommitted(grant);
        var oreId = Assert.Single(grant.ItemRevisions).ItemInstanceId;

        state = await world.LoadStateAsync(characterId);
        var questGrantId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("grant.quest_lineage", questGrantId, service => service.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                questGrantId,
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    characterId,
                    state.Revision,
                    QuestItem,
                    1,
                    inventoryId,
                    1,
                    ItemPolicySourceKinds.QuestGrant,
                    QuestGrantId)),
            CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        var questReplayId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("grant.quest_already_satisfied", questReplayId, service => service.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                questReplayId,
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    characterId,
                    state.Revision,
                    QuestItem,
                    1,
                    inventoryId,
                    2,
                    ItemPolicySourceKinds.QuestGrant,
                    QuestGrantId)),
            CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        var ore = await world.LoadItemAsync(oreId);
        var relocateId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("relocate.offline_account", relocateId, service => service.ExecuteAsync(
            new ItemTransactionRequest<RelocateItemCommand>(
                relocateId,
                ItemTransactionActor.ForOfflineAccount(accountId),
                new RelocateItemCommand(characterId, state.Revision, oreId, ore.Revision, inventoryId, 5)),
            CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        ore = await world.LoadItemAsync(oreId);
        var splitId = Guid.NewGuid();
        var split = await traced.AssertGoldenAsync("split_stack.account", splitId, service => service.ExecuteAsync(
            new ItemTransactionRequest<SplitItemStackCommand>(
                splitId,
                ItemTransactionActor.ForAccount(accountId),
                new SplitItemStackCommand(characterId, state.Revision, oreId, ore.Revision, 3, inventoryId, 6)),
            CancellationToken.None));
        AssertCommitted(split);
        var splitOreId = await world.FindItemAsync(inventoryId, 6);

        state = await world.LoadStateAsync(characterId);
        ore = await world.LoadItemAsync(oreId);
        var splitOre = await world.LoadItemAsync(splitOreId);
        var mergeId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("merge_stacks.success", mergeId, service => service.ExecuteAsync(
            new ItemTransactionRequest<MergeItemStacksCommand>(
                mergeId,
                ItemTransactionActor.ForSystem(),
                new MergeItemStacksCommand(
                    characterId,
                    state.Revision,
                    splitOreId,
                    splitOre.Revision,
                    oreId,
                    ore.Revision)),
            CancellationToken.None)));

        var ringId = await world.GrantAsync(characterId, Ring, 1, inventoryId, 2);
        state = await world.LoadStateAsync(characterId);
        ore = await world.LoadItemAsync(oreId);
        var ring = await world.LoadItemAsync(ringId);
        var swapId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("swap_container_items.success", swapId, service => service.ExecuteAsync(
            new ItemTransactionRequest<SwapContainerItemsCommand>(
                swapId,
                ItemTransactionActor.ForSystem(),
                new SwapContainerItemsCommand(
                    characterId,
                    state.Revision,
                    oreId,
                    ore.Revision,
                    ringId,
                    ring.Revision)),
            CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        ore = await world.LoadItemAsync(oreId);
        var consumeId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("consume_quantity.partial", consumeId, service => service.ExecuteAsync(
            new ItemTransactionRequest<ConsumeItemQuantityCommand>(
                consumeId,
                ItemTransactionActor.ForSystem(),
                new ConsumeItemQuantityCommand(characterId, state.Revision, oreId, ore.Revision, 2)),
            CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        ring = await world.LoadItemAsync(ringId);
        var equipId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("equip.success", equipId, service => service.ExecuteAsync(
            new ItemTransactionRequest<EquipItemCommand>(
                equipId,
                ItemTransactionActor.ForSystem(),
                new EquipItemCommand(
                    characterId,
                    state.Revision,
                    ringId,
                    ring.Revision,
                    "ring_1")),
            CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        ring = await world.LoadItemAsync(ringId);
        var unequipId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("unequip.success", unequipId, service => service.ExecuteAsync(
            new ItemTransactionRequest<UnequipItemCommand>(
                unequipId,
                ItemTransactionActor.ForSystem(),
                new UnequipItemCommand(characterId, state.Revision, ringId, ring.Revision, inventoryId, 7)),
            CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        ring = await world.LoadItemAsync(ringId);
        var destroyId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("destroy.success", destroyId, service => service.ExecuteAsync(
            new ItemTransactionRequest<DestroyItemCommand>(
                destroyId,
                ItemTransactionActor.ForSystem(),
                new DestroyItemCommand(characterId, state.Revision, ringId, ring.Revision, "golden_cleanup")),
            CancellationToken.None)));
    }

    [PostgresIntegrationFact]
    public async Task CombatCommandsMatchTheirGoldenTraces()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(context);
        var archer = await world.RegisterAsync("golden_archer");
        var characterId = archer.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var inventoryId = state.PermanentInventoryContainerId;
        var arrowId = await world.GrantAsync(characterId, Arrow, 50, inventoryId, 0);
        await world.EquipAsync(characterId, arrowId, "ammunition");

        state = await world.LoadStateAsync(characterId);
        var arrow = await world.LoadItemAsync(arrowId);
        var partialId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("consume_equipped_ammunition.partial", partialId, service => service.ExecuteAsync(
            new ItemTransactionRequest<ConsumeEquippedAmmunitionCommand>(
                partialId,
                ItemTransactionActor.ForSystem(),
                new ConsumeEquippedAmmunitionCommand(characterId, state.Revision, arrowId, arrow.Revision, 5)),
            CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        arrow = await world.LoadItemAsync(arrowId);
        var exhaustedId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("consume_equipped_ammunition.exhausted", exhaustedId, service => service.ExecuteAsync(
            new ItemTransactionRequest<ConsumeEquippedAmmunitionCommand>(
                exhaustedId,
                ItemTransactionActor.ForSystem(),
                new ConsumeEquippedAmmunitionCommand(
                    characterId,
                    state.Revision,
                    arrowId,
                    arrow.Revision,
                    arrow.Quantity)),
            CancellationToken.None)));

        // A combat death commits the shots the worker had not sent yet and records the killer.
        var pendingArrowId = await world.GrantAsync(characterId, Arrow, 20, inventoryId, 0);
        await world.EquipAsync(characterId, pendingArrowId, "ammunition");
        state = await world.LoadStateAsync(characterId);
        var pendingArrow = await world.LoadItemAsync(pendingArrowId);
        var deathId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("process_player_death.combat", deathId, service => service.ExecuteAsync(
            new ItemTransactionRequest<ProcessPlayerDeathCommand>(
                deathId,
                ItemTransactionActor.ForSystem(),
                CreateDeathCommand(characterId, state.Revision) with
                {
                    AmmunitionConsumption = new DeathAmmunitionConsumption(
                        pendingArrowId,
                        pendingArrow.Revision,
                        3),
                    Killer = new DeathKiller(DeathKillerKinds.Mob, "mob.fixture_wolf")
                }),
            CancellationToken.None)));
    }

    [PostgresIntegrationFact]
    public async Task PolicyAndQuestCommandsMatchTheirGoldenTraces()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(context);
        var player = await world.RegisterAsync("golden_policy");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var inventoryId = state.PermanentInventoryContainerId;
        var protectedRingId = await world.GrantAsync(characterId, Ring, 1, inventoryId, 0);
        var insuredRingId = await world.GrantAsync(characterId, Ring, 1, inventoryId, 1);
        await world.GrantAsync(
            characterId,
            QuestItem,
            1,
            inventoryId,
            2,
            ItemPolicySourceKinds.QuestGrant,
            QuestGrantId);

        state = await world.LoadStateAsync(characterId);
        var protectedRing = await world.LoadItemAsync(protectedRingId);
        var applyId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("apply_item_policy.protected", applyId, service => service.ExecuteAsync(
            new ItemTransactionRequest<ApplyItemPolicyCommand>(
                applyId,
                ItemTransactionActor.ForSystem(),
                new ApplyItemPolicyCommand(
                    characterId,
                    state.Revision,
                    protectedRingId,
                    protectedRing.Revision,
                    ItemPolicyIds.ProtectedOnDeath,
                    "integration_fixture",
                    "golden")),
            CancellationToken.None)));

        await world.ApplyPolicyAsync(characterId, insuredRingId, ItemPolicyIds.Insured);
        state = await world.LoadStateAsync(characterId);
        var insuredRing = await world.LoadItemAsync(insuredRingId);
        var removeId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("remove_insurance_policy.success", removeId, service => service.ExecuteAsync(
            new ItemTransactionRequest<RemoveInsurancePolicyCommand>(
                removeId,
                ItemTransactionActor.ForSystem(),
                new RemoveInsurancePolicyCommand(characterId, state.Revision, insuredRingId, insuredRing.Revision)),
            CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        var abandonId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("abandon_quest_items.success", abandonId, service => service.ExecuteAsync(
            new ItemTransactionRequest<AbandonQuestItemsCommand>(
                abandonId,
                ItemTransactionActor.ForSystem(),
                new AbandonQuestItemsCommand(characterId, state.Revision, QuestGrantId)),
            CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        var abandonAgainId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("abandon_quest_items.already_absent", abandonAgainId, service => service.ExecuteAsync(
            new ItemTransactionRequest<AbandonQuestItemsCommand>(
                abandonAgainId,
                ItemTransactionActor.ForSystem(),
                new AbandonQuestItemsCommand(characterId, state.Revision, QuestGrantId)),
            CancellationToken.None)));

        // NPC insurance charges character currency through the Characters wallet.
        await world.ExecuteSqlAsync(
            "update characters set currency = 250 where id = @CharacterId;",
            new { CharacterId = characterId });
        var npcRingId = await world.GrantAsync(characterId, Ring, 1, inventoryId, 3);
        var session = await world.JoinAsync(player);
        state = await world.LoadStateAsync(characterId);
        var npcRing = await world.LoadItemAsync(npcRingId);
        var insureId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("apply_item_policy.insurance_charge", insureId, service => service.ExecuteAsync(
            new ItemTransactionRequest<ApplyItemPolicyCommand>(
                insureId,
                SimulationActor(session, new ItemTransactionLiveAccess(false, false, true)),
                new ApplyItemPolicyCommand(
                    characterId,
                    state.Revision,
                    npcRingId,
                    npcRing.Revision,
                    ItemPolicyIds.Insured,
                    ItemPolicySourceKinds.InsuranceService,
                    "golden_npc",
                    100)),
            CancellationToken.None)));
    }

    [PostgresIntegrationFact]
    public async Task BagRecoveryAndSecureTierCommandsMatchTheirGoldenTraces()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(context);
        var first = await world.RegisterAsync("golden_bag_first");
        var second = await world.RegisterAsync("golden_bag_second");
        var firstState = await world.LoadStateAsync(first.Character.Id);
        var secondState = await world.LoadStateAsync(second.Character.Id);
        var firstBagId = await world.GrantAsync(
            first.Character.Id,
            Pack,
            1,
            firstState.PermanentInventoryContainerId,
            0);
        var secondBagId = await world.GrantAsync(
            second.Character.Id,
            Pack,
            1,
            secondState.PermanentInventoryContainerId,
            0);
        await world.EquipAsync(first.Character.Id, firstBagId, ItemEquipmentSlotIds.Bag);
        await world.EquipAsync(second.Character.Id, secondBagId, ItemEquipmentSlotIds.Bag);

        firstState = await world.LoadStateAsync(first.Character.Id);
        secondState = await world.LoadStateAsync(second.Character.Id);
        var firstBag = await world.LoadItemAsync(firstBagId);
        var secondBag = await world.LoadItemAsync(secondBagId);
        var bagSwapId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("swap_bag_aggregates.success", bagSwapId, service => service.ExecuteAsync(
            new ItemTransactionRequest<SwapBagAggregatesCommand>(
                bagSwapId,
                ItemTransactionActor.ForSystem(),
                new SwapBagAggregatesCommand(
                    first.Character.Id,
                    firstState.Revision,
                    firstBagId,
                    firstBag.Revision,
                    firstBag.BagContentsRevision!.Value,
                    second.Character.Id,
                    secondState.Revision,
                    secondBagId,
                    secondBag.Revision,
                    secondBag.BagContentsRevision!.Value)),
            CancellationToken.None)));

        var characterId = first.Character.Id;
        var inventoryId = firstState.PermanentInventoryContainerId;
        var oreId = await world.GrantAsync(characterId, Ore, 4, inventoryId, 1);
        var state = await world.LoadStateAsync(characterId);
        var ore = await world.LoadItemAsync(oreId);
        var addId = Guid.NewGuid();
        var add = await traced.AssertGoldenAsync("add_recovery_delivery.success", addId, service => service.ExecuteAsync(
            new ItemTransactionRequest<AddRecoveryDeliveryCommand>(
                addId,
                ItemTransactionActor.ForSystem(),
                new AddRecoveryDeliveryCommand(
                    characterId,
                    state.Revision,
                    "golden_source",
                    "golden-event-1",
                    null,
                    null,
                    [new ItemRevisionExpectation(oreId, ore.Revision)])),
            CancellationToken.None));
        AssertCommitted(add);
        var deliveryId = Assert.Single(add.RecoveryDeliveryIds);

        state = await world.LoadStateAsync(characterId);
        ore = await world.LoadItemAsync(oreId);
        var claimId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("claim_recovery_delivery.success", claimId, service => service.ExecuteAsync(
            new ItemTransactionRequest<ClaimRecoveryDeliveryCommand>(
                claimId,
                ItemTransactionActor.ForSystem(),
                new ClaimRecoveryDeliveryCommand(
                    characterId,
                    state.Revision,
                    deliveryId,
                    0,
                    inventoryId,
                    [new ItemRevisionExpectation(oreId, ore.Revision)])),
            CancellationToken.None)));

        var expiringOreId = await world.GrantAsync(characterId, Ore, 2, inventoryId, 2);
        var expiringDeliveryId = await world.AddRecoveryDeliveryAsync(
            characterId,
            expiringOreId,
            DateTime.UtcNow.AddMinutes(5));
        await world.ExecuteSqlAsync(
            """
            update recovery_deliveries
            set created_at = now() - interval '2 seconds',
                expires_at = now() - interval '1 second'
            where id = @DeliveryId;
            """,
            new { DeliveryId = expiringDeliveryId });
        var expireDeliveryId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("expire_recovery_delivery.success", expireDeliveryId, service => service.ExecuteAsync(
            new ItemTransactionRequest<ExpireRecoveryDeliveryCommand>(
                expireDeliveryId,
                ItemTransactionActor.ForSystem(),
                new ExpireRecoveryDeliveryCommand(expiringDeliveryId)),
            CancellationToken.None)));

        await world.AddSecureTierAsync("secure_container.golden_six", 6);
        state = await world.LoadStateAsync(characterId);
        var expandId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("change_secure_container_tier.expand", expandId, service => service.ExecuteAsync(
            new ItemTransactionRequest<ChangeSecureContainerTierCommand>(
                expandId,
                ItemTransactionActor.ForSystem(),
                new ChangeSecureContainerTierCommand(
                    first.Registration.AccountId,
                    0,
                    "secure_container.golden_six",
                    [new CharacterRevisionExpectation(characterId, state.Revision)])),
            CancellationToken.None)));

        await world.GrantAsync(characterId, Ore, 1, state.SecureContainerId, 5);
        var reduceId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("change_secure_container_tier.reduce_with_overflow", reduceId, service => service.ExecuteAsync(
            new ItemTransactionRequest<ChangeSecureContainerTierCommand>(
                reduceId,
                ItemTransactionActor.ForSystem(),
                new ChangeSecureContainerTierCommand(
                    first.Registration.AccountId,
                    1,
                    "secure_container.base",
                    [])),
            CancellationToken.None)));

        var unchangedId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("change_secure_container_tier.unchanged", unchangedId, service => service.ExecuteAsync(
            new ItemTransactionRequest<ChangeSecureContainerTierCommand>(
                unchangedId,
                ItemTransactionActor.ForSystem(),
                new ChangeSecureContainerTierCommand(
                    first.Registration.AccountId,
                    2,
                    "secure_container.base",
                    [])),
            CancellationToken.None)));
    }

    [PostgresIntegrationFact]
    public async Task DeathAndCorpseCommandsMatchTheirGoldenTraces()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(context);
        var victim = await world.RegisterAsync("golden_victim");
        var looter = await world.RegisterAsync("golden_looter");
        var victimId = victim.Character.Id;
        var looterId = looter.Character.Id;
        var victimState = await world.LoadStateAsync(victimId);
        var looterState = await world.LoadStateAsync(looterId);
        var looterInventoryId = looterState.PermanentInventoryContainerId;
        var oreId = await world.GrantAsync(victimId, Ore, 10, victimState.PermanentInventoryContainerId, 0);
        await world.GrantAsync(victimId, QuestItem, 1, victimState.PermanentInventoryContainerId, 1);
        var ringId = await world.GrantAsync(victimId, Ring, 1, victimState.PermanentInventoryContainerId, 2);
        await world.EquipAsync(victimId, ringId, "ring_1");

        victimState = await world.LoadStateAsync(victimId);
        var deathCommand = CreateDeathCommand(victimId, victimState.Revision);
        var deathId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("process_player_death.success", deathId, service => service.ExecuteAsync(
            new ItemTransactionRequest<ProcessPlayerDeathCommand>(
                deathId,
                ItemTransactionActor.ForSystem(),
                deathCommand),
            CancellationToken.None)));

        var deathReplayId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("process_player_death.event_replayed", deathReplayId, service => service.ExecuteAsync(
            new ItemTransactionRequest<ProcessPlayerDeathCommand>(
                deathReplayId,
                ItemTransactionActor.ForSystem(),
                deathCommand),
            CancellationToken.None)));

        var corpseId = (await world.LoadItemAsync(oreId)).ContainerId!.Value;
        corpseId = await FindCorpseAsync(world, corpseId);
        var sections = await world.LoadCorpseSectionsAsync(corpseId);

        var ore = await world.LoadItemAsync(oreId);
        var lootPartialId = Guid.NewGuid();
        var lootPartialDestinationRevision = await world.LoadContainerRevisionAsync(looterInventoryId);
        AssertCommitted(await traced.AssertGoldenAsync("loot_corpse_partial_stack.success", lootPartialId, service => service.ExecuteAsync(
            new ItemTransactionRequest<LootCorpsePartialStackCommand>(
                lootPartialId,
                ItemTransactionActor.ForSystem(),
                new LootCorpsePartialStackCommand(
                    looterId,
                    corpseId,
                    oreId,
                    ore.Revision,
                    3,
                    looterInventoryId,
                    lootPartialDestinationRevision,
                    0,
                    null,
                    null)),
            CancellationToken.None)));
        var lootedOreId = await world.FindItemAsync(looterInventoryId, 0);

        var ring = await world.LoadItemAsync(ringId);
        var lootId = Guid.NewGuid();
        var lootDestinationRevision = await world.LoadContainerRevisionAsync(looterInventoryId);
        AssertCommitted(await traced.AssertGoldenAsync("loot_corpse_item.success", lootId, service => service.ExecuteAsync(
            new ItemTransactionRequest<LootCorpseItemCommand>(
                lootId,
                ItemTransactionActor.ForSystem(),
                new LootCorpseItemCommand(
                    looterId,
                    corpseId,
                    ringId,
                    ring.Revision,
                    looterInventoryId,
                    lootDestinationRevision,
                    1,
                    null,
                    null)),
            CancellationToken.None)));

        var lootedOre = await world.LoadItemAsync(lootedOreId);
        var depositPartialId = Guid.NewGuid();
        var depositPartialDestinationRevision = await world.LoadContainerRevisionAsync(sections.GeneralContainerId);
        AssertCommitted(await traced.AssertGoldenAsync("deposit_corpse_partial_stack.success", depositPartialId, service => service.ExecuteAsync(
            new ItemTransactionRequest<DepositCorpsePartialStackCommand>(
                depositPartialId,
                ItemTransactionActor.ForSystem(),
                new DepositCorpsePartialStackCommand(
                    looterId,
                    corpseId,
                    lootedOreId,
                    lootedOre.Revision,
                    1,
                    sections.GeneralContainerId,
                    depositPartialDestinationRevision,
                    3,
                    null,
                    null)),
            CancellationToken.None)));

        ring = await world.LoadItemAsync(ringId);
        var depositId = Guid.NewGuid();
        var depositDestinationRevision = await world.LoadContainerRevisionAsync(sections.GeneralContainerId);
        AssertCommitted(await traced.AssertGoldenAsync("deposit_corpse_item.success", depositId, service => service.ExecuteAsync(
            new ItemTransactionRequest<DepositCorpseItemCommand>(
                depositId,
                ItemTransactionActor.ForSystem(),
                new DepositCorpseItemCommand(
                    looterId,
                    corpseId,
                    ringId,
                    ring.Revision,
                    sections.GeneralContainerId,
                    depositDestinationRevision,
                    4,
                    null,
                    null)),
            CancellationToken.None)));

        ore = await world.LoadItemAsync(oreId);
        var movePartialId = Guid.NewGuid();
        var movePartialDestinationRevision = await world.LoadContainerRevisionAsync(sections.GeneralContainerId);
        AssertCommitted(await traced.AssertGoldenAsync("move_corpse_partial_stack.success", movePartialId, service => service.ExecuteAsync(
            new ItemTransactionRequest<MoveCorpsePartialStackCommand>(
                movePartialId,
                ItemTransactionActor.ForSystem(),
                new MoveCorpsePartialStackCommand(
                    looterId,
                    corpseId,
                    oreId,
                    ore.Revision,
                    2,
                    sections.GeneralContainerId,
                    movePartialDestinationRevision,
                    5,
                    null,
                    null)),
            CancellationToken.None)));

        ring = await world.LoadItemAsync(ringId);
        var moveId = Guid.NewGuid();
        var moveDestinationRevision = await world.LoadContainerRevisionAsync(sections.GeneralContainerId);
        AssertCommitted(await traced.AssertGoldenAsync("move_corpse_item.success", moveId, service => service.ExecuteAsync(
            new ItemTransactionRequest<MoveCorpseItemCommand>(
                moveId,
                ItemTransactionActor.ForSystem(),
                new MoveCorpseItemCommand(
                    looterId,
                    corpseId,
                    ringId,
                    ring.Revision,
                    sections.GeneralContainerId,
                    moveDestinationRevision,
                    6,
                    null,
                    null)),
            CancellationToken.None)));

        await world.ExecuteSqlAsync(
            """
            update corpses
            set created_at = now() - interval '6 minutes',
                expires_at = now() - interval '1 minute'
            where id = @CorpseId;
            """,
            new { CorpseId = corpseId });
        AssertCommitted(await traced.AssertGoldenAsync("expire_corpse.success", sections.ExpiryOperationId, service => service.ExecuteAsync(
            new ItemTransactionRequest<ExpireCorpseCommand>(
                sections.ExpiryOperationId,
                ItemTransactionActor.ForSystem(),
                new ExpireCorpseCommand(corpseId)),
            CancellationToken.None)));
    }

    [PostgresIntegrationFact]
    public async Task CorpseBagSwapMatchesItsGoldenTrace()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(context);
        var player = await world.RegisterAsync("golden_corpse_bag");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var inventoryId = state.PermanentInventoryContainerId;
        var lostBagId = await world.GrantAsync(characterId, Pack, 1, inventoryId, 0);
        await world.EquipAsync(characterId, lostBagId, ItemEquipmentSlotIds.Bag);
        var corpseId = await world.ProcessDeathAsync(characterId);
        var newBagId = await world.GrantAsync(characterId, Pack, 1, inventoryId, 0);
        await world.EquipAsync(characterId, newBagId, ItemEquipmentSlotIds.Bag);

        var lostBag = await world.LoadItemAsync(lostBagId);
        var newBag = await world.LoadItemAsync(newBagId);
        var swapId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("swap_corpse_bag.success", swapId, service => service.ExecuteAsync(
            new ItemTransactionRequest<SwapCorpseBagCommand>(
                swapId,
                ItemTransactionActor.ForSystem(),
                new SwapCorpseBagCommand(
                    characterId,
                    corpseId,
                    lostBagId,
                    lostBag.Revision,
                    lostBag.BagContentsContainerId!.Value,
                    lostBag.BagContentsRevision!.Value,
                    newBagId,
                    newBag.Revision,
                    newBag.BagContentsContainerId!.Value,
                    newBag.BagContentsRevision!.Value)),
            CancellationToken.None)));
    }

    [PostgresIntegrationFact]
    public async Task EquipSwapMatchesItsGoldenTrace()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(context);
        var player = await world.RegisterAsync("golden_equip_swap");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var inventoryId = state.PermanentInventoryContainerId;
        var equippedRingId = await world.GrantAsync(characterId, Ring, 1, inventoryId, 0);
        await world.EquipAsync(characterId, equippedRingId, "ring_1");
        var newRingId = await world.GrantAsync(characterId, Ring, 1, inventoryId, 4);

        state = await world.LoadStateAsync(characterId);
        var newRing = await world.LoadItemAsync(newRingId);
        var swapId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("equip.swap", swapId, service => service.ExecuteAsync(
            new ItemTransactionRequest<EquipItemCommand>(
                swapId,
                ItemTransactionActor.ForSystem(),
                new EquipItemCommand(characterId, state.Revision, newRingId, newRing.Revision, "ring_1")),
            CancellationToken.None)));
    }

    [PostgresIntegrationFact]
    public async Task CorpseLootToEquipmentSwapMatchesItsGoldenTrace()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(context);
        var source = await world.RegisterAsync("golden_equipment_corpse");
        var looter = await world.RegisterAsync("golden_equipment_looter");
        var sourceInventoryId = (await world.LoadStateAsync(source.Character.Id)).PermanentInventoryContainerId;
        var corpseRingId = await world.GrantAsync(source.Character.Id, Ring, 1, sourceInventoryId, 0);
        var corpseId = await world.ProcessDeathAsync(source.Character.Id);
        var looterId = looter.Character.Id;
        var looterInventoryId = (await world.LoadStateAsync(looterId)).PermanentInventoryContainerId;
        var equippedRingId = await world.GrantAsync(looterId, Ring, 1, looterInventoryId, 0);
        await world.EquipAsync(looterId, equippedRingId, "ring_1");

        var corpseRing = await world.LoadItemAsync(corpseRingId);
        var equippedRing = await world.LoadItemAsync(equippedRingId);
        var lootId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("loot_corpse_item_to_equipment.swap", lootId, service => service.ExecuteAsync(
            new ItemTransactionRequest<LootCorpseItemToEquipmentCommand>(
                lootId,
                ItemTransactionActor.ForSystem(),
                new LootCorpseItemToEquipmentCommand(
                    looterId,
                    corpseId,
                    corpseRingId,
                    corpseRing.Revision,
                    "ring_1",
                    equippedRingId,
                    equippedRing.Revision)),
            CancellationToken.None)));
    }

    [PostgresIntegrationFact]
    public async Task MobCorpseCommandsMatchTheirGoldenTraces()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(context);
        var player = await world.RegisterAsync("golden_mob_looter");
        var characterId = player.Character.Id;

        var createId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("create_persistent_mob_corpse.success", createId, service => service.ExecuteAsync(
            new ItemTransactionRequest<CreatePersistentMobCorpseCommand>(
                createId,
                ItemTransactionActor.ForSystem(),
                new CreatePersistentMobCorpseCommand(
                    Guid.NewGuid(),
                    WorkerId,
                    WorkerRuntimeId,
                    ShardId,
                    "mob.feral_wolf",
                    "Feral Wolf",
                    4d,
                    0d,
                    -2.5d,
                    0d,
                    0d,
                    0d,
                    1d,
                    MobCorpsePresentationKey,
                    300d,
                    30,
                    [
                        new MobCorpseLootEntryRequest(Guid.NewGuid(), Ore, 3),
                        new MobCorpseLootEntryRequest(Guid.NewGuid(), Potion, 2)
                    ])),
            CancellationToken.None)));

        var session = await world.JoinAsync(player);
        var state = await world.LoadStateAsync(characterId);
        var grantId = Guid.NewGuid();
        var mobLootDestinationRevision = await world.LoadContainerRevisionAsync(state.PermanentInventoryContainerId);
        AssertCommitted(await traced.AssertGoldenAsync("grant_mob_loot.success", grantId, service => service.ExecuteAsync(
            new ItemTransactionRequest<GrantMobLootCommand>(
                grantId,
                SimulationActor(session),
                new GrantMobLootCommand(
                    characterId,
                    state.Revision,
                    grantId,
                    Guid.NewGuid(),
                    "mob.feral_wolf",
                    Ore,
                    2,
                    state.PermanentInventoryContainerId,
                    mobLootDestinationRevision,
                    0)),
            CancellationToken.None)));
    }

    [PostgresIntegrationFact]
    public async Task KernelPathsMatchTheirGoldenTraces()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(context);
        var player = await world.RegisterAsync("golden_kernel");
        var characterId = player.Character.Id;
        var accountId = player.Registration.AccountId;
        var state = await world.LoadStateAsync(characterId);
        var inventoryId = state.PermanentInventoryContainerId;
        var oreId = await world.GrantAsync(characterId, Ore, 5, inventoryId, 0);

        // Live simulation authority.
        var session = await world.JoinAsync(player);
        state = await world.LoadStateAsync(characterId);
        var ore = await world.LoadItemAsync(oreId);
        var liveRequest = new ItemTransactionRequest<RelocateItemCommand>(
            Guid.NewGuid(),
            SimulationActor(session),
            new RelocateItemCommand(characterId, state.Revision, oreId, ore.Revision, inventoryId, 1));
        AssertCommitted(await traced.AssertGoldenAsync("relocate.simulation_worker", liveRequest.OperationId, service =>
            service.ExecuteAsync(liveRequest, CancellationToken.None)));

        await world.ExecuteSqlAsync(
            "update character_simulation_sessions set released_at = now() where id = @SessionId;",
            new { SessionId = session.SimulationSessionId });
        AssertCommitted(await traced.AssertGoldenAsync("kernel.live_authority_rejected_committed_replay", liveRequest.OperationId, service =>
            service.ExecuteAsync(liveRequest, CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        ore = await world.LoadItemAsync(oreId);
        var releasedRequest = liveRequest with
        {
            OperationId = Guid.NewGuid(),
            Command = new RelocateItemCommand(characterId, state.Revision, oreId, ore.Revision, inventoryId, 2)
        };
        AssertRejected(
            await traced.AssertGoldenAsync("kernel.live_authority_rejected_no_replay", releasedRequest.OperationId, service =>
                service.ExecuteAsync(releasedRequest, CancellationToken.None)),
            ItemTransactionErrorCodes.SimulationSessionInvalid);

        // Operation claims.
        state = await world.LoadStateAsync(characterId);
        var grantRequest = new ItemTransactionRequest<GrantItemCommand>(
            Guid.NewGuid(),
            ItemTransactionActor.ForSystem(),
            new GrantItemCommand(characterId, state.Revision, Ore, 1, inventoryId, 3));
        AssertCommitted(await context.ItemTransactionService.ExecuteAsync(grantRequest, CancellationToken.None));
        AssertCommitted(await traced.AssertGoldenAsync("kernel.claim_replay_committed", grantRequest.OperationId, service =>
            service.ExecuteAsync(grantRequest, CancellationToken.None)));
        AssertRejected(
            await traced.AssertGoldenAsync("kernel.claim_hash_conflict", grantRequest.OperationId, service =>
                service.ExecuteAsync(
                    grantRequest with { Command = grantRequest.Command with { Quantity = 2 } },
                    CancellationToken.None)),
            ItemTransactionErrorCodes.ItemOperationConflict);

        state = await world.LoadStateAsync(characterId);
        ore = await world.LoadItemAsync(oreId);
        var staleRequest = new ItemTransactionRequest<RelocateItemCommand>(
            Guid.NewGuid(),
            ItemTransactionActor.ForOfflineAccount(accountId),
            new RelocateItemCommand(characterId, state.Revision, oreId, ore.Revision + 7, inventoryId, 4));
        AssertRejected(
            await traced.AssertGoldenAsync("kernel.stored_rejection_account", staleRequest.OperationId, service =>
                service.ExecuteAsync(staleRequest, CancellationToken.None)),
            ItemTransactionErrorCodes.ItemStateConflict);
        AssertRejected(
            await traced.AssertGoldenAsync("kernel.claim_replay_stored_rejection", staleRequest.OperationId, service =>
                service.ExecuteAsync(staleRequest, CancellationToken.None)),
            ItemTransactionErrorCodes.ItemStateConflict);

        await world.ExecuteSqlAsync(
            """
            update item_operations
            set status = 'pending',
                result_payload = null,
                completed_at = null
            where operation_id = @OperationId;
            """,
            new { staleRequest.OperationId });
        AssertRejected(
            await traced.AssertGoldenAsync("kernel.claim_pending", staleRequest.OperationId, service =>
                service.ExecuteAsync(staleRequest, CancellationToken.None)),
            ItemTransactionErrorCodes.ItemOperationConflict);

        // A retried system operation releases its claim when it is rejected.
        var missingCorpseId = Guid.NewGuid();
        AssertRejected(
            await traced.AssertGoldenAsync("kernel.retried_system_rejection_released", missingCorpseId, service =>
                service.ExecuteAsync(
                    new ItemTransactionRequest<ExpireCorpseCommand>(
                        missingCorpseId,
                        ItemTransactionActor.ForSystem(),
                        new ExpireCorpseCommand(missingCorpseId)),
                    CancellationToken.None)),
            ItemTransactionErrorCodes.CorpseNotFound);

        // The finalizer rejects a grant that would exceed the carry-weight hard cap.
        await world.GrantAsync(characterId, Ore, 40, inventoryId, 5);
        state = await world.LoadStateAsync(characterId);
        var heavyGrantId = Guid.NewGuid();
        AssertRejected(
            await traced.AssertGoldenAsync("kernel.finalizer_hard_cap", heavyGrantId, service =>
                service.ExecuteAsync(
                    new ItemTransactionRequest<GrantItemCommand>(
                        heavyGrantId,
                        ItemTransactionActor.ForSystem(),
                        new GrantItemCommand(characterId, state.Revision, Ore, 10, inventoryId, 6)),
                    CancellationToken.None)),
            ItemTransactionErrorCodes.CarryWeightLimitExceeded);

        // Admission rejections never reach the database.
        var derivedId = Guid.Parse("8f14e45f-ceea-567a-9a6f-b3f0a2c4d1e8");
        AssertRejected(
            await traced.AssertGoldenAsync("kernel.operation_id_invalid", derivedId, service =>
                service.ExecuteAsync(
                    new ItemTransactionRequest<GrantItemCommand>(
                        derivedId,
                        ItemTransactionActor.ForAccount(accountId),
                        new GrantItemCommand(characterId, state.Revision, Ore, 1, inventoryId, 7)),
                    CancellationToken.None)),
            ItemTransactionErrorCodes.ItemOperationIdInvalid);
    }

    [PostgresIntegrationFact]
    public async Task VendorAndGatheringCommandsMatchTheirGoldenTraces()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(context);
        var player = await world.RegisterAsync("golden_economy");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var inventoryId = state.PermanentInventoryContainerId;
        await world.ExecuteSqlAsync(
            "update characters set currency = 500 where id = @CharacterId;",
            new { CharacterId = characterId });
        var pickaxeId = await world.GrantAsync(characterId, Pickaxe, 1, inventoryId, 0);
        await world.EquipAsync(characterId, pickaxeId, ItemEquipmentSlotIds.Tool);
        var oreId = await world.GrantAsync(characterId, Ore, 3, inventoryId, 1);
        var session = await world.JoinAsync(player);
        var vendorAccess = new ItemTransactionLiveAccess(false, false, false, false, Vendor);

        state = await world.LoadStateAsync(characterId);
        var purchaseId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("vendor_purchase.success", purchaseId, service => service.ExecuteAsync(
            new ItemTransactionRequest<PurchaseFromVendorCommand>(
                purchaseId,
                SimulationActor(session, vendorAccess),
                new PurchaseFromVendorCommand(characterId, state.Revision, Vendor, Potion, 2, 50)),
            CancellationToken.None)));

        var gatherId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("gather_resource.success", gatherId, service => service.ExecuteAsync(
            new ItemTransactionRequest<GatherResourceCommand>(
                gatherId,
                SimulationActor(session),
                new GatherResourceCommand(characterId, "resource.golden", "golden.node:0000", "pickaxe", Ore, 1)),
            CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        var ore = await world.LoadItemAsync(oreId);
        var partialSaleId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("vendor_sale.partial_stack", partialSaleId, service => service.ExecuteAsync(
            new ItemTransactionRequest<SellToVendorCommand>(
                partialSaleId,
                SimulationActor(session, vendorAccess),
                new SellToVendorCommand(characterId, state.Revision, Vendor, oreId, ore.Revision, 2, 8)),
            CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        ore = await world.LoadItemAsync(oreId);
        var saleId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("vendor_sale.success", saleId, service => service.ExecuteAsync(
            new ItemTransactionRequest<SellToVendorCommand>(
                saleId,
                SimulationActor(session, vendorAccess),
                new SellToVendorCommand(characterId, state.Revision, Vendor, oreId, ore.Revision, 2, 8)),
            CancellationToken.None)));
    }

    [PostgresIntegrationFact]
    public async Task LimitPathsMatchTheirGoldenTraces()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var options = ItemOperationsOptions.CreateDefaults() with
        {
            TransactionTimeout = TimeSpan.FromSeconds(2),
            LockTimeout = TimeSpan.FromMilliseconds(100),
            MaximumCommandPayloadBytes = 1024
        };
        await using var traced = TracedItemTransactions.Create(context, options);
        var player = await world.RegisterAsync("golden_limits");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);

        await using (var blocker = await context.DataSource.OpenConnectionAsync())
        {
            await using var blockerTransaction = await blocker.BeginTransactionAsync();
            await using (var command = blocker.CreateCommand())
            {
                command.Transaction = blockerTransaction;
                command.CommandText =
                    "select character_id from character_item_states where character_id = @CharacterId for update;";
                command.Parameters.AddWithValue("CharacterId", characterId);
                await command.ExecuteNonQueryAsync();
            }

            var timeoutId = Guid.NewGuid();
            AssertRejected(
                await traced.AssertGoldenAsync("kernel.lock_timeout", timeoutId, service =>
                    service.ExecuteAsync(
                        new ItemTransactionRequest<GrantItemCommand>(
                            timeoutId,
                            ItemTransactionActor.ForSystem(),
                            new GrantItemCommand(
                                characterId,
                                state.Revision,
                                Ore,
                                1,
                                state.PermanentInventoryContainerId,
                                0)),
                        CancellationToken.None)),
                ItemTransactionErrorCodes.ItemTransactionTimeout);
            await blockerTransaction.RollbackAsync();
        }

        var oversizedId = Guid.NewGuid();
        AssertRejected(
            await traced.AssertGoldenAsync("kernel.payload_too_large", oversizedId, service =>
                service.ExecuteAsync(
                    new ItemTransactionRequest<AddRecoveryDeliveryCommand>(
                        oversizedId,
                        ItemTransactionActor.ForSystem(),
                        new AddRecoveryDeliveryCommand(
                            characterId,
                            state.Revision,
                            "golden_source",
                            new string('x', 2048),
                            null,
                            null,
                            [])),
                    CancellationToken.None)),
            ItemTransactionErrorCodes.ItemCommandPayloadTooLarge);
    }

    private static async Task<Guid> FindCorpseAsync(ItemScenarioWorld world, Guid sectionContainerId)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select corpse_id from corpse_sections where container_id = @ContainerId;";
        command.Parameters.AddWithValue("ContainerId", sectionContainerId);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static void AssertRejected(ItemTransactionResult result, string code)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(code, result.Error!.Code);
    }
}
